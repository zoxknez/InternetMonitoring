using IEM.Core;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Scheduling;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// A two-day test will meet a Windows update, a power cut, or a service restart. What
/// happens next decides whether the customer has evidence or has to start over, so this
/// is the part of the system worth being pedantic about.
/// </summary>
public sealed class SessionResumeTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    /// <summary>How long the monitor is treated as having been down between runs.</summary>
    private static readonly TimeSpan Interruption = TimeSpan.FromSeconds(30);

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

    /// <summary>Checkpoint on every sample, so the resume path is actually exercised.</summary>
    private static readonly RecorderOptions EagerCheckpoints = new()
    {
        CheckpointInterval = TimeSpan.Zero,
        IdleSyncInterval = TimeSpan.Zero,
    };

    /// <param name="LastObservedUtc">Wall time of the final sample, on the log's own timeline.</param>
    private sealed record InterruptedSession(SessionPaths Paths, MonitorEngine Engine, DateTimeOffset LastObservedUtc)
    {
        /// <summary>
        /// A plausible "now" for the moment someone tries to resume.
        /// <para>
        /// Derived from the log's own timeline rather than from the real clock. The
        /// hand-driven clock starts at a fixed date, so mixing in real wall time would
        /// manufacture an interruption of several hours and make every assertion here
        /// meaningless.
        /// </para>
        /// </summary>
        public DateTimeOffset ResumedAt => LastObservedUtc + Interruption;
    }

    private static SessionStartPayload Start(TimeSpan planned, DateTimeOffset startedUtc) => new(
        "S1", "2.1.0", startedUtc, planned,
        "TEST-PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1");

    /// <summary>Runs part of a session and walks away without closing it, as a crash would.</summary>
    private async Task<InterruptedSession> RunInterruptedAsync(
        IReadOnlyList<ProbeCycle> script,
        int sampleCount,
        TimeSpan planned,
        RecorderOptions? recorderOptions = null)
    {
        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, script, Step);
        var engine = new MonitorEngine(source, FastOptions(), clock);
        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);

        using var recorder = EvidenceRecorder.Start(
            paths, engine, Start(planned, clock.UtcNow), recorderOptions ?? EagerCheckpoints);

        await engine.RunAsync(Step * sampleCount, CancellationToken.None);

        // No Complete() call: the session is left open exactly as a killed process leaves it.
        return new InterruptedSession(paths, engine, clock.UtcNow);
    }

    private async Task<MonitorEngine> ResumeAsync(
        SessionPaths paths,
        ResumeContext context,
        IReadOnlyList<ProbeCycle> script,
        int sampleCount)
    {
        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, script, Step);
        var engine = new MonitorEngine(source, FastOptions(), clock, context);

        using var recorder = EvidenceRecorder.Resume(paths, engine, "S1", EagerCheckpoints);
        await engine.RunAsync(Step * sampleCount, CancellationToken.None);
        recorder.Complete(engine.Statistics, clock.UtcNow);

        return engine;
    }

    // ---- Decisions --------------------------------------------------------

    [Fact]
    public void An_empty_root_has_nothing_to_resume()
    {
        var analysis = SessionResumeAnalyzer.Analyze(_root, DateTimeOffset.UtcNow);

        Assert.Equal(ResumeDecision.NothingToResume, analysis.Decision);
    }

    [Fact]
    public async Task A_closed_session_is_not_resumed()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 5, TimeSpan.FromHours(48));

        using (var recorder = EvidenceRecorder.Resume(session.Paths, session.Engine, "S1", EagerCheckpoints))
        {
            recorder.Complete(session.Engine.Statistics, session.LastObservedUtc);
        }

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        Assert.Equal(ResumeDecision.AlreadyClosed, analysis.Decision);
    }

    [Fact]
    public async Task An_interrupted_session_is_resumable_with_time_left()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 10, TimeSpan.FromHours(48));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);
        Assert.NotNull(analysis.Context);
        Assert.Equal(Interruption, analysis.Interruption);
        Assert.True(
            analysis.Remaining > TimeSpan.FromHours(47),
            $"expected nearly the full 48h left, got {analysis.Remaining}");
    }

    // ---- P0-7: an outage in progress survives the process dying ---------------

    /// <summary>
    /// Writes a chain that ends the way a killed process leaves one: healthy samples, then
    /// failing samples, and nothing else.
    /// <para>
    /// The engine cannot produce this on its own - stopping it cleanly runs its shutdown
    /// path, which closes the outage and writes the segment. A process that is killed never
    /// gets that far, which is precisely the case worth testing.
    /// </para>
    /// </summary>
    private SessionPaths WriteChainKilledMidOutage(int healthySamples, int failingSamples)
    {
        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawLog)!);

        var startedUtc = new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero);

        using var writer = HashChainWriter.Open(paths.RawLog);
        writer.Append(Start(TimeSpan.FromHours(48), startedUtc));

        var sequence = 0L;

        for (var i = 0; i < healthySamples + failingSamples; i++)
        {
            var failing = i >= healthySamples;
            var at = TimeSpan.FromSeconds(i + 1);

            writer.Append(new SamplePayload(
                ++sequence,
                startedUtc + at,
                at,
                failing ? NetworkState.CpeUpstreamUnreachable : NetworkState.Ok,
                failing ? Severity.Outage : Severity.Ok,
                failing ? "Nothing beyond the gateway answered" : "ok",
                failing ? "Incident" : "Stable",
                LinkStatus.Up,
                failing ? null : TimeSpan.FromMilliseconds(18),
                new ProbeTally(1, 1), new ProbeTally(3, failing ? 0 : 3), new ProbeTally(2, failing ? 0 : 2),
                new ProbeTally(1, failing ? 0 : 1), new ProbeTally(1, failing ? 0 : 1),
                new ProbeTally(1, failing ? 0 : 1), new ProbeTally(1, failing ? 0 : 1),
                new ProbeTally(1, failing ? 0 : 1),
                false, null, null));
        }

        return paths;
    }

    /// <summary>
    /// The crash case. Failing samples reach the chain as they happen, but the segment that
    /// summarises them is only written when it closes - so a process killed mid-outage used
    /// to leave the evidence in the log and nothing whatsoever in the statistics.
    /// </summary>
    [Fact]
    public void An_outage_running_when_the_process_died_is_reconstructed()
    {
        var paths = WriteChainKilledMidOutage(healthySamples: 3, failingSamples: 9);

        var analysis = SessionResumeAnalyzer.Analyze(
            paths, new DateTimeOffset(2026, 8, 13, 16, 0, 12, TimeSpan.Zero) + Interruption);

        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);

        var open = analysis.Context!.OpenIncident;
        Assert.NotNull(open);
        Assert.Equal(9, open.SampleCount);
        Assert.Contains(NetworkState.CpeUpstreamUnreachable, open.StatesSeen);
        Assert.Equal(TimeSpan.FromSeconds(3), open.LastGood!.Value.Monotonic);
        Assert.Equal(TimeSpan.FromSeconds(4), open.FirstBad.Monotonic);
        Assert.Equal(TimeSpan.FromSeconds(12), open.LastBad.Monotonic);
    }

    [Fact]
    public void A_chain_that_ends_healthy_reconstructs_no_open_outage()
    {
        var paths = WriteChainKilledMidOutage(healthySamples: 12, failingSamples: 0);

        var analysis = SessionResumeAnalyzer.Analyze(
            paths, new DateTimeOffset(2026, 8, 13, 16, 0, 12, TimeSpan.Zero) + Interruption);

        Assert.Null(analysis.Context!.OpenIncident);
    }

    /// <summary>
    /// Reconstructing it is only half the job: the pause that follows has to close it, so it
    /// reaches the totals and cannot go on absorbing time nobody watched.
    /// </summary>
    [Fact]
    public async Task The_reconstructed_outage_is_closed_by_the_pause_and_counted()
    {
        var paths = WriteChainKilledMidOutage(healthySamples: 3, failingSamples: 9);

        var analysis = SessionResumeAnalyzer.Analyze(
            paths, new DateTimeOffset(2026, 8, 13, 16, 0, 12, TimeSpan.Zero) + Interruption);

        var closed = new List<IncidentRecord>();
        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, [CycleBuilder.Wired().Build()], Step);
        var engine = new MonitorEngine(source, FastOptions(), clock, analysis.Context);
        engine.IncidentClosed += closed.Add;

        await engine.RunAsync(Step * 5, CancellationToken.None);

        var restored = Assert.Single(closed);

        Assert.True(restored.EndedByGap);
        Assert.Equal(9, restored.SampleCount);
        Assert.Equal(TimeSpan.FromSeconds(8), restored.DurationMin);

        // Thirty seconds of interruption sit between the last failing sample and the resume.
        // None of it may be charged to the operator.
        Assert.True(
            restored.DurationMax < TimeSpan.FromSeconds(15),
            $"the pause leaked into the ceiling: {restored.DurationMax}");
    }

    // ---- Closing an abandoned session tells the truth --------------------------

    /// <summary>
    /// The closer used to write its closing entry from totals the analysis had thrown away:
    /// every abandoned session ended up asserting zero monitored time and a hundred-percent
    /// availability in the hash chain - over a session that may have died mid-outage, with
    /// the chain then verifying those false numbers perfectly.
    /// </summary>
    [Fact]
    public void An_abandoned_session_closes_with_the_totals_it_actually_recorded()
    {
        var paths = WriteChainKilledMidOutage(healthySamples: 3, failingSamples: 9);

        // Eight days after the last sample the session is expired, not resumable.
        var now = new DateTimeOffset(2026, 8, 13, 16, 0, 12, TimeSpan.Zero) + TimeSpan.FromDays(8);

        var analysis = SessionResumeAnalyzer.Analyze(paths, now);
        Assert.Equal(ResumeDecision.Expired, analysis.Decision);

        var closed = AbandonedSessionCloser.Close(paths, now);

        Assert.True(closed);
        Assert.True(ChainReader.IsClosed(paths.RawLog));

        // Re-analysis now says AlreadyClosed, which only happens for a well-formed end.
        Assert.Equal(
            ResumeDecision.AlreadyClosed,
            SessionResumeAnalyzer.Analyze(paths, now + TimeSpan.FromMinutes(1)).Decision);

        // The closing entry carries real numbers: twelve samples over twelve observed
        // seconds, not zeros, and the trailing outage is an incident rather than a blank.
        var end = ChainReader.Read(paths.RawLog)
            .First(e => e.Kind == EvidenceKind.SessionEnd);

        Assert.True(end.Payload.GetProperty("monitoredMs").GetDouble() > 0, "nadzirano vreme je nula");

        var incident = ChainReader.Read(paths.RawLog)
            .FirstOrDefault(e => e.Kind == EvidenceKind.Incident);

        Assert.NotNull(incident);
        Assert.Equal(
            nameof(NetworkState.CpeUpstreamUnreachable),
            incident.Payload.GetProperty("worstState").GetString());
    }

    // ---- Degraded time survives the process dying -----------------------------

    /// <summary>
    /// Writes a chain whose tail is degraded but not down, with a checkpoint in the middle
    /// that already folds part of the degraded stretch - the state a killed process leaves
    /// when it died while the link was merely bad.
    /// </summary>
    private SessionPaths WriteChainDegradedTail(bool withCheckpoint)
    {
        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.RawLog)!);

        var startedUtc = new DateTimeOffset(2026, 8, 13, 16, 0, 0, TimeSpan.Zero);

        using var writer = HashChainWriter.Open(paths.RawLog);
        writer.Append(Start(TimeSpan.FromHours(48), startedUtc));

        NetworkState StateAt(int second) => second is 2 or 3 or 4
            ? NetworkState.HighLatency
            : NetworkState.Ok;

        Severity SeverityAt(int second) => StateAt(second).SeverityOf();

        for (var second = 1; second <= 5; second++)
        {
            if (withCheckpoint && second == 4)
            {
                // Written on the sample path right after sample 3, so its totals fold every
                // interval up to and including (2 -> 3): one second of degraded time.
                writer.Append(new CheckpointPayload(
                    startedUtc + TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(3),
                    3,
                    MonitoredTime: TimeSpan.FromSeconds(3),
                    GapTime: TimeSpan.Zero,
                    DegradedTime: TimeSpan.FromSeconds(1),
                    UpstreamDowntime: TimeSpan.Zero,
                    LocalDowntime: TimeSpan.Zero,
                    IncidentCount: 0,
                    UpstreamIncidentCount: 0,
                    LongestUpstreamOutage: TimeSpan.Zero));
            }

            var at = TimeSpan.FromSeconds(second);
            var degraded = StateAt(second) != NetworkState.Ok;

            writer.Append(new SamplePayload(
                second, startedUtc + at, at,
                StateAt(second), SeverityAt(second),
                degraded ? "Latency above the threshold" : "ok",
                degraded ? "Stable" : "Stable",
                LinkStatus.Up,
                degraded ? TimeSpan.FromMilliseconds(400) : TimeSpan.FromMilliseconds(18),
                new ProbeTally(1, 1), new ProbeTally(3, 3), new ProbeTally(2, 2),
                new ProbeTally(1, 1), new ProbeTally(1, 1), new ProbeTally(1, 1),
                new ProbeTally(1, 1), new ProbeTally(1, 1),
                false, null, null));
        }

        return paths;
    }

    /// <summary>
    /// The engine books each interval against the previous sample's severity. A checkpoint
    /// only folds the intervals booked by the time it was written, so the degraded stretches
    /// after it - like the downtime of an open outage - have to be rebuilt from the samples
    /// or they quietly vanish from a resumed session's totals.
    /// </summary>
    [Fact]
    public void Degraded_time_after_the_last_checkpoint_is_reconstructed()
    {
        var paths = WriteChainDegradedTail(withCheckpoint: true);

        var analysis = SessionResumeAnalyzer.Analyze(
            paths, new DateTimeOffset(2026, 8, 13, 16, 0, 5, TimeSpan.Zero) + Interruption);

        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);

        // One second folded into the checkpoint, plus the intervals (3 -> 4) and (4 -> 5),
        // each booked against a degraded previous sample.
        Assert.Equal(TimeSpan.FromSeconds(3), analysis.Context!.Prior.DegradedTime);
    }

    /// <summary>Without any checkpoint the whole chain is the tail, so all of it counts.</summary>
    [Fact]
    public void Degraded_time_is_reconstructed_from_the_whole_chain_without_a_checkpoint()
    {
        var paths = WriteChainDegradedTail(withCheckpoint: false);

        var analysis = SessionResumeAnalyzer.Analyze(
            paths, new DateTimeOffset(2026, 8, 13, 16, 0, 5, TimeSpan.Zero) + Interruption);

        // Three intervals are booked against a degraded previous sample: (2 -> 3),
        // (3 -> 4) and (4 -> 5). The interval (1 -> 2) is booked against a healthy one.
        Assert.Equal(TimeSpan.FromSeconds(3), analysis.Context!.Prior.DegradedTime);
    }

    // ---- Why monitoring was not running ---------------------------------------

    /// <summary>
    /// A machine up for less time than the pause lasted must have restarted during it - it
    /// cannot have been running throughout and simply missed it.
    /// <para>
    /// Worth distinguishing, because the two read very differently to an operator. "The
    /// computer restarted" is an ordinary explanation nobody questions; "monitoring was not
    /// running" invites the question of why not, and puts the customer on the back foot over
    /// a gap they did not cause.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_interruption_longer_than_the_uptime_is_recorded_as_a_restart()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 5, TimeSpan.FromHours(48));
        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        var gaps = new List<MonitoringGapEvent>();

        // The machine came back up moments ago, while the interruption lasted thirty
        // seconds. It cannot have been running throughout and simply missed it.
        var clock = new ManualClock();
        clock.Reboot(TimeSpan.Zero);

        var source = new ScriptedProbeSource(clock, [CycleBuilder.Wired().Build()], Step);
        var engine = new MonitorEngine(source, FastOptions(), clock, analysis.Context);

        engine.GapDetected += gaps.Add;
        await engine.RunAsync(Step * 3, CancellationToken.None);

        Assert.Contains(gaps, g => g.Cause == GapCause.Reboot);
    }

    [Fact]
    public async Task An_interruption_shorter_than_the_uptime_is_recorded_as_monitoring_not_running()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 5, TimeSpan.FromHours(48));
        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        var gaps = new List<MonitoringGapEvent>();

        // The machine has been up for hours, so the service was restarted, not the machine.
        var clock = new ManualClock();
        var source = new ScriptedProbeSource(clock, [CycleBuilder.Wired().Build()], Step);
        var engine = new MonitorEngine(source, FastOptions(), clock, analysis.Context);

        engine.GapDetected += gaps.Add;
        await engine.RunAsync(Step * 3, CancellationToken.None);

        Assert.Contains(gaps, g => g.Cause == GapCause.MonitorNotRunning);
        Assert.DoesNotContain(gaps, g => g.Cause == GapCause.Reboot);
    }

    // ---- The environment survives the interruption ----------------------------

    /// <summary>
    /// A router swapped out while the monitor was not running is precisely when a router
    /// gets swapped out. If the comparison restarts from whatever is present afterwards, the
    /// session silently becomes evidence about a different connection.
    /// </summary>
    [Fact]
    public async Task The_connection_under_test_is_remembered_across_the_interruption()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 6, TimeSpan.FromHours(48));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);

        var carried = analysis.Context!.Environment;

        Assert.NotNull(carried);
        Assert.Equal("{TEST}", carried.InterfaceId);
        Assert.Equal("192.168.1.1", carried.GatewayAddress);
    }

    /// <summary>
    /// And the engine has to actually use it, rather than establishing a fresh baseline on
    /// its first sample and comparing the rest of the session against that.
    /// </summary>
    [Fact]
    public async Task A_resumed_session_compares_against_the_original_connection()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 6, TimeSpan.FromHours(48));
        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        var changes = new List<NetworkEnvironmentChange>();
        var clock = new ManualClock();

        // The gateway moved while nothing was watching.
        var movedHouse = CycleBuilder.Wired().DifferentGateway("192.168.0.1").Build();
        var source = new ScriptedProbeSource(clock, [movedHouse], Step);
        var engine = new MonitorEngine(source, FastOptions(), clock, analysis.Context);

        engine.NetworkEnvironmentChanged += changes.Add;

        await engine.RunAsync(Step * 3, CancellationToken.None);

        var change = Assert.Single(changes);

        Assert.False(change.IsBaseline, "a resumed session must not restate a baseline as though it were new");
        Assert.Contains(change.Differences, d => d.Contains("192.168.0.1", StringComparison.Ordinal));
    }

    // ---- P0-5: a falsified session is not continued ---------------------------

    /// <summary>
    /// Appending to an altered chain would produce a longer record with the flaw buried
    /// deeper in it, and every entry written afterwards would chain from a record already in
    /// doubt. The session stops; a new one begins beside it.
    /// </summary>
    [Fact]
    public async Task A_session_whose_chain_was_altered_is_not_continued()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 10, TimeSpan.FromHours(48));

        var lines = await File.ReadAllLinesAsync(session.Paths.RawLog);
        var target = Array.FindIndex(lines, l => l.Contains("\"state\":\"Ok\"", StringComparison.Ordinal));
        Assert.True(target >= 0, "nije pronađen ispravan uzorak za izmenu");

        lines[target] = lines[target].Replace(
            "\"state\":\"Ok\"", "\"state\":\"InternetDown\"", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(session.Paths.RawLog, lines);

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        Assert.Equal(ResumeDecision.IntegrityCompromised, analysis.Decision);
        Assert.Null(analysis.Context);
    }

    /// <summary>
    /// The case that must <em>not</em> be mistaken for the one above. A killed process
    /// leaves a half-written final line every time; refusing to continue on that basis would
    /// throw away a two-day test because of the entry that was in flight.
    /// </summary>
    [Fact]
    public async Task A_half_written_final_line_is_recovered_rather_than_treated_as_tampering()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 10, TimeSpan.FromHours(48));

        var text = await File.ReadAllTextAsync(session.Paths.RawLog);
        await File.WriteAllTextAsync(session.Paths.RawLog, text[..^24]);

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);
    }

    [Fact]
    public async Task A_session_whose_time_ran_out_is_not_extended()
    {
        // Planned for ten seconds and interrupted; by the time anyone looks, it is over.
        // Resuming would silently stretch the observation window past what was planned.
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 5, TimeSpan.FromSeconds(10));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.LastObservedUtc.AddHours(1));

        Assert.Equal(ResumeDecision.Expired, analysis.Decision);
    }

    [Fact]
    public async Task An_absurdly_long_interruption_is_not_resumed()
    {
        // A session abandoned for weeks is not the same test any more, whatever its plan says.
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 5, Timeout.InfiniteTimeSpan);

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.LastObservedUtc.AddDays(30));

        Assert.Equal(ResumeDecision.Expired, analysis.Decision);
    }

    [Fact]
    public async Task An_open_ended_session_is_resumable_after_a_short_interruption()
    {
        var session = await RunInterruptedAsync([CycleBuilder.Wired().Build()], 5, Timeout.InfiniteTimeSpan);

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);
        Assert.Equal(Timeout.InfiniteTimeSpan, analysis.Remaining);
    }

    // ---- Continuity -------------------------------------------------------

    [Fact]
    public async Task Resuming_keeps_one_session_and_one_valid_chain()
    {
        var healthy = CycleBuilder.Wired().Build();
        var session = await RunInterruptedAsync([healthy], 10, TimeSpan.FromHours(48));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        await ResumeAsync(session.Paths, analysis.Context!, [healthy], 10);

        var verification = ChainVerifier.Verify(session.Paths.RawLog);
        Assert.True(verification.Valid, verification.Reason);

        // One opening entry and one closing entry, not two of each.
        var lines = File.ReadLines(session.Paths.RawLog).ToList();
        Assert.Equal(1, lines.Count(l => l.Contains("\"k\":\"SessionStart\"", StringComparison.Ordinal)));
        Assert.Equal(1, lines.Count(l => l.Contains("\"k\":\"SessionEnd\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Monitored_time_carries_across_the_interruption()
    {
        // The whole point. Restarting the total would report a fraction of the real
        // observation window and quietly understate how long the line was watched.
        var healthy = CycleBuilder.Wired().Build();
        var session = await RunInterruptedAsync([healthy], 10, TimeSpan.FromHours(48));

        var monitoredBefore = session.Engine.Statistics.MonitoredTime;
        Assert.True(monitoredBefore > TimeSpan.Zero);

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        var resumed = await ResumeAsync(session.Paths, analysis.Context!, [healthy], 10);

        Assert.True(resumed.Statistics.IsResumed);
        Assert.True(
            resumed.Statistics.MonitoredTime > monitoredBefore,
            $"expected more than {monitoredBefore}, got {resumed.Statistics.MonitoredTime}");
    }

    [Fact]
    public async Task Incident_numbering_continues_instead_of_colliding()
    {
        // Two incidents both numbered 1 would put the report in direct contradiction
        // with the log it summarises.
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();
        var script = new[] { healthy, down, down, healthy, healthy };

        var session = await RunInterruptedAsync(script, 5, TimeSpan.FromHours(48));
        Assert.Equal(1, session.Engine.Statistics.IncidentCount);

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        var resumed = await ResumeAsync(session.Paths, analysis.Context!, script, 5);

        Assert.Equal(2, resumed.Statistics.IncidentCount);
        Assert.Equal(2, resumed.Statistics.UpstreamIncidentCount);
        Assert.Equal(2, resumed.Statistics.Incidents[0].Number);
    }

    [Fact]
    public async Task Sample_numbering_continues_instead_of_restarting()
    {
        var healthy = CycleBuilder.Wired().Build();
        var session = await RunInterruptedAsync([healthy], 10, TimeSpan.FromHours(48));

        var lastBefore = session.Engine.LastSampleSequence;
        Assert.Equal(10, lastBefore);

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        var resumed = await ResumeAsync(session.Paths, analysis.Context!, [healthy], 5);

        Assert.Equal(lastBefore + 5, resumed.LastSampleSequence);
    }

    [Fact]
    public async Task The_timeline_stays_continuous_across_the_interruption()
    {
        // Without an offset the resumed run would restart at zero and plot on top of the
        // first run in every chart and every export.
        var healthy = CycleBuilder.Wired().Build();
        var session = await RunInterruptedAsync([healthy], 10, TimeSpan.FromHours(48));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        Assert.True(analysis.Context!.ElapsedBefore > TimeSpan.Zero);

        var resumed = await ResumeAsync(session.Paths, analysis.Context, [healthy], 5);

        Assert.True(
            resumed.SessionElapsed > analysis.Context.ElapsedBefore,
            $"expected the timeline to advance past {analysis.Context.ElapsedBefore}, got {resumed.SessionElapsed}");
    }

    [Fact]
    public async Task The_interruption_is_recorded_as_a_gap_and_never_as_downtime()
    {
        var healthy = CycleBuilder.Wired().Build();
        var session = await RunInterruptedAsync([healthy], 10, TimeSpan.FromHours(48));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        var resumed = await ResumeAsync(session.Paths, analysis.Context!, [healthy], 5);

        Assert.Contains(
            File.ReadLines(session.Paths.RawLog),
            l => l.Contains("\"cause\":\"MonitorNotRunning\"", StringComparison.Ordinal));

        Assert.Equal(TimeSpan.Zero, resumed.Statistics.TotalDowntime);
        Assert.True(resumed.Statistics.GapTime >= Interruption);
    }

    [Fact]
    public async Task A_resumed_session_still_produces_a_verifiable_package()
    {
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var session = await RunInterruptedAsync([healthy, down, down, healthy], 4, TimeSpan.FromHours(48));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        await ResumeAsync(session.Paths, analysis.Context!, [healthy], 5);

        var package = Evidence.EvidencePackage.Build(session.Paths);

        Assert.True(package.Verification.Valid, package.Verification.Reason);
        Assert.True(File.Exists(Path.Combine(session.Paths.Directory, "Izvestaj.html")));
    }

    // ---- Reconstruction ---------------------------------------------------

    [Fact]
    public async Task Reconstruction_matches_the_engine_that_produced_the_log()
    {
        // The reconstructed totals have to equal what the engine actually held, or the
        // resumed session carries on from numbers nobody measured.
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var session = await RunInterruptedAsync(
            [healthy, healthy, down, down, down, healthy, healthy, down, healthy], 9, TimeSpan.FromHours(48));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        var prior = analysis.Context!.Prior;
        var actual = session.Engine.Statistics;

        Assert.Equal(actual.IncidentCount, prior.IncidentCount);
        Assert.Equal(actual.UpstreamIncidentCount, prior.UpstreamIncidentCount);
        Assert.Equal(actual.UpstreamDowntime, prior.UpstreamDowntime);
        Assert.Equal(actual.LocalDowntime, prior.LocalDowntime);
        Assert.Equal(actual.LongestUpstreamOutage, prior.LongestUpstreamOutage);
    }

    [Fact]
    public async Task Reconstruction_works_without_any_checkpoint()
    {
        // A session that died in its first minutes has no checkpoint. Replaying the whole
        // chain is slower but must give the same answer, otherwise such a session could
        // never be continued.
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();
        var noCheckpoints = new RecorderOptions { CheckpointInterval = TimeSpan.FromDays(1) };

        var session = await RunInterruptedAsync(
            [healthy, down, down, healthy, healthy], 5, TimeSpan.FromHours(48), noCheckpoints);

        Assert.DoesNotContain(
            File.ReadLines(session.Paths.RawLog),
            l => l.Contains("\"k\":\"Checkpoint\"", StringComparison.Ordinal));

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);

        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);
        Assert.Equal(session.Engine.Statistics.IncidentCount, analysis.Context!.Prior.IncidentCount);
        Assert.Equal(session.Engine.Statistics.UpstreamDowntime, analysis.Context.Prior.UpstreamDowntime);
    }

    [Fact]
    public async Task Reconstruction_survives_a_crash_mid_write()
    {
        var healthy = CycleBuilder.Wired().Build();
        var session = await RunInterruptedAsync([healthy], 10, TimeSpan.FromHours(48));

        await File.AppendAllTextAsync(session.Paths.RawLog, "{\"k\":\"Sample\",\"n\":99,\"prev\":\"dead");

        var analysis = SessionResumeAnalyzer.Analyze(_root, session.ResumedAt);
        Assert.Equal(ResumeDecision.Resumable, analysis.Decision);

        await ResumeAsync(session.Paths, analysis.Context!, [healthy], 5);

        var verification = ChainVerifier.Verify(session.Paths.RawLog);
        Assert.True(verification.Valid, verification.Reason);
    }
}
