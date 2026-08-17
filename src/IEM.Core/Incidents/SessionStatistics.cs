using IEM.Core.Model;

namespace IEM.Core.Incidents;

/// <summary>
/// Totals carried over from an earlier run of the same session.
/// <para>
/// A 48-hour test that survives a reboot has to keep counting where it left off. Starting
/// availability again from zero after an interruption would quietly report a fraction of
/// the real observation window, and renumbering incidents from one would make the report
/// contradict the raw log it was built from.
/// </para>
/// </summary>
public sealed record PriorTotals(
    TimeSpan MonitoredTime,
    TimeSpan GapTime,
    TimeSpan DegradedTime,
    TimeSpan UpstreamDowntime,
    TimeSpan LocalDowntime,
    int IncidentCount,
    int UpstreamIncidentCount,
    TimeSpan LongestUpstreamOutage)
{
    public static readonly PriorTotals None = new(
        TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0, TimeSpan.Zero);

    public bool IsEmpty => this == None;
}

/// <summary>
/// Running totals for a monitoring session.
/// <para>
/// The critical decision here is what availability is measured against. Wall-clock
/// session length is the wrong denominator: if the machine slept for six hours, nothing
/// was observed, and counting those hours as uptime inflates the figure in the
/// customer's favour - which is exactly the kind of flaw that discredits an otherwise
/// sound report. So availability is computed over time actually monitored, and gaps are
/// reported separately as neither uptime nor downtime.
/// </para>
/// <para>
/// Downtime is accumulated from closed incidents rather than from samples, so the
/// headline percentage and the incident table can never disagree.
/// </para>
/// </summary>
public sealed class SessionStatistics(PriorTotals? prior = null)
{
    private readonly PriorTotals _prior = prior ?? PriorTotals.None;
    private readonly List<IncidentRecord> _incidents = [];

    private TimeSpan _monitoredThisRun;
    private TimeSpan _gapThisRun;
    private TimeSpan _degradedThisRun;
    private TimeSpan _upstreamDowntimeThisRun;
    private TimeSpan _localDowntimeThisRun;

    /// <summary>Totals carried in from an earlier run, if this session was resumed.</summary>
    public PriorTotals Prior => _prior;

    public bool IsResumed => !_prior.IsEmpty;

    /// <summary>Time during which sampling was actually running, across the whole session.</summary>
    public TimeSpan MonitoredTime => _prior.MonitoredTime + _monitoredThisRun;

    /// <summary>Time during which nothing was observed: sleep, reboot, or a starved process.</summary>
    public TimeSpan GapTime => _prior.GapTime + _gapThisRun;

    /// <summary>Time spent in a degraded but usable state. Not counted as downtime.</summary>
    public TimeSpan DegradedTime => _prior.DegradedTime + _degradedThisRun;

    /// <summary>Total elapsed time including gaps. Shown alongside, never used as the denominator.</summary>
    public TimeSpan WallClockTime => MonitoredTime + GapTime;

    /// <summary>Incidents closed during this run. Earlier ones live in the raw log and the index.</summary>
    public IReadOnlyList<IncidentRecord> Incidents => _incidents;

    /// <summary>Incidents across the whole session, including any from before a restart.</summary>
    public int IncidentCount => _prior.IncidentCount + _incidents.Count;

    /// <summary>Downtime whose cause sits upstream of the customer's equipment.</summary>
    public TimeSpan UpstreamDowntime => _prior.UpstreamDowntime + _upstreamDowntimeThisRun;

    /// <summary>Downtime caused by the customer's own adapter, Wi-Fi link, or router.</summary>
    public TimeSpan LocalDowntime => _prior.LocalDowntime + _localDowntimeThisRun;

    public TimeSpan TotalDowntime => UpstreamDowntime + LocalDowntime;

    public int UpstreamIncidentCount =>
        _prior.UpstreamIncidentCount + _incidents.Count(i => i.IsUpstream);

    public TimeSpan LongestUpstreamOutage
    {
        get
        {
            var thisRun = _incidents
                .Where(i => i.IsUpstream)
                .Select(i => i.DurationReported)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();

            return thisRun > _prior.LongestUpstreamOutage ? thisRun : _prior.LongestUpstreamOutage;
        }
    }

    /// <summary>
    /// Availability over monitored time. Returns 100 when nothing has been monitored yet,
    /// since claiming any other figure from no observations would be an invention.
    /// </summary>
    public double AvailabilityPercent =>
        MonitoredTime <= TimeSpan.Zero
            ? 100d
            : Math.Clamp(100d * (MonitoredTime - TotalDowntime) / MonitoredTime, 0d, 100d);

    /// <summary>
    /// Availability counting only faults attributable upstream. This is the figure that
    /// belongs in a complaint; the overall figure includes the customer's own equipment.
    /// </summary>
    public double UpstreamAvailabilityPercent =>
        MonitoredTime <= TimeSpan.Zero
            ? 100d
            : Math.Clamp(100d * (MonitoredTime - UpstreamDowntime) / MonitoredTime, 0d, 100d);

    /// <summary>Accounts for the interval between two consecutive samples.</summary>
    public void RecordInterval(TimeSpan delta, bool isGap, Severity severity)
    {
        if (delta <= TimeSpan.Zero)
        {
            return;
        }

        if (isGap)
        {
            _gapThisRun += delta;
            return;
        }

        _monitoredThisRun += delta;

        if (severity == Severity.Degraded)
        {
            _degradedThisRun += delta;
        }
    }

    /// <summary>
    /// Records a stretch during which the monitor was not running at all - the machine was
    /// off, or the service was restarted. Neither uptime nor downtime, exactly like a
    /// sleep gap, because nothing was being observed either way.
    /// </summary>
    public void RecordInterruption(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
        {
            _gapThisRun += duration;
        }
    }

    public void RecordIncident(IncidentRecord incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        _incidents.Add(incident);

        if (incident.IsUpstream)
        {
            _upstreamDowntimeThisRun += incident.DurationReported;
        }
        else
        {
            _localDowntimeThisRun += incident.DurationReported;
        }
    }
}
