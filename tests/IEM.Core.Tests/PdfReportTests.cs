using System.Text;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Evidence;
using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// Checks on the printable report.
/// <para>
/// The PDF cannot be read back as text the way the HTML can, so these work on the file
/// itself: its structure, its page count, and the bytes of the strings it draws. That is
/// enough to catch the failures that actually matter - a document that renders our letters
/// as boxes, one that silently loses a section, or one that resolves a font off the machine
/// it happens to be built on.
/// </para>
/// </summary>
public sealed class PdfReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-pdf-tests", Guid.NewGuid().ToString("N"));

    public PdfReportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over a leftover temp directory.
        }
    }

    private string Render(SessionSnapshot session, string? hash = "abc123", bool valid = true)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.pdf");
        PdfReportBuilder.Write(path, session, hash, valid);
        return path;
    }

    /// <summary>
    /// Every face the report asks for has to come out of the assembly.
    /// <para>
    /// The resolver is the whole reason a font is embedded at all: a build that lost the
    /// resource, or a resolver that quietly fell through to a system font, both produce a
    /// document that looks fine on the machine that made it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(EmbeddedFonts.Sans, false)]
    [InlineData(EmbeddedFonts.Sans, true)]
    [InlineData(EmbeddedFonts.Mono, false)]
    public void Every_face_the_report_uses_is_carried_inside_the_program(string family, bool bold)
    {
        EmbeddedFonts.Install();

        var resolver = PdfSharp.Fonts.GlobalFontSettings.FontResolver;
        Assert.NotNull(resolver);

        var face = resolver.ResolveTypeface(family, bold, false);
        Assert.NotNull(face);

        var bytes = resolver.GetFont(face.FaceName);

        // A TrueType file, not whatever else happened to be at that resource name.
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 100_000, $"{face.FaceName} je premali da bude font");
        Assert.Equal([0x00, 0x01, 0x00, 0x00], bytes[..4]);
    }

    [Fact]
    public void The_report_is_a_pdf_with_the_sections_a_reader_expects()
    {
        var path = Render(Sample(incidentCount: 3));
        var bytes = File.ReadAllBytes(path);

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes[..5]));
        Assert.True(bytes.Length > 20_000, "izveštaj je premali da sadrži ugrađen font");

        var text = ExtractText(path);

        Assert.Contains("Evidencija kvaliteta internet veze", text, StringComparison.Ordinal);
        Assert.Contains("Prekidi", text, StringComparison.Ordinal);
        Assert.Contains("Integritet zapisa", text, StringComparison.Ordinal);
        Assert.Contains("Granice ovog dokumenta", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one failure this whole font arrangement exists to prevent.
    /// <para>
    /// A substituted face renders <c>č ć ž š đ</c> as boxes, and an encoding that cannot
    /// carry them drops them without complaint - "zabeležen" becomes "zabeleen" in a
    /// document being sent to an operator, and nothing in the export says anything went
    /// wrong.
    /// </para>
    /// </summary>
    [Fact]
    public void Our_letters_survive_into_the_document()
    {
        var text = ExtractText(Render(Sample(incidentCount: 2)));

        foreach (var word in new[] { "Kašnjenje", "Prekidi", "zaključak", "Završni", "vaše" })
        {
            Assert.Contains(word, text, StringComparison.Ordinal);
        }

        foreach (var letter in "čćžšđ")
        {
            Assert.Contains(letter, text);
        }
    }

    /// <summary>
    /// A long session has to paginate, and every page has to be numbered - a stack of
    /// unnumbered pages handed across a counter cannot be shown to be complete.
    /// </summary>
    [Fact]
    public void A_long_session_paginates_and_every_page_is_numbered()
    {
        var text = ExtractText(Render(Sample(incidentCount: 40)));

        var pages = CountPages(text);
        Assert.True(pages > 1, "sesija sa 40 prekida mora da pređe na više strana");

        for (var page = 1; page <= pages; page++)
        {
            Assert.Contains($"Strana {page} od {pages}", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The claim the report makes about itself has to hold in the report itself: the same
    /// build over the same session produces the same document. The PDF container regenerates
    /// a few internal identifiers on every write, which is why the document says so rather
    /// than claiming a byte-identical file.
    /// </summary>
    [Fact]
    public void Two_exports_of_one_session_draw_exactly_the_same_thing()
    {
        var session = Sample(incidentCount: 6);

        Assert.Equal(ExtractText(Render(session)), ExtractText(Render(session)));
    }

    [Fact]
    public void A_broken_chain_is_stated_rather_than_softened()
    {
        var text = ExtractText(Render(Sample(incidentCount: 1), hash: null, valid: false));

        Assert.Contains("narušen", text, StringComparison.Ordinal);
        Assert.DoesNotContain("neprekinut", text, StringComparison.Ordinal);
    }

    // ---- Reading the produced file ------------------------------------------

    /// <summary>
    /// Pulls the drawn strings back out of the document.
    /// <para>
    /// Deliberately independent of the code that wrote it: it inflates the content streams
    /// and decodes the glyph codes through the <c>ToUnicode</c> map the file carries, which
    /// is the same route any PDF reader takes. A test that trusted the builder's own
    /// notion of what it drew would pass on a document nobody can read.
    /// </para>
    /// </summary>
    private static string ExtractText(string path)
    {
        var streams = Inflate(File.ReadAllBytes(path));
        var map = ToUnicode(streams);
        var text = new StringBuilder();

        foreach (var stream in streams)
        {
            var content = Encoding.Latin1.GetString(stream);

            if (!content.Contains("Tj", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var run in System.Text.RegularExpressions.Regex.Matches(
                         content, @"<([0-9A-Fa-f\s]*)>\s*Tj|\(((?:[^()\\]|\\.)*)\)\s*Tj"))
            {
                var match = (System.Text.RegularExpressions.Match)run;

                if (match.Groups[1].Success)
                {
                    var hex = System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"\s", string.Empty);

                    for (var i = 0; i + 4 <= hex.Length; i += 4)
                    {
                        text.Append(map.TryGetValue(Convert.ToInt32(hex[i..(i + 4)], 16), out var c) ? c : '�');
                    }
                }
                else
                {
                    // WinAnsi, not Latin-1: reading it as the latter turns a correctly
                    // encoded ž into an invisible control character and makes a healthy
                    // document look like it dropped every diacritic.
                    text.Append(Encoding.GetEncoding(1252).GetString(
                        Encoding.Latin1.GetBytes(match.Groups[2].Value.Replace("\\", string.Empty, StringComparison.Ordinal))));
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    private static int CountPages(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"Strana \d+ od (\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static List<byte[]> Inflate(byte[] data)
    {
        var streams = new List<byte[]>();
        var text = Encoding.Latin1.GetString(data);

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, @"[^d]stream\r?\n"))
        {
            var start = match.Index + match.Length;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);

            if (end < 0)
            {
                continue;
            }

            var raw = Encoding.Latin1.GetBytes(text[start..end]);

            try
            {
                using var input = new MemoryStream(raw);
                using var inflater = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var output = new MemoryStream();

                inflater.CopyTo(output);
                streams.Add(output.ToArray());
            }
            catch (InvalidDataException)
            {
                streams.Add(raw);
            }
        }

        return streams;
    }

    private static Dictionary<int, char> ToUnicode(List<byte[]> streams)
    {
        var map = new Dictionary<int, char>();

        foreach (var stream in streams)
        {
            var text = Encoding.Latin1.GetString(stream);

            foreach (System.Text.RegularExpressions.Match block in
                     System.Text.RegularExpressions.Regex.Matches(text, @"beginbfchar(.*?)endbfchar",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                foreach (System.Text.RegularExpressions.Match pair in
                         System.Text.RegularExpressions.Regex.Matches(
                             block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    map[Convert.ToInt32(pair.Groups[1].Value, 16)] =
                        (char)Convert.ToInt32(pair.Groups[2].Value[..4], 16);
                }
            }

            foreach (System.Text.RegularExpressions.Match block in
                     System.Text.RegularExpressions.Regex.Matches(text, @"beginbfrange(.*?)endbfrange",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                foreach (System.Text.RegularExpressions.Match range in
                         System.Text.RegularExpressions.Regex.Matches(
                             block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    var first = Convert.ToInt32(range.Groups[1].Value, 16);
                    var last = Convert.ToInt32(range.Groups[2].Value, 16);
                    var target = Convert.ToInt32(range.Groups[3].Value[..4], 16);

                    for (var code = first; code <= last; code++)
                    {
                        map[code] = (char)(target + code - first);
                    }
                }
            }
        }

        return map;
    }

    // ---- Fixture ------------------------------------------------------------

    /// <summary>
    /// A session shaped like a real one: wireless, with gaps, traces and every incident
    /// flag, so the sections that only appear under those conditions are actually drawn.
    /// </summary>
    private static SessionSnapshot Sample(int incidentCount)
    {
        var start = new DateTimeOffset(2026, 8, 12, 19, 40, 12, TimeSpan.FromHours(2));

        NetworkState[] states =
        [
            NetworkState.CpeUpstreamUnreachable,
            NetworkState.GatewayDown,
            NetworkState.WifiRadioDown,
            NetworkState.DnsIspFailure,
        ];

        var incidents = Enumerable.Range(0, incidentCount).Select(i =>
        {
            var state = states[i % states.Length];
            var began = start.AddMinutes(23 * i);
            var length = TimeSpan.FromSeconds(31 + (i * 11));

            return new IncidentRow(
                i + 1, began, began + length, state, state.AttributionOf(),
                length - TimeSpan.FromSeconds(4), length, length + TimeSpan.FromSeconds(4),
                SampleCount: 12 + i,
                IsOpen: i == incidentCount - 1,
                EndedByGap: i % 5 == 2,
                StartedAfterGap: i % 5 == 3,
                RouteChanged: i % 7 == 1,
                CorrelationId: Guid.Empty,
                Support: 4 + (i % 5),
                Coverage: 9);
        }).ToList();

        List<TraceHop> hops =
        [
            new(1, "192.168.1.1", TimeSpan.FromMilliseconds(1.4)),
            new(2, "10.64.12.1", TimeSpan.FromMilliseconds(8.2)),
            new(3, "212.200.34.129", TimeSpan.FromMilliseconds(11.9)),
            new(4, null, null),
            new(5, null, null),
        ];

        var latency = Enumerable.Range(0, 300).Select(i => new LatencyBucket(
            TimeSpan.FromMinutes(i * 4),
            i % 29 == 0 ? null : 14,
            i % 29 == 0 ? null : 22,
            i % 29 == 0 ? null : 61,
            i % 29 == 0,
            i % 13 == 0)).ToList();

        return new SessionSnapshot(
            "S20260812194012", start, start.AddHours(41),
            "DESKTOP-TEST", "Intel(R) Wi-Fi 6E AX211 160MHz", LinkMedium.Wireless,
            866_700_000, "192.168.1.1",
            MonitoredTime: TimeSpan.FromHours(33.2),
            GapTime: TimeSpan.FromHours(7.8),
            UpstreamDowntime: TimeSpan.FromMinutes(74),
            LocalDowntime: TimeSpan.FromMinutes(19),
            AvailabilityPercent: 95.3241,
            UpstreamAvailabilityPercent: 96.2887,
            SampleCount: 119_412,
            incidents,
            [
                new(start.AddHours(3), TimeSpan.FromMinutes(412), nameof(GapCause.Sleep)),
                new(start.AddHours(14), TimeSpan.FromSeconds(96), nameof(GapCause.Reboot)),
            ],
            latency,
            [
                new TraceRow(
                    1, nameof(TracePhase.DuringOutage), start.AddMinutes(1), "1.1.1.1",
                    ReachedTarget: false, PrivateHopCount: 2, FirstPublicHop: "212.200.34.129",
                    LastAnsweringTtl: 3, StopsInsideHomeNetwork: false, Hops: hops),
            ]);
    }
}
