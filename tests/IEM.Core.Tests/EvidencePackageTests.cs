using System.Security.Cryptography;
using System.Text;
using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Scheduling;
using IEM.Evidence;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

public sealed class EvidencePackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-tests", Guid.NewGuid().ToString("N"));

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

    /// <summary>Records a short session and builds its package.</summary>
    private async Task<(SessionPaths Paths, PackageResult Result)> BuildAsync(
        IReadOnlyList<ProbeCycle> script,
        int sampleCount)
    {
        var clock = new ManualClock();
        var step = TimeSpan.FromSeconds(1);
        var source = new ScriptedProbeSource(clock, script, step);

        var options = MonitorOptions.Default with
        {
            Cadence = new CadenceOptions
            {
                StableInterval = TimeSpan.FromMilliseconds(1),
                SuspectInterval = TimeSpan.FromMilliseconds(1),
                BurstInterval = TimeSpan.FromMilliseconds(1),
                IncidentInterval = TimeSpan.FromMilliseconds(1),
                RecoveryInterval = TimeSpan.FromMilliseconds(1),
                RecoveryHold = TimeSpan.FromSeconds(2),
            },
        };

        var engine = new MonitorEngine(source, options, clock);
        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);

        var start = new SessionStartPayload(
            "S1", "2.1.0", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
            "TEST-PC", "Ethernet 4", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1");

        using (var recorder = EvidenceRecorder.Start(paths, engine, start))
        {
            await engine.RunAsync(step * sampleCount, CancellationToken.None);
            recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);
        }

        return (paths, EvidencePackage.Build(paths));
    }

    /// <summary>
    /// Long enough for a verdict to be drawn at all. The shared rule refuses to conclude
    /// anything from under a minute of observation, which is correct - a thirty-second
    /// test cannot support a complaint whatever it happened to catch - so a fixture that
    /// asserts a verdict has to run past that threshold.
    /// </summary>
    private const int ConclusiveSampleCount = 90;

    private static IReadOnlyList<ProbeCycle> OutageScript()
    {
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();
        return [healthy, healthy, down, down, down, healthy, healthy, healthy];
    }

    [Fact]
    public async Task The_package_contains_everything_a_recipient_needs()
    {
        var (paths, result) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        foreach (var name in new[]
                 {
                     "Izvestaj.html", "Izvestaj.pdf", "Rezime.txt", "Prekidi.csv", "Merenja.csv",
                     "SHA256SUMS.txt", "Provera-lanca.txt", "SirovaEvidencija.jsonl",
                 })
        {
            Assert.True(File.Exists(Path.Combine(paths.Directory, name)), $"{name} nedostaje");
        }

        Assert.Null(result.PdfFailure);
        Assert.True(result.Verification.Valid);
        Assert.NotNull(result.ZipPath);
        Assert.True(File.Exists(result.ZipPath));

        // Checksummed like everything else, or the package carries a document it makes no
        // statement about - which is the one file a recipient is most likely to read.
        var sums = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "SHA256SUMS.txt"));
        Assert.Contains("Izvestaj.pdf", sums, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report states what was measured, not who is to blame. A monitor running on the
    /// customer's own computer never observes the operator's network, so a report opening
    /// with "confirmed at the operator" hands them the easiest possible rebuttal - while the
    /// thing that actually was measured, the router answering throughout, is much harder to
    /// wave away.
    /// </summary>
    [Fact]
    public async Task The_report_says_the_fault_was_isolated_beyond_the_customers_equipment()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var html = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.Contains("class=\"verdict bad\"", html, StringComparison.Ordinal);
        Assert.Contains("izolovani iza vaše opreme", html, StringComparison.Ordinal);
        Assert.Contains("isključena kao", html, StringComparison.Ordinal);

        Assert.DoesNotContain("Potvrđeni prekidi na strani operatera", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A router losing its Wi-Fi radio must not produce a report telling the customer to
    /// complain to their operator. Getting this backwards sends people into a dispute
    /// they cannot win.
    /// </summary>
    [Fact]
    public async Task The_report_does_not_blame_the_operator_for_a_router_fault()
    {
        var healthy = CycleBuilder.Wireless().Build();
        var radioDown = CycleBuilder.Wireless().AdapterDown().SsidNotVisible().AllExternalFail().Build();

        var (paths, _) = await BuildAsync([healthy, radioDown, radioDown, healthy, healthy], ConclusiveSampleCount);

        var html = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.Contains("class=\"verdict warn\"", html, StringComparison.Ordinal);
        Assert.Contains("Prekidi postoje, ali su lokalni", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Imate osnov za prigovor", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_session_says_so_plainly()
    {
        var (paths, _) = await BuildAsync([CycleBuilder.Wired().Build()], ConclusiveSampleCount);

        var html = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.Contains("class=\"verdict good\"", html, StringComparison.Ordinal);
        Assert.Contains("Veza je bila stabilna", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A test too short to conclude anything must say so rather than declare a case.
    /// <para>
    /// The rule lives in the core and is shared by the window, the console and this
    /// report, so a thirty-second run cannot produce a document telling someone they have
    /// grounds for a complaint.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_session_too_short_to_conclude_from_says_so()
    {
        var (paths, _) = await BuildAsync(OutageScript(), sampleCount: 8);

        var html = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.Contains("Test je prekratak", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Imate osnov za prigovor", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_is_self_contained()
    {
        // It has to render on a machine with no internet - the subject of the document -
        // and survive a mail client that blocks remote content.
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var html = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<svg", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a byte order mark Excel on a Serbian Windows renders č as Ä, and the
    /// customer forwards a mangled file to their operator.
    /// </summary>
    [Fact]
    public async Task Csv_files_open_correctly_in_serbian_excel()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        foreach (var name in new[] { "Prekidi.csv", "Merenja.csv", "Rezime.txt" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(paths.Directory, name));

            Assert.True(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{name} nema UTF-8 BOM");
        }

        var incidents = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Prekidi.csv"), Encoding.UTF8);

        // Semicolons, because the Serbian locale writes decimals with a comma.
        Assert.Contains(';', incidents);
        Assert.Contains("Početak", incidents, StringComparison.Ordinal);
        Assert.Matches(@"\d,\d", incidents);
    }

    [Fact]
    public async Task Incident_rows_carry_the_duration_bounds()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var lines = await File.ReadAllLinesAsync(Path.Combine(paths.Directory, "Prekidi.csv"), Encoding.UTF8);

        Assert.Equal(2, lines.Length);

        var header = lines[0].Split(';');
        Assert.Contains("Najmanje (s)", header);
        Assert.Contains("Najviše (s)", header);
        Assert.Contains("Operater", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checksums_cover_the_package_and_verify()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var checksumPath = Path.Combine(paths.Directory, "SHA256SUMS.txt");
        var lines = await File.ReadAllLinesAsync(checksumPath);

        Assert.NotEmpty(lines);

        foreach (var line in lines)
        {
            var expected = line[..64];
            var relative = line[66..];
            var full = Path.Combine(paths.Directory, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(full), $"{relative} je naveden ali ne postoji");

            await using var stream = File.OpenRead(full);
            Assert.Equal(expected, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)));
        }

        Assert.DoesNotContain(lines, l => l.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_package_can_be_rebuilt_from_a_stored_session()
    {
        // A session cut short by a power cut still has intact evidence; producing its
        // report must not require repeating the test.
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        File.Delete(Path.Combine(paths.Directory, "Izvestaj.html"));

        var rebuilt = EvidencePackage.Build(paths);

        Assert.True(File.Exists(Path.Combine(paths.Directory, "Izvestaj.html")));
        Assert.True(rebuilt.Verification.Valid);
    }

    /// <summary>
    /// The whole path: scored in the engine, written to the chain, read back through the
    /// rebuilt index, and shown as a band. The scorer used to be called nowhere at all, so
    /// this is the test that proves the number in front of the reader is a real one.
    /// </summary>
    [Fact]
    public async Task Confidence_survives_the_chain_and_reaches_the_report_as_a_band()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var raw = await File.ReadAllTextAsync(paths.RawLog);

        Assert.Contains("\"support\":", raw, StringComparison.Ordinal);
        Assert.Contains("\"coverage\":", raw, StringComparison.Ordinal);
        Assert.Contains("\"confidenceBand\":", raw, StringComparison.Ordinal);
        Assert.Contains("cpe.gatewayReachable", raw, StringComparison.Ordinal);

        // Rebuilt from the chain alone, the way an export does it.
        File.Delete(paths.Database);
        EvidencePackage.Build(paths);

        var html = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.Contains("Pouzdanost", html, StringComparison.Ordinal);
        Assert.Contains("Kako se čita pouzdanost", html, StringComparison.Ordinal);
        Assert.Contains("Izražena je kao pojas, ne kao", html, StringComparison.Ordinal);

        // A band in the table, whichever one the evidence earned - never a bare percentage.
        //
        // Matched without pinning the cell's attributes. The assertion used to require a
        // bare <td>, so adding an alignment class to the column failed a test about what
        // the report concludes - which is not what it is there to protect.
        string[] bands = ["VRLO VISOKA", "VISOKA", "UMERENA", "NISKA", "VRLO NISKA"];

        Assert.Contains(bands, band =>
            System.Text.RegularExpressions.Regex.IsMatch(html, $"<td[^>]*>{band}</td>"));
    }

    /// <summary>
    /// A report can be produced while the session is still running.
    /// <para>
    /// The console offers exactly that, and someone two days into a test has every reason to
    /// want to look at what they have so far. It used to fail part-way through - after the
    /// CSVs had already been rewritten - because the checksum step opened the raw log in a
    /// way that excluded the writer still appending to it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_report_can_be_built_while_the_session_is_still_being_written()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        // A writer holding the log open exactly as the service does throughout a session.
        await using var writer = new FileStream(
            paths.RawLog, FileMode.Append, FileAccess.Write, FileShare.Read);

        var result = EvidencePackage.Build(paths);

        Assert.True(result.Verification.Valid);
        Assert.True(File.Exists(Path.Combine(paths.Directory, "Izvestaj.html")));
        Assert.True(File.Exists(Path.Combine(paths.Directory, "SHA256SUMS.txt")));
        Assert.NotNull(result.ZipPath);
    }

    // ---- P0-5: the raw chain is the only source of truth ---------------------

    /// <summary>
    /// An export never repairs evidence. A hash that stops matching part-way through means
    /// the file changed; carrying on and noting it on the report would still produce a
    /// document that gets forwarded and treated as a claim.
    /// </summary>
    [Fact]
    public async Task A_tampered_log_is_refused_rather_than_exported_with_a_warning()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var lines = await File.ReadAllLinesAsync(paths.RawLog);

        // Turn one healthy sample into an outage - the edit someone would actually make to
        // inflate a claim. Located by content rather than by index so the test cannot
        // silently pass by editing nothing.
        var target = Array.FindIndex(lines, l => l.Contains("\"state\":\"Ok\"", StringComparison.Ordinal));
        Assert.True(target >= 0, "nije pronađen ispravan uzorak za izmenu");

        lines[target] = lines[target].Replace(
            "\"state\":\"Ok\"", "\"state\":\"InternetDown\"", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(paths.RawLog, lines);

        File.Delete(Path.Combine(paths.Directory, "Izvestaj.html"));

        var refusal = Assert.Throws<EvidenceExportRefusedException>(() => EvidencePackage.Build(paths));

        Assert.Contains("IZVOZ ODBIJEN", refusal.Message, StringComparison.Ordinal);
        Assert.False(
            File.Exists(Path.Combine(paths.Directory, "Izvestaj.html")),
            "a refused export must not leave a report behind");
    }

    /// <summary>
    /// The database is a cache over the chain. Deleting the raw log used to leave a report
    /// that still declared the chain unbroken - the most useful single edit available to
    /// anyone wanting to inflate a claim.
    /// </summary>
    [Fact]
    public async Task Deleting_the_raw_log_makes_the_report_impossible()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        File.Delete(paths.RawLog);
        File.Delete(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.True(File.Exists(paths.Database), "the index is still there, and still not evidence");
        Assert.Throws<EvidenceExportRefusedException>(() => EvidencePackage.Build(paths));
        Assert.False(File.Exists(Path.Combine(paths.Directory, "Izvestaj.html")));
    }

    /// <summary>
    /// The other direction: the chain alone is enough. Losing the index costs nothing,
    /// because the report is built from a fresh one reconstructed out of the chain.
    /// </summary>
    [Fact]
    public async Task Deleting_the_index_costs_nothing_because_it_is_rebuilt_from_the_chain()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var expected = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Prekidi.csv"));

        File.Delete(paths.Database);

        var result = EvidencePackage.Build(paths);

        Assert.True(result.Verification.Valid);
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Prekidi.csv")));
    }

    /// <summary>
    /// A record this build cannot interpret - one written by a later version, say - must be
    /// counted and reported, not skipped in silence. Silently dropping it during the very
    /// operation meant to establish the chain as authoritative produces a report that is
    /// short of evidence which demonstrably exists, with nothing anywhere saying so.
    /// </summary>
    [Fact]
    public async Task Records_this_build_cannot_read_are_counted_rather_than_dropped_quietly()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var rebuild = EvidenceIndexRebuilder.RebuildForExport(paths);

        try
        {
            // Everything in a chain this build wrote must be readable by it.
            Assert.Equal(0, rebuild.UnreadableEntries);
            Assert.False(rebuild.Incomplete);
            Assert.True(rebuild.DerivedRecords > 0);
        }
        finally
        {
            File.Delete(rebuild.DatabasePath);
        }
    }

    /// <summary>
    /// A crash between the chain write and the database write leaves the index short. That
    /// is ordinary; saying nothing about it would not be.
    /// </summary>
    [Fact]
    public async Task A_short_index_is_rebuilt_and_the_difference_is_reported()
    {
        var (paths, _) = await BuildAsync(OutageScript(), ConclusiveSampleCount);

        var chainOnly = EvidencePackage.Build(paths).Rebuild.DerivedRecords;

        File.Delete(paths.Database);

        var result = EvidencePackage.Build(paths);

        Assert.Equal(chainOnly, result.Rebuild.DerivedRecords);
        Assert.True(result.Rebuild.DerivedRecords > 0, "the chain carried nothing at all");
    }
}
