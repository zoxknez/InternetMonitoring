using IEM.Core.Classification;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Core.Scheduling;
using IEM.Core.Time;

namespace IEM.Core;

/// <param name="Instant">Monotonic and wall-clock position of this sample.</param>
public sealed record MonitorSample(
    long Sequence,
    SampleInstant Instant,
    ProbeCycle Cycle,
    SampleVerdict Verdict,
    CadencePhase Phase,
    ClockObservation? Clock);

/// <param name="Cause">Why sampling stopped, as far as can be told.</param>
public sealed record MonitoringGapEvent(SampleInstant DetectedAt, TimeSpan Duration, GapCause Cause);

/// <summary>
/// The connection under test stopped being the same connection.
/// <para>
/// Not an error, and not something to hide. Someone who moves a laptop from home Wi-Fi to a
/// phone hotspot has recorded two different networks perfectly accurately; a report that
/// presents that as one connection is the thing that would be wrong.
/// </para>
/// </summary>
/// <param name="Previous">Null for the opening record, which establishes the baseline.</param>
public sealed record NetworkEnvironmentChange(
    SampleInstant At,
    NetworkEnvironment? Previous,
    NetworkEnvironment Current,
    IReadOnlyList<string> Differences)
{
    /// <summary>The environment as first observed, rather than a change to it.</summary>
    public bool IsBaseline => Previous is null;
}

public enum GapCause
{
    /// <summary>Sampling stopped for a while and nothing explains it further.</summary>
    Unknown,

    /// <summary>System uptime went backwards, so the machine restarted.</summary>
    Reboot,

    /// <summary>The wall clock was corrected, which is why the gap looked like one.</summary>
    ClockAdjustment,

    /// <summary>The machine went to sleep, as reported by the operating system.</summary>
    Sleep,

    /// <summary>
    /// The monitor itself was not running: the service was restarted, or the machine was
    /// off. Distinct from a sleep gap because the cause is known precisely.
    /// </summary>
    MonitorNotRunning,
}

