using System.Text;
using IEM.Core.Model;
using IEM.Core.Speed;
using IEM.Evidence;
using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// A speed measurement beside the session has to reach the report with its verdict, not as a
/// bare number. The conditions are what make the figure usable in a complaint, and a report
/// that printed "45 Mbit/s" without saying it was taken over Wi-Fi would do its reader a
/// disservice no decimal places can repair.
/// </summary>
public sealed class SpeedReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-speed-tests", Guid.NewGuid().ToString("N"));

    public SpeedReportTests() => Directory.CreateDirectory(_root);

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

    private static SpeedMeasurementNote Note(bool valid = true, params string[] defects) => new(
        new DateTimeOffset(2026, 8, 15, 10, 30, 0, TimeSpan.FromHours(2)),
        LinkMedium.Ethernet,
        LinkSpeedMbps: 1000,
        ContractedMbps: 100,
        DownloadMbps: 94.3,
        BytesTransferred: 523_000_000,
        Duration: TimeSpan.FromSeconds(10),
        valid,
        BandLabel: valid ? "90 % ugovorene ili više" : "ISPOD 70 % UGOVORENE",
        Defects: defects)
    {
        // Written the way this build writes them. Without the version the finding is read as
        // one from 2.6, whose stored verdict is history rather than a conclusion - which is
        // the whole point of the field, and worth being reminded of here.
        FindingSchemaVersion = SpeedMeasurementNote.CurrentFindingSchemaVersion,
        RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
    };

    private static SessionSnapshot Session() => new(
        "S1",
        new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero),
        "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1",
        MonitoredTime: TimeSpan.FromHours(48),
        GapTime: TimeSpan.Zero,
        UpstreamDowntime: TimeSpan.FromMinutes(42),
        LocalDowntime: TimeSpan.Zero,
        AvailabilityPercent: 98.5,
        UpstreamAvailabilityPercent: 98.5,
        SampleCount: 172_800,
        Incidents: [],
        Gaps: [],
        Latency: [],
        Traces: []);

    private string RenderHtml(SpeedMeasurementNote? note, SessionSnapshot? session = null)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.html");
        HtmlReportBuilder.Write(path, session ?? Session(), "abc123", true, note);
        return File.ReadAllText(path);
    }

    // ---- The note beside the session -------------------------------------------

    [Fact]
    public void A_written_note_reads_back_identically()
    {
        var note = Note(defects: "Merenje je izvršeno preko Wi-Fi veze.");
        note.Write(_root);

        var read = SpeedMeasurementNote.Read(_root);

        Assert.NotNull(read);

        // The lists are compared by their contents; a record compares them by reference, so
        // they are carried over before the whole is compared field by field.
        Assert.Equal(
            note with { Defects = read.Defects, ObservedInterfaces = read.ObservedInterfaces },
            read);

        Assert.Equal(note.Defects, read.Defects);
        Assert.Equal(note.ObservedInterfaces, read.ObservedInterfaces);
    }

    /// <summary>
    /// The report states what the sockets did, beside what the route table predicted - and for
    /// a note that never watched them it says so rather than leaving the row out.
    /// <para>
    /// Left out, an unobserved path would be indistinguishable from a good one on the page,
    /// which is the reading this whole line of work exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public void The_report_states_what_the_measurements_own_connections_did()
    {
        var observed = Note() with
        {
            ActualPathState = PathAgreementState.Mismatch,
            ObservedConnections = 6,
            ObservedInterfaces = ["Ethernet 4"],
        };

        var html = RenderHtml(observed);

        Assert.Contains("Veze merenja", html, StringComparison.Ordinal);
        Assert.Contains("nisu sve izašle kroz izabrani adapter", html, StringComparison.Ordinal);
        Assert.Contains("Ethernet 4", html, StringComparison.Ordinal);

        Assert.Contains("veze merenja nisu posmatrane", RenderHtml(Note()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_note_reads_as_absent()
    {
        Assert.Null(SpeedMeasurementNote.Read(_root));
    }

    /// <summary>
    /// A broken note must not take the report down with it: the report can say everything
    /// true without a speed figure, but not without itself.
    /// </summary>
    [Fact]
    public void An_unparseable_note_reads_as_absent()
    {
        File.WriteAllText(Path.Combine(_root, SpeedMeasurementNote.FileName), "{ nije json");
        Assert.Null(SpeedMeasurementNote.Read(_root));
    }

    // ---- What the report says ---------------------------------------------------

    [Fact]
    public void A_valid_measurement_is_stated_with_its_verdict()
    {
        var html = RenderHtml(Note());

        Assert.Contains("Merenje brzine", html, StringComparison.Ordinal);
        Assert.Contains("94,3 Mbit/s", html, StringComparison.Ordinal);
        Assert.Contains("Ispunjava uslove za korišćenje uz prigovor", html, StringComparison.Ordinal);
    }

    // ---- The sending half and the latency under load ----------------------------

    /// <summary>
    /// The sending direction reaches the report with its own contracted figure and its own
    /// verdict: a connection that meets its download rate while failing its upload is an
    /// ordinary complaint, and the report has to be able to say so.
    /// </summary>
    [Fact]
    public void The_sending_direction_is_stated_with_its_own_verdict()
    {
        var html = RenderHtml(Note() with
        {
            UploadMbps = 8.4,
            UploadBytesTransferred = 10_000_000,
            ContractedUploadMbps = 20,
            UploadBandLabel = "ISPOD 70 % UGOVORENE (slanje)",
        });

        Assert.Contains("Slanje - izmereno", html, StringComparison.Ordinal);
        Assert.Contains("8,4 Mbit/s", html, StringComparison.Ordinal);
        Assert.Contains("ISPOD 70 % UGOVORENE (slanje)", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A measurement whose sending half never ran must not appear in the report as a line
    /// that cannot send: that is a finding this tool did not make.
    /// </summary>
    [Fact]
    public void A_measurement_without_a_sending_half_says_nothing_about_sending()
    {
        var html = RenderHtml(Note());

        Assert.DoesNotContain("Slanje - izmereno", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Ocena slanja", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Latency_under_load_reaches_the_report_with_what_it_means()
    {
        var html = RenderHtml(Note() with
        {
            IdleLatencyMs = 12,
            LatencyUnderDownloadMs = 30,
            LatencyUnderUploadMs = 212,
            LatencyIncreaseMs = 200,
            LoadedLatencyLabel = "VELIKO",
        });

        Assert.Contains("Kašnjenje pod opterećenjem", html, StringComparison.Ordinal);
        Assert.Contains("200 ms", html, StringComparison.Ordinal);
        Assert.Contains("VELIKO", html, StringComparison.Ordinal);

        // Not just the number: what it means for calls and games, which is the complaint
        // somebody is actually trying to describe.
        Assert.Contains("pozivi", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measurement_that_never_sampled_latency_says_nothing_about_it()
    {
        Assert.DoesNotContain("Kašnjenje pod opterećenjem", RenderHtml(Note()), StringComparison.Ordinal);
    }

    /// <summary>
    /// "This document does not prove the contracted rate" stops being true the moment a
    /// properly measured figure is attached to it, and a report contradicting its own
    /// appendix is a gift to whoever reads it looking for flaws.
    /// </summary>
    [Fact]
    public void A_valid_measurement_replaces_the_blanket_disclaimer()
    {
        Assert.DoesNotContain("ne dokazuje", RenderHtml(Note()), StringComparison.Ordinal);
        Assert.Contains("ne dokazuje", RenderHtml(null), StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_measurement_lists_what_is_wrong_with_it()
    {
        var html = RenderHtml(Note(valid: false, defects: "Merenje je izvršeno preko Wi-Fi veze."));

        Assert.Contains("NE ispunjava uslove", html, StringComparison.Ordinal);
        Assert.Contains("preko Wi-Fi veze", html, StringComparison.Ordinal);
    }

    // ---- An outage that our own download could explain ----------------------------

    /// <summary>
    /// The figure, not just the fact: "the line was busy" invites the question "how busy",
    /// and the answer separates a stream in another room from a download that was using the
    /// whole connection. Without this the reader saw only a lower confidence band and no
    /// reason for it.
    /// </summary>
    [Fact]
    public void An_outage_measured_while_the_machine_itself_was_downloading_says_so()
    {
        var html = RenderHtml(null, WithIncident(25_000_000));

        Assert.Contains("i sam računar koristio vezu", html, StringComparison.Ordinal);
        Assert.Contains("25 MB/s", html, StringComparison.Ordinal);
        Assert.Contains("ne može bez rezerve pripisati samoj vezi", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quiet_line_during_the_outage_adds_no_such_caveat()
    {
        var html = RenderHtml(null, WithIncident(4_000));

        Assert.DoesNotContain("i sam računar koristio vezu", html, StringComparison.Ordinal);
    }

    /// <summary>Nothing measured is not a caveat either - it is simply nothing to say.</summary>
    [Fact]
    public void An_unmeasured_outage_adds_no_caveat()
    {
        var html = RenderHtml(null, WithIncident(null));

        Assert.DoesNotContain("i sam računar koristio vezu", html, StringComparison.Ordinal);
    }

    private static SessionSnapshot WithIncident(long? peakLocalTraffic)
    {
        var began = new DateTimeOffset(2026, 8, 13, 9, 15, 0, TimeSpan.Zero);
        var length = TimeSpan.FromSeconds(42);

        var incident = new IncidentRow(
            1, began, began + length,
            NetworkState.CpeUpstreamUnreachable,
            FaultAttribution.Upstream,
            length - TimeSpan.FromSeconds(1), length, length + TimeSpan.FromSeconds(1),
            SampleCount: 40,
            IsOpen: false,
            EndedByGap: false,
            StartedAfterGap: false,
            RouteChanged: false,
            CorrelationId: Guid.NewGuid(),
            Support: 70,
            Coverage: 80,
            PeakLocalTrafficBytesPerSecond: peakLocalTraffic);

        return Session() with { Incidents = [incident] };
    }

    [Fact]
    public void The_pdf_carries_the_section_without_failing()
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.pdf");

        PdfReportBuilder.Write(path, Session(), "abc123", true, Note());

        var header = new byte[4];
        using (var file = File.OpenRead(path))
        {
            file.ReadExactly(header);
        }

        Assert.Equal("%PDF"u8, header);
    }
}
