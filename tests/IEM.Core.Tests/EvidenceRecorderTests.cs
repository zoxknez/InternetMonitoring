using IEM.Core;
using IEM.Core.Model;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// End-to-end: engine to disk. Covers the two properties a recorded session has to have -
/// it verifies afterwards, and it survives the process dying mid-run.
/// </summary>
public sealed class EvidenceRecorderTests : IDisposable
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
            // SQLite may still hold the file briefly; a leftover temp directory is harmless.
        }
    }

    private static SessionStartPayload Start(string id) => new(
        id, "2.1.0", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
        "TEST-PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1");

    private async Task<(SessionPaths Paths, MonitorEngine Engine)> RecordAsync(
        IReadOnlyList<ProbeCycle> script,
        int sampleCount,
        SessionPaths? into = null,
        string sessionId = "S1")
    {
        var clock = new ManualClock();
        var step = TimeSpan.FromSeconds(1);
        var source = new ScriptedProbeSource(clock, script, step);

        var options = MonitorOptions.Default with
        {
            Cadence = new Core.Scheduling.CadenceOptions
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
        var paths = into ?? SessionPaths.ForNewSession(_root, DateTimeOffset.Now);

        using var recorder = EvidenceRecorder.Start(paths, engine, Start(sessionId));
        await engine.RunAsync(step * sampleCount, CancellationToken.None);
        recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);

        return (paths, engine);
    }

    [Fact]
    public async Task A_recorded_session_verifies_and_holds_every_sample()
    {
        var healthy = CycleBuilder.Wired().Build();

        var (paths, _) = await RecordAsync([healthy], sampleCount: 10);

        var verification = ChainVerifier.Verify(paths.RawLog);
        Assert.True(verification.Valid, verification.Reason);

        // Session start, the environment baseline, ten samples, session end. The baseline
        // is what makes the recording evidence about a particular connection rather than
        // about "the internet".
        Assert.Equal(13, verification.EntriesChecked);
        Assert.True(File.Exists(paths.Database));
        Assert.True(File.Exists(paths.ChainVerification));
    }

    [Fact]
    public async Task Incidents_reach_both_the_chain_and_the_index()
    {
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var (paths, engine) = await RecordAsync(
            [healthy, down, down, healthy, healthy], sampleCount: 5);

        Assert.True(ChainVerifier.Verify(paths.RawLog).Valid);

        var incidentLines = File.ReadLines(paths.RawLog)
            .Count(l => l.Contains("\"k\":\"Incident\"", StringComparison.Ordinal));
        Assert.Equal(1, incidentLines);

        using var store = SqliteSessionStore.Open(paths.Database);
        Assert.Equal(1, store.CountIncidents("S1"));
        Assert.Equal(5, store.CountSamples("S1"));
        Assert.Equal(1, engine.Statistics.UpstreamIncidentCount);
    }

    [Fact]
    public async Task The_verification_report_is_written_in_serbian()
    {
        var (paths, _) = await RecordAsync([CycleBuilder.Wired().Build()], sampleCount: 3);

        var report = await File.ReadAllTextAsync(paths.ChainVerification);

        Assert.Contains("ISPRAVAN", report, StringComparison.Ordinal);
        Assert.Contains("PROVERA INTEGRITETA", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session must survive the process dying. Reopening the same directory continues
    /// the same chain rather than starting a second one, which is what will make the
    /// Windows service able to pick up a 48-hour test after a restart.
    /// </summary>
    [Fact]
    public async Task Reopening_a_session_directory_continues_the_same_chain()
    {
        var healthy = CycleBuilder.Wired().Build();

        var (paths, _) = await RecordAsync([healthy], sampleCount: 5);
        var firstRunEntries = ChainVerifier.Verify(paths.RawLog).EntriesChecked;

        await RecordAsync([healthy], sampleCount: 5, into: paths, sessionId: "S1");

        var verification = ChainVerifier.Verify(paths.RawLog);

        Assert.True(verification.Valid, verification.Reason);
        Assert.True(
            verification.EntriesChecked > firstRunEntries,
            "reopening should have appended to the existing chain");
    }

    [Fact]
    public async Task A_crash_mid_write_costs_only_the_entry_in_flight()
    {
        var healthy = CycleBuilder.Wired().Build();
        var (paths, _) = await RecordAsync([healthy], sampleCount: 5);

        var intactEntries = ChainVerifier.Verify(paths.RawLog).EntriesChecked;

        // What being killed part-way through an append leaves on disk.
        await File.AppendAllTextAsync(paths.RawLog, "{\"k\":\"Sample\",\"n\":99,\"prev\":\"dead");

        Assert.False(ChainVerifier.Verify(paths.RawLog).Valid);

        using (var writer = HashChainWriter.Open(paths.RawLog))
        {
            writer.Append(new GapPayload(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), GapCause.Reboot));
        }

        var recovered = ChainVerifier.Verify(paths.RawLog);

        Assert.True(recovered.Valid, recovered.Reason);
        Assert.Equal(intactEntries + 1, recovered.EntriesChecked);
    }

    [Fact]
    public async Task Gaps_are_recorded_as_gaps_and_never_as_downtime()
    {
        var healthy = CycleBuilder.Wired().Build();

        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, [healthy], TimeSpan.FromMinutes(1));
        var engine = new MonitorEngine(source, MonitorOptions.Default with
        {
            Cadence = new Core.Scheduling.CadenceOptions { StableInterval = TimeSpan.FromMilliseconds(1) },
        }, clock);

        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);

        using (var recorder = EvidenceRecorder.Start(paths, engine, Start("S1")))
        {
            // A minute between samples with a one-millisecond cadence is unmistakably a gap.
            await engine.RunAsync(TimeSpan.FromMinutes(4), CancellationToken.None);
            recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);
        }

        var gapLines = File.ReadLines(paths.RawLog)
            .Count(l => l.Contains("\"k\":\"Gap\"", StringComparison.Ordinal));

        Assert.True(gapLines > 0, "sampling pauses should be recorded");
        Assert.Equal(TimeSpan.Zero, engine.Statistics.TotalDowntime);
        Assert.True(engine.Statistics.GapTime > TimeSpan.Zero);
        Assert.True(ChainVerifier.Verify(paths.RawLog).Valid);
    }
}
