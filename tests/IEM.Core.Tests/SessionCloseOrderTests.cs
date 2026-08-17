using IEM.Core;
using IEM.Core.Model;
using IEM.Evidence;
using IEM.Core.Probes;
using IEM.Core.Scheduling;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// Nothing may be written to the chain after the session is closed.
/// <para>
/// A record appended past the closing entry reads as "the session ended, and then more
/// things happened" - a contradiction on the face of the evidence, and the first thing
/// anyone checking it would seize on. Probes answering late are ordinary: a TCP connect can
/// take two seconds and a path trace tens of seconds, so one begun just before the user
/// pressed stop will routinely finish afterwards. That cannot be left to the shutdown
/// happening to run in a convenient order.
/// </para>
/// </summary>
public sealed class SessionCloseOrderTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static MonitorOptions FastOptions() => MonitorOptions.Default with
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

    private async Task<(SessionPaths Paths, EvidenceRecorder Recorder, MonitorEngine Engine)> RunAndCloseAsync()
    {
        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, [CycleBuilder.Wired().Build()], Step);
        var engine = new MonitorEngine(source, FastOptions(), clock);
        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);

        var recorder = EvidenceRecorder.Start(
            paths,
            engine,
            new SessionStartPayload(
                "S1", "2.2.0", clock.UtcNow, TimeSpan.FromHours(48),
                "TEST-PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1"),
            new RecorderOptions { CheckpointInterval = TimeSpan.Zero, IdleSyncInterval = TimeSpan.Zero });

        await engine.RunAsync(Step * 4, CancellationToken.None);
        recorder.Complete(engine.Statistics, clock.UtcNow);

        return (paths, recorder, engine);
    }

    /// <summary>
    /// A trace that finishes after the session closed is refused, and the refusal is counted
    /// rather than swallowed.
    /// </summary>
    [Fact]
    public async Task A_trace_arriving_after_the_session_closed_is_refused()
    {
        var (paths, recorder, _) = await RunAndCloseAsync();

        // Counted through the verifier, which shares the handle the recorder still holds.
        var before = ChainVerifier.Verify(paths.RawLog).EntriesChecked;

        recorder.RecordTrace(new IncidentTrace(
            IncidentNumber: 1,
            Phase: TracePhase.DuringOutage,
            TakenUtc: DateTimeOffset.UtcNow,
            Result: new TraceResult("1.1.1.1", [], ReachedTarget: false)));

        recorder.Dispose();

        Assert.Equal(before, ChainVerifier.Verify(paths.RawLog).EntriesChecked);
        Assert.Equal(1, recorder.RefusedAfterClose);
        Assert.Equal(nameof(EvidenceKind.Trace), recorder.LastRefusedKind);
    }

    /// <summary>
    /// The closing entry has to be the last thing in the file, because that is what makes it
    /// a closing entry at all.
    /// </summary>
    [Fact]
    public async Task The_closing_entry_is_the_final_record()
    {
        var (paths, recorder, _) = await RunAndCloseAsync();

        recorder.RecordTrace(new IncidentTrace(
            IncidentNumber: 1,
            Phase: TracePhase.AfterRecovery,
            TakenUtc: DateTimeOffset.UtcNow,
            Result: new TraceResult("1.1.1.1", [], ReachedTarget: false)));

        recorder.Dispose();

        var lines = await File.ReadAllLinesAsync(paths.RawLog);

        Assert.Contains("\"k\":\"SessionEnd\"", lines[^1], StringComparison.Ordinal);
        Assert.True(ChainVerifier.Verify(paths.RawLog).Valid);
    }

    /// <summary>
    /// A trace is the only evidence that says <em>where</em> the connection stopped rather
    /// than merely that it stopped. It was reaching the chain and going no further: no table
    /// in the index, no path through the rebuild, nothing in the report.
    /// </summary>
    [Fact]
    public async Task A_trace_taken_during_the_session_reaches_the_report()
    {
        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, [CycleBuilder.Wired().Build()], Step);
        var engine = new MonitorEngine(source, FastOptions(), clock);
        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);

        var recorder = EvidenceRecorder.Start(
            paths,
            engine,
            new SessionStartPayload(
                "S1", "2.2.0", clock.UtcNow, TimeSpan.FromHours(48),
                "TEST-PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1"),
            new RecorderOptions { CheckpointInterval = TimeSpan.Zero, IdleSyncInterval = TimeSpan.Zero });

        // The shape that matters: the router answered, nothing beyond it did.
        recorder.RecordTrace(new IncidentTrace(
            IncidentNumber: 1,
            Phase: TracePhase.DuringOutage,
            TakenUtc: clock.UtcNow,
            Result: new TraceResult(
                "1.1.1.1",
                [
                    new TraceHop(1, "192.168.1.1", TimeSpan.FromMilliseconds(2)),
                    new TraceHop(2, null, null),
                    new TraceHop(3, null, null),
                ],
                ReachedTarget: false)));

        await engine.RunAsync(Step * 3, CancellationToken.None);
        recorder.Complete(engine.Statistics, clock.UtcNow);
        recorder.Dispose();

        // Rebuilt from the chain alone, the way an export does it.
        File.Delete(paths.Database);
        EvidencePackage.Build(paths, createZip: false);

        var html = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Izvestaj.html"));

        Assert.Contains("Trase putanje tokom prekida", html, StringComparison.Ordinal);
        Assert.Contains("192.168.1.1", html, StringComparison.Ordinal);
        Assert.Contains("nije izašla iz vaše lokalne mreže", html, StringComparison.Ordinal);

        // The asymmetry has to be stated, or a reader takes silence for a located fault.
        Assert.Contains("ne</em> dokazuje kvar na sledećem uređaju", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_refused_during_a_normal_session()
    {
        var (_, recorder, _) = await RunAndCloseAsync();

        recorder.Dispose();

        Assert.Equal(0, recorder.RefusedAfterClose);
        Assert.Null(recorder.LastRefusedKind);
    }
}