/// <summary>
/// State carried over when a session is picked up after an interruption.
/// <para>
/// Without this, a service restart part-way through a two-day test would silently reset
/// availability, renumber incidents from one, and restart sample sequence numbers - all
/// of which would put the report in direct contradiction with the raw log it claims to
/// summarise.
/// </para>
/// </summary>
/// <param name="Interruption">Wall-clock time during which the monitor was not running.</param>
public sealed record ResumeContext(
    PriorTotals Prior,
    int ClosedIncidentCount,
    long LastSampleSequence,
    TimeSpan Interruption)
{
    /// <summary>
    /// Session-relative time already elapsed before this run began.
    /// <para>
    /// Added to every sample's monotonic position so the session keeps one continuous
    /// timeline. Without it the second run would restart at zero and its samples would
    /// plot on top of the first run's in every chart and every export.
    /// </para>
    /// </summary>
    public TimeSpan ElapsedBefore { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// An outage that was still in progress when the previous run ended, reconstructed from
    /// the raw chain.
    /// <para>
    /// The failing samples were written to the chain as they happened, but the segment that
    /// summarises them is only written when it closes - so a crash mid-outage left the
    /// evidence in the log and nothing in the statistics. Restoring it here means the
    /// segment is closed properly by the pause, and counted.
    /// </para>
    /// </summary>
    public OpenIncidentState? OpenIncident { get; init; }

    /// <summary>
    /// The connection this session was measuring, as last recorded.
    /// <para>
    /// Carried across so the comparison survives the interruption. Without it the check
    /// restarts from whatever is present after the restart, and a router swapped out during
    /// the interruption - which is exactly when routers get swapped - passes unremarked.
    /// </para>
    /// </summary>
    public NetworkEnvironment? Environment { get; init; }
}

/// <summary>An outage in progress at the moment the previous run stopped.</summary>
public sealed record OpenIncidentState(
    SampleInstant? LastGood,
    SampleInstant FirstBad,
    SampleInstant LastBad,
    IReadOnlyList<NetworkState> StatesSeen,
    int SampleCount,
    string TechnicalDetail);

public sealed record MonitorOptions
{
    public static readonly MonitorOptions Default = new();

    public ProbeOptions Probes { get; init; } = ProbeOptions.Default;

    public CadenceOptions Cadence { get; init; } = CadenceOptions.Default;

    public ClassifierOptions Classifier { get; init; } = ClassifierOptions.Default;

    /// <summary>
    /// Absolute floor for calling a sampling pause a gap. The effective threshold is the
    /// larger of this and a multiple of the current interval, so a 100 ms cadence does not
    /// report a gap every time the scheduler hiccups.
    /// </summary>
    public TimeSpan MinimumGapThreshold { get; init; } = TimeSpan.FromSeconds(5);

    public int GapIntervalMultiplier { get; init; } = 5;

    /// <summary>Samples kept for the jitter calculation.</summary>
    public int JitterWindow { get; init; } = 16;
}

/// <summary>
/// Drives the sampling loop: probe, classify, adjust cadence, detect incidents, keep score.
/// <para>
/// Losing connectivity must never stop the monitor - that is the entire point - so every
/// step is written to survive failure and carry on recording.
/// </para>
/// </summary>
public sealed class MonitorEngine
{
    private readonly MonitorOptions _options;
    private readonly IProbeSource _probeSource;
    private readonly IClock _clock;
    private readonly StateClassifier _classifier;
    private readonly AdaptiveCadenceController _cadence;
    private readonly IncidentDetector _incidents;
    private readonly ClockIntegrityMonitor _clockIntegrity = new();
    private readonly IncidentEvidenceCollector _evidence = new();
    private readonly Queue<double> _recentRoundTrips = new();
    private readonly ResumeContext? _resume;

    private readonly TimeSpan _elapsedBefore;

    private long _sequence;
    private long _startTicks;
    private bool _started;
    private TimeSpan? _previousSampleAt;
    private SampleVerdict _previousVerdict = new(NetworkState.Ok, "Session start");
    private string? _previousBssid;
    private NetworkEnvironment? _baselineEnvironment;
    private NetworkEnvironment? _currentEnvironment;
    private double? _previousRoundTripMs;
    private double _jitterEstimateMs;

    /// <summary>Set by the host when the operating system reports a suspend.</summary>
    private volatile bool _suspendObserved;

    public MonitorEngine(
        IProbeSource probeSource,
        MonitorOptions? options = null,
        IClock? clock = null,
        ResumeContext? resume = null)
    {
        _probeSource = probeSource ?? throw new ArgumentNullException(nameof(probeSource));
        _options = options ?? MonitorOptions.Default;
        _clock = clock ?? SystemClock.Instance;
        _classifier = new StateClassifier(_options.Classifier);
        _cadence = new AdaptiveCadenceController(_options.Cadence);
        _resume = resume;

        _incidents = new IncidentDetector(resume?.ClosedIncidentCount ?? 0);

        // Seeded from the chain, so the first sample after a restart is compared against the
        // connection the session began on rather than establishing a fresh baseline.
        _baselineEnvironment = resume?.Environment;
        _currentEnvironment = resume?.Environment;

        if (resume?.OpenIncident is { } open)
        {
            _incidents.RestoreOpenIncident(
                open.LastGood, open.FirstBad, open.LastBad,
                open.StatesSeen, open.SampleCount, open.TechnicalDetail);
        }

        _sequence = resume?.LastSampleSequence ?? 0;
        _elapsedBefore = resume?.ElapsedBefore ?? TimeSpan.Zero;
        Statistics = new SessionStatistics(resume?.Prior);
    }

    /// <summary>Session-relative elapsed time carried in from earlier runs.</summary>
    public TimeSpan ElapsedBefore => _elapsedBefore;

    /// <summary>Session-relative elapsed time, including anything carried in.</summary>
    /// <remarks>
    /// Guarded by an explicit flag rather than by testing the start tick against zero.
    /// Zero is a perfectly legitimate reading from a monotonic counter, and treating it as
    /// "not started" made every checkpoint record an elapsed time of nothing - which in
    /// turn made a resumed session believe it had never monitored anything.
    /// </remarks>
    public TimeSpan SessionElapsed =>
        _started ? _elapsedBefore + _clock.MonotonicElapsedSince(_startTicks) : _elapsedBefore;

    /// <summary>Sequence number of the most recent sample.</summary>
    public long LastSampleSequence => _sequence;

    public SessionStatistics Statistics { get; }

    /// <summary>
    /// Tells the engine the operating system reported a suspend, so the pause that follows
    /// is attributed to sleep rather than filed under an unexplained gap.
    /// <para>
    /// Called from a power-event callback on another thread, hence the volatile flag: the
    /// sampling loop reads it on the next iteration, whenever that turns out to be.
    /// </para>
    /// </summary>
    public void NotifySuspending() => _suspendObserved = true;

    public event Action<MonitorSample>? SampleRecorded;

    public event Action<IncidentRecord>? IncidentClosed;

    public event Action<MonitoringGapEvent>? GapDetected;

    public event Action<ClockObservation>? ClockAnomalyDetected;

    public event Action<NetworkEnvironmentChange>? NetworkEnvironmentChanged;

    /// <summary>The environment as first observed, which the session's evidence is about.</summary>
    public NetworkEnvironment? BaselineEnvironment => _baselineEnvironment;

    /// <summary>
    /// Runs until <paramref name="duration"/> elapses or the token is cancelled.
    /// Pass <see cref="Timeout.InfiniteTimeSpan"/> to run until stopped.
    /// </summary>
    public async Task RunAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        _startTicks = _clock.MonotonicTicks;
        _started = true;

        RecordResumeInterruption();

        while (!cancellationToken.IsCancellationRequested)
        {
            var elapsed = _clock.MonotonicElapsedSince(_startTicks);
            if (duration != Timeout.InfiniteTimeSpan && elapsed >= duration)
            {
                break;
            }

            var cycleStart = _clock.MonotonicTicks;
            var interval = _cadence.Interval;

            try
            {
                await StepAsync(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var spent = _clock.MonotonicElapsed(cycleStart, _clock.MonotonicTicks);
            var remaining = _cadence.Interval - spent;

            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Finish();
    }

    /// <summary>
    /// Books the time the monitor was not running as a gap, and announces it so it reaches
    /// the raw log.
    /// <para>
    /// Left unrecorded, the stretch between two runs would silently vanish from the
    /// session: it is not uptime, and it is emphatically not downtime the operator caused,
    /// but the report still has to account for where those minutes went.
    /// </para>
    /// </summary>
    private void RecordResumeInterruption()
    {
        if (_resume is not { Interruption.Ticks: > 0 } resume)
        {
            return;
        }

        Statistics.RecordInterruption(resume.Interruption);

        // An outage reconstructed from the chain ends where the previous run stopped
        // watching - which is before the interruption, not after it. Measuring to the resume
        // instead would charge the whole interruption to the operator, the very thing the
        // pause is supposed to prevent.
        var cut = _incidents.ObserveGap(_elapsedBefore - resume.Interruption);
        if (cut is not null)
        {
            Statistics.RecordIncident(cut);
            IncidentClosed?.Invoke(cut);
        }

        var instant = new SampleInstant(TimeSpan.Zero, _clock.UtcNow);
        GapDetected?.Invoke(new MonitoringGapEvent(instant, resume.Interruption, CauseOfInterruption(resume)));
    }

    /// <summary>
    /// Why monitoring was not running, as far as the machine can say.
    /// <para>
    /// A machine that has been up for less time than the pause lasted must have restarted
    /// during it - it cannot have been running the whole time and simply missed it. That is
    /// worth distinguishing: "the computer restarted" is an ordinary explanation an operator
    /// accepts without comment, while "monitoring was not running" invites the question of
    /// why not, and puts the customer on the back foot over a gap they did not cause.
    /// </para>
    /// <para>
    /// The engine already knows this. It was simply not being asked on the resume path,
    /// which reported every interruption as an unexplained absence of monitoring.
    /// </para>
    /// </summary>
    private GapCause CauseOfInterruption(ResumeContext resume) =>
        _clock.SystemUptime < resume.Interruption
            ? GapCause.Reboot
            : GapCause.MonitorNotRunning;

    private async Task StepAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var sequence = ++_sequence;
        var cycle = await _probeSource.SampleAsync(sequence, budget, cancellationToken).ConfigureAwait(false);

        // Offset by whatever the session had already run for, so a resumed session keeps
        // one continuous timeline instead of two overlapping ones.
        var now = _elapsedBefore + _clock.MonotonicElapsed(_startTicks, cycle.MonotonicTicks);
        var instant = new SampleInstant(now, cycle.WallUtc);

        var clockObservation = _clockIntegrity.Observe(_clock);
        if (clockObservation?.IsAnomalous == true)
        {
            ClockAnomalyDetected?.Invoke(clockObservation);
        }

        AccountForElapsedTime(instant, budget, clockObservation);

        TrackEnvironment(cycle, instant);

        var context = BuildContext(cycle);
        var verdict = _classifier.Classify(cycle, context);

        // Evidence has to be gathered while the outage is happening. Nearly every signal is
        // of the form "did this hold throughout", which cannot be reconstructed afterwards
        // from a duration and a state name.
        if (verdict.IsOutage)
        {
            if (!_evidence.IsCollecting)
            {
                _evidence.Begin();
            }

            _evidence.Observe(cycle, context, clockObservation?.IsAnomalous == true);
        }
        else if (verdict.State != NetworkState.MonitoringGap)
        {
            _evidence.ObserveHealthy(cycle);
        }

        // The adapter goes in with the sample, so a segment records which link carried
        // traffic at each of its boundaries. Without it, Windows failing over from a dead
        // Wi-Fi to a live Ethernet reads as a two-second recovery - short, convincing, and
        // entirely false.
        var closed = _incidents.Observe(instant, verdict, cycle.AgreedInterfaceId);

        if (closed is not null)
        {
            closed = Score(closed, cycle);
            Statistics.RecordIncident(closed);
            IncidentClosed?.Invoke(closed);
        }

        var decision = _cadence.Observe(verdict, now);

        // The probes run on their own loops now, so they have to be told when to tighten.
        // Without this the engine would sample every hundred milliseconds while the probes
        // underneath it carried on once a second, and every tick would read the same shelf.
        _probeSource.NotifyCadence(decision.Phase, decision.Interval);

        _previousVerdict = verdict;
        _previousSampleAt = now;
        _previousBssid = cycle.Link.Wireless?.Bssid ?? _previousBssid;

        SampleRecorded?.Invoke(new MonitorSample(sequence, instant, cycle, verdict, decision.Phase, clockObservation));
    }

    /// <summary>
    /// Attributes the interval since the previous sample.
    /// <para>
    /// The interval is credited to the previous verdict rather than this one, which is the
    /// conservative direction: it under-states rather than over-states how long a fault
    /// lasted. Downtime totals come from incident records anyway, so the headline figure
    /// and the incident table can never disagree.
    /// </para>
    /// </summary>
    private void AccountForElapsedTime(SampleInstant instant, TimeSpan budget, ClockObservation? clockObservation)
    {
        if (_previousSampleAt is not { } previous)
        {
            return;
        }

        var delta = instant.Monotonic - previous;
        var threshold = Max(_options.MinimumGapThreshold, budget * _options.GapIntervalMultiplier);
        var isGap = delta > threshold;

        Statistics.RecordInterval(delta, isGap, _previousVerdict.Severity);

        if (!isGap)
        {
            return;
        }

        // A suspend the operating system told us about beats any inference from the clock,
        // and it is consumed here so the next unexplained pause is not also blamed on sleep.
        var suspended = _suspendObserved;
        _suspendObserved = false;

        var cause = clockObservation?.Anomaly switch
        {
            ClockAnomaly.Reboot => GapCause.Reboot,
            ClockAnomaly.WallClockJump when !suspended => GapCause.ClockAdjustment,
            _ => suspended ? GapCause.Sleep : GapCause.Unknown,
        };

        _evidence.NoteMonitoringPaused(
            sleep: cause == GapCause.Sleep,
            clockAdjusted: cause == GapCause.ClockAdjustment);

        // The pause closes whatever segment was in progress, at the point observation
        // stopped. Letting it stay open would charge the whole pause to the operator: six
        // hours of a sleeping laptop would be reported as six hours without service.
        var cut = _incidents.ObserveGap(previous);
        if (cut is not null)
        {
            // No recovery cycle: service was never observed to return, only observation to
            // stop. Passing the current cycle would compare a public address read after the
            // pause against one read before it and call any difference significant.
            cut = Score(cut, recovery: null);
            Statistics.RecordIncident(cut);
            IncidentClosed?.Invoke(cut);
        }

        GapDetected?.Invoke(new MonitoringGapEvent(instant, delta, cause));
    }

    /// <summary>
    /// Attaches how far the evidence goes towards this segment being what it was called.
    /// <para>
    /// Two numbers rather than one, and the reason is the failure mode they replace: two
    /// clean signals out of nineteen used to come out as a hundred percent and the label
    /// VERY HIGH. Coverage is what stops that, and an operator noticing that seventeen
    /// checks never ran is exactly how a report built on the old figure would have been
    /// taken apart.
    /// </para>
    /// </summary>
    /// <param name="recovery">The cycle on which service returned, when there was one.</param>
    private IncidentRecord Score(IncidentRecord incident, ProbeCycle? recovery)
    {
        var evidence = _evidence.Build(recovery);

        return incident with
        {
            Confidence = ConfidenceScorer.Score(incident.WorstState, evidence),

            // Carried out of the evidence and onto the record, so the report can say how busy
            // the machine itself was rather than only that the check did not come out clean.
            PeakLocalTrafficBytesPerSecond = evidence.PeakLocalTrafficBytesPerSecond,
        };
    }

    /// <summary>
    /// Watches for the connection under test turning into a different connection.
    /// <para>
    /// Over forty-eight hours this is not hypothetical: a laptop gets carried to another
    /// room and roams, a docking station is plugged in, a phone hotspot takes over. Each is
    /// a legitimate thing to record and an illegitimate thing to present as one continuous
    /// measurement of one link.
    /// </para>
    /// </summary>
    private void TrackEnvironment(ProbeCycle cycle, SampleInstant instant)
    {
        // A down or missing adapter reports almost nothing, and treating that emptiness as
        // a changed environment would raise the alarm on every outage the tool exists to
        // measure. The comparison resumes when there is something to compare.
        if (cycle.Link.Status == LinkStatus.Missing || string.IsNullOrEmpty(cycle.Link.InterfaceId))
        {
            return;
        }

        var observed = NetworkEnvironment.From(
            cycle.Link,
            cycle.AgreedSourceAddress is { } source ? [source] : [],
            cycle.MultiplePathsInUse);

        if (_currentEnvironment is null)
        {
            _baselineEnvironment = observed;
            _currentEnvironment = observed;

            // Announced, not merely stored. The baseline has to reach the raw log, or the
            // report has no record of what connection it is even about.
            NetworkEnvironmentChanged?.Invoke(
                new NetworkEnvironmentChange(instant, null, observed, []));

            return;
        }

        if (string.Equals(observed.Fingerprint, _currentEnvironment.Fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        var differences = observed.DifferencesFrom(_currentEnvironment);
        var previous = _currentEnvironment;
        _currentEnvironment = observed;

        if (differences.Count > 0)
        {
            NetworkEnvironmentChanged?.Invoke(
                new NetworkEnvironmentChange(instant, previous, observed, differences));
        }
    }

    private ClassificationContext BuildContext(ProbeCycle cycle)
    {
        UpdateJitter(cycle);

        var bssid = cycle.Link.Wireless?.Bssid;
        var roamed = bssid is not null && _previousBssid is not null && bssid != _previousBssid;

        return new ClassificationContext
        {
            BssidChanged = roamed,
            Jitter = _recentRoundTrips.Count >= 2 ? TimeSpan.FromMilliseconds(_jitterEstimateMs) : null,

            // The router's own WAN uptime going backwards means it re-established the
            // connection. That is the difference between a fault on the operator's line
            // and a router that restarted itself - and only the router can tell us.
            CpeRebootDetected = cycle.Link.RouterReconnected,
        };
    }

    /// <summary>
    /// Smoothed mean deviation between consecutive round trips, in the spirit of the
    /// RTP interarrival jitter estimate. A single spike should not read as instability.
    /// </summary>
    private void UpdateJitter(ProbeCycle cycle)
    {
        if (cycle.AverageExternalRoundTrip is not { } rtt)
        {
            return;
        }

        var current = rtt.TotalMilliseconds;

        if (_previousRoundTripMs is { } previous)
        {
            var deviation = Math.Abs(current - previous);
            _jitterEstimateMs += (deviation - _jitterEstimateMs) / 16d;
        }

        _previousRoundTripMs = current;
        _recentRoundTrips.Enqueue(current);

        while (_recentRoundTrips.Count > _options.JitterWindow)
        {
            _recentRoundTrips.Dequeue();
        }
    }

    private void Finish()
    {
        var open = _incidents.CloseOpenIncident();
        if (open is null)
        {
            return;
        }

        // Still failing when monitoring stopped, so there is no recovery cycle to read.
        open = Score(open, recovery: null);

        Statistics.RecordIncident(open);
        IncidentClosed?.Invoke(open);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
