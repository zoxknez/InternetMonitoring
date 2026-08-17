namespace IEM.Core.Time;

/// <summary>What the integrity monitor concluded about a pair of consecutive readings.</summary>
public enum ClockAnomaly
{
    /// <summary>Wall clock and monotonic clock advanced together. Nothing to report.</summary>
    None,

    /// <summary>
    /// Wall clock moved by a materially different amount than the monotonic clock.
    /// An NTP correction or a manual clock change. Recorded so the operator cannot
    /// later argue the timestamps were manipulated.
    /// </summary>
    WallClockJump,

    /// <summary>System uptime decreased, so the machine rebooted between samples.</summary>
    Reboot,
}

/// <param name="WallDelta">Wall-clock time between the two readings.</param>
/// <param name="MonotonicDelta">Authoritative elapsed time between the two readings.</param>
/// <param name="UptimeDelta">Change in system uptime; negative means a reboot.</param>
/// <param name="Skew">
/// <c>WallDelta - MonotonicDelta</c>. Non-zero means the wall clock was corrected.
/// Positive means the wall clock jumped forward.
/// </param>
public sealed record ClockObservation(
    TimeSpan WallDelta,
    TimeSpan MonotonicDelta,
    TimeSpan UptimeDelta,
    ClockAnomaly Anomaly,
    TimeSpan Skew)
{
    public bool IsAnomalous => Anomaly != ClockAnomaly.None;
}

/// <summary>
/// Watches for wall-clock corrections and reboots between samples.
/// <para>
/// This exists because a recorded outage length is only credible if the recording
/// can show that the clock did not move underneath it. Durations always come from
/// the monotonic clock; this monitor detects and reports the cases where the wall
/// clock disagreed, rather than hiding them.
/// </para>
/// </summary>
public sealed class ClockIntegrityMonitor
{
    /// <summary>
    /// How far wall and monotonic clocks may diverge between two samples before it
    /// counts as a correction. Ordinary scheduling noise is well under this.
    /// </summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _tolerance;

    private DateTimeOffset _lastWall;
    private long _lastMonotonicTicks;
    private TimeSpan _lastUptime;
    private bool _hasPrevious;

    public ClockIntegrityMonitor(TimeSpan? tolerance = null)
    {
        _tolerance = tolerance ?? DefaultTolerance;
        if (_tolerance <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "Tolerance must be positive.");
        }
    }

    /// <summary>
    /// Records a reading and compares it with the previous one.
    /// Returns <see langword="null"/> for the first reading, which has nothing to compare against.
    /// </summary>
    public ClockObservation? Observe(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var wall = clock.UtcNow;
        var monotonicTicks = clock.MonotonicTicks;
        var uptime = clock.SystemUptime;

        if (!_hasPrevious)
        {
            _lastWall = wall;
            _lastMonotonicTicks = monotonicTicks;
            _lastUptime = uptime;
            _hasPrevious = true;
            return null;
        }

        var wallDelta = wall - _lastWall;
        var monotonicDelta = clock.MonotonicElapsed(_lastMonotonicTicks, monotonicTicks);
        var uptimeDelta = uptime - _lastUptime;
        var skew = wallDelta - monotonicDelta;

        // Reboot is checked first: a reboot resets the monotonic counter too, so the
        // skew reading is meaningless and would otherwise be misreported as a jump.
        var anomaly = uptimeDelta < TimeSpan.Zero
            ? ClockAnomaly.Reboot
            : skew.Duration() > _tolerance
                ? ClockAnomaly.WallClockJump
                : ClockAnomaly.None;

        _lastWall = wall;
        _lastMonotonicTicks = monotonicTicks;
        _lastUptime = uptime;

        return new ClockObservation(wallDelta, monotonicDelta, uptimeDelta, anomaly, skew);
    }
}
