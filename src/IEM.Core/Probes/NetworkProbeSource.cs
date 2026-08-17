using System.Diagnostics;
using IEM.Core.Model;
using IEM.Core.Scheduling;
using IEM.Core.Time;

namespace IEM.Core.Probes;

/// <summary>Produces one <see cref="ProbeCycle"/> per aggregator tick.</summary>
public interface IProbeSource
{
    Task<ProbeCycle> SampleAsync(long sequence, TimeSpan budget, CancellationToken cancellationToken);

    /// <summary>
    /// Tells the source how fast the engine intends to sample, so probing can tighten
    /// during an incident and relax afterwards. Sources with no probes of their own ignore it.
    /// </summary>
    void NotifyCadence(CadencePhase phase, TimeSpan interval)
    {
        // Default: nothing to adjust.
    }
}

/// <summary>
/// Reads whatever the probes have most recently filed and assembles it into one tick.
/// <para>
/// This deliberately does no probing and awaits nothing. Probes run on their own loops in
/// <see cref="ProbeScheduler"/>; the aggregator simply looks at the shelf each time it
/// fires. That is what keeps a two-second TCP timeout from setting the pace for the whole
/// engine - the failure this class used to have, at the exact moment precision mattered
/// most.
/// </para>
/// </summary>
public sealed class NetworkProbeSource : IProbeSource, IAsyncDisposable
{
    private readonly ProbeOptions _options;
    private readonly ILinkInspector _linkInspector;
    private readonly IClock _clock;
    private readonly ObservationStore _store;
    private readonly ProbeScheduler _scheduler;
    private readonly RouterStatusCache _routerStatus;

    /// <summary>
    /// Reads the adapter's byte counters, so every sample can say how much of the link this
    /// machine was using itself. The same monitor the speed measurement gates on, for the
    /// same reason: our own traffic must not be read as the operator's fault.
    /// </summary>
    private readonly LinkActivityMonitor _activity;

    /// <summary>Answers whether our own speed measurement is loading the line right now.</summary>
    private readonly Func<bool>? _selfTestInProgress;

    private LinkSnapshot _lastLink;

    /// <summary>
    /// Last public address seen. Kept because it cannot be read while the connection is
    /// down, and the interesting question is whether it changed across an outage.
    /// </summary>
    private string? _lastPublicAddress;

    /// <param name="routes">
    /// Resolves which adapter and source address each target is reached through. Without one
    /// the measurements are unchanged but nothing can be attributed to a specific link.
    /// </param>
    /// <param name="boundIcmp">Platform sender that can pin ICMP to a source address.</param>
    /// <param name="selfTestInProgress">
    /// Asked once per tick whether this tool is deliberately saturating the connection with
    /// a speed measurement - which may be running in another process entirely, so the answer
    /// comes from the caller rather than from anything here. During it the classifier
    /// suspends assessment instead of recording the load as a fault.
    /// </param>
    public NetworkProbeSource(
        ProbeOptions? options = null,
        ILinkInspector? linkInspector = null,
        IClock? clock = null,
        IRouteResolver? routes = null,
        IBoundIcmp? boundIcmp = null,
        Func<bool>? selfTestInProgress = null)
    {
        _selfTestInProgress = selfTestInProgress;

        _options = options ?? ProbeOptions.Default;
        _linkInspector = linkInspector ?? new SystemLinkInspector();
        _clock = clock ?? SystemClock.Instance;

        // Seeded before anything starts, so the first deep round already knows which
        // resolver the operator handed out.
        _lastLink = _linkInspector.Inspect();

        _store = new ObservationStore(_clock);
        _activity = new LinkActivityMonitor(_clock);

        _scheduler = new ProbeScheduler(
            _store, _options, _clock, () => _lastLink, httpClient: null, routes, boundIcmp);

        _scheduler.Start();

        _routerStatus = new RouterStatusCache(_clock);
        _routerStatus.Start();
    }

    /// <summary>The moment the fast probes first saw trouble, or null while all is well.</summary>
    public long? SuspicionStartedAtTicks => _store.SuspicionStartedAtTicks;

    public void NotifyCadence(CadencePhase phase, TimeSpan interval) => _scheduler.SetPhase(phase, interval);

    public Task<ProbeCycle> SampleAsync(long sequence, TimeSpan budget, CancellationToken cancellationToken)
    {
        var wall = _clock.UtcNow;
        var monotonic = _clock.MonotonicTicks;
        var started = Stopwatch.GetTimestamp();

        var link = EnrichWithRouterStatus(_linkInspector.Inspect());
        _lastLink = link;

        var results = _store.Snapshot();

        // What the machine itself is putting through the link, read from the adapter's own
        // counters. One reading per tick, because the connection being slow and the computer
        // using all of it look identical from here - and only one of the two is the
        // operator's fault. The first call has no interval to measure over and returns null,
        // which is carried through as "not known" rather than as a quiet line.
        var activity = _activity.Sample(link.InterfaceId);

        var duration = Stopwatch.GetElapsedTime(started);

        var cycle = new ProbeCycle(sequence, wall, monotonic, link, results, duration)
        {
            // Recorded rather than absorbed. The tick reads a shelf, so overrunning now
            // means something is wrong with the machine, not with the network.
            Overran = duration > budget,
            LocalTrafficBytesPerSecond = activity?.BytesPerSecond,

            // Asked every tick rather than latched, so a measurement that ends mid-session
            // hands assessment straight back instead of leaving a hole in the record.
            SelfTestRunning = SelfTestRunning(),
        };

        // Trouble on the cheap probes means the slow ones need to be current. Asking costs
        // this tick nothing - the request is a flag, and the deep loop picks it up.
        if (!cycle.AnyExternalReachability || cycle.ExternalIcmp.SomeFailed || cycle.Gateway.AllFailed)
        {
            _scheduler.RequestDeepRound();
            _routerStatus.RequestUrgentRead();
        }

        return Task.FromResult(cycle);
    }

    /// <summary>
    /// Whether our own measurement is loading the line, with a failure to answer treated as
    /// "no".
    /// <para>
    /// That direction on purpose: the alternative is a broken check silencing the monitoring,
    /// which would turn a safeguard into a session that records nothing and reports that
    /// everything was fine.
    /// </para>
    /// </summary>
    private bool SelfTestRunning()
    {
        try
        {
            return _selfTestInProgress?.Invoke() == true;
        }
#pragma warning disable CA1031 // A caller's faulty predicate must not stop the monitoring.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>
    /// Folds in what the router says about itself, and remembers the public address.
    /// <para>
    /// A different address after an outage means the WAN session was re-established, which
    /// is the operator's side of the link rather than the customer's.
    /// </para>
    /// </summary>
    private LinkSnapshot EnrichWithRouterStatus(LinkSnapshot link)
    {
        var reading = _routerStatus.Current();

        if (reading.Status?.ExternalAddress is { Length: > 0 } address)
        {
            _lastPublicAddress = address;
        }

        return link with
        {
            Router = reading.Status,
            RouterReconnected = reading.Reconnected,
            RouterChecked = reading.Status is not null,
            PublicAddress = _lastPublicAddress,
        };
    }

    /// <summary>
    /// Stops all probing and waits for whatever is in flight.
    /// <para>
    /// Idempotent and called explicitly at the start of closing a session. Probing must stop
    /// before the evidence is written, or a probe that answers late lands in the chain after
    /// the entry that closes it - a record saying the session ended and then carried on.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _scheduler.DisposeAsync().ConfigureAwait(false);
        await _routerStatus.DisposeAsync().ConfigureAwait(false);
    }

    private bool _disposed;
}
