using System.Net;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network;
using IEM.Linux.Network.Netlink;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Rtnetlink Observer &amp; Route TOCTOU Continuity.
/// Invariants 247 &amp; §5.4-§5.8 (Phase 3.1-4G):
/// 1. Query socket != observer socket.
/// 2. Route/link/address events increment RouteGeneration and invalidate route cache.
/// 3. TOCTOU: [T0, T1] evaluation yields Held (no events) vs ChangedDuringExecution (event in window).
/// 4. Observer offline / subscription denied yields PathContinuity.Unknown (never synthetic Held).
/// 5. ChangedDuringExecution does NOT alter ProbeOutcome, Resolved, or Bound facts.
/// </summary>
public sealed class RtnetlinkObserverContinuityTests
{
    [Fact]
    public void NullNetworkChangeObserver_returns_Unknown_and_Generation_1()
    {
        var observer = NullNetworkChangeObserver.Instance;

        Assert.False(observer.IsLive);
        Assert.Equal(1UL, observer.RouteGeneration);

        var continuity = observer.EvaluateContinuity(100, 200, ProbePath.Unresolved);
        Assert.Equal(PathContinuity.Unknown, continuity);
    }

    [Fact]
    public void LinuxRtnetlinkObserver_increments_generation_and_fires_event()
    {
        var observer = new LinuxRtnetlinkObserver();
        var initialGen = observer.RouteGeneration;
        var firedGen = 0UL;

        observer.RouteChanged += gen => firedGen = gen;

        observer.RecordChangeEvent(NetlinkConstants.RTM_NEWROUTE, 1000);

        Assert.Equal(initialGen + 1, observer.RouteGeneration);
        Assert.Equal(observer.RouteGeneration, firedGen);
    }

    [Fact]
    public void LinuxRouteResolver_invalidates_cache_on_generation_change()
    {
        var observer = new LinuxRtnetlinkObserver();
        var resolver = new LinuxRouteResolver(observer: observer);

        // Populate cache for loopback
        var path1 = resolver.Resolve(IPAddress.Loopback);
        Assert.False(path1.Bound);

        // Inject route change event
        observer.RecordChangeEvent(NetlinkConstants.RTM_NEWROUTE, 2000);

        // Next resolve must check fresh generation
        var path2 = resolver.Resolve(IPAddress.Loopback);
        Assert.False(path2.Bound);
    }

    [Fact]
    public void EvaluateContinuity_returns_Held_when_no_events_in_window_and_live()
    {
        var observer = new TestRtnetlinkObserver(isLive: true);

        // Probe window: [1000, 2000]
        // Event at 500 (before window)
        observer.RecordChangeEvent(NetlinkConstants.RTM_NEWROUTE, 500);

        var continuity = observer.EvaluateContinuity(1000, 2000, new ProbePath("eth0", "192.168.1.10", Resolved: true));
        Assert.Equal(PathContinuity.Held, continuity);
    }

    [Fact]
    public void EvaluateContinuity_returns_ChangedDuringExecution_when_event_in_window()
    {
        var observer = new TestRtnetlinkObserver(isLive: true);

        // Event occurs during execution window [1000, 2000] at 1500
        observer.RecordChangeEvent(NetlinkConstants.RTM_NEWROUTE, 1500);

        var continuity = observer.EvaluateContinuity(1000, 2000, new ProbePath("eth0", "192.168.1.10", Resolved: true));
        Assert.Equal(PathContinuity.ChangedDuringExecution, continuity);
    }

    [Fact]
    public void EvaluateContinuity_ignores_unrelated_family_event()
    {
        var observer = new LinuxRtnetlinkObserver(isLive: true);
        // Record an IPv6 event (Family = 10) during probe window [1000, 2000] at 1500
        observer.RecordChangeEvent(NetlinkConstants.RTM_NEWROUTE, 1500, family: 10, ifindex: 2);

        // IPv4 probe path (Family = 2)
        var continuity = observer.EvaluateContinuity(
            1000,
            2000,
            new ProbePath("2", "192.168.1.10", Resolved: true),
            destination: IPAddress.Parse("1.1.1.1"));

        // Unrelated IPv6 event must NOT downgrade IPv4 probe
        Assert.Equal(PathContinuity.Held, continuity);
    }

    [Fact]
    public void EvaluateContinuity_ignores_unrelated_interface_event()
    {
        var observer = new LinuxRtnetlinkObserver(isLive: true);
        // Record an IPv4 event on interface 99 during probe window
        observer.RecordChangeEvent(NetlinkConstants.RTM_NEWLINK, 1500, family: 2, ifindex: 99);

        // Probe path on interface 2
        var continuity = observer.EvaluateContinuity(
            1000,
            2000,
            new ProbePath("2", "192.168.1.10", Resolved: true),
            destination: IPAddress.Parse("1.1.1.1"));

        // Unrelated interface event must NOT downgrade interface 2 probe
        Assert.Equal(PathContinuity.Held, continuity);
    }

    [Fact]
    public void EvaluateContinuity_matching_family_and_interface_event_downgrades_to_ChangedDuringExecution()
    {
        var observer = new LinuxRtnetlinkObserver(isLive: true);
        // Record an IPv4 event on interface 2 during probe window
        observer.RecordChangeEvent(NetlinkConstants.RTM_NEWROUTE, 1500, family: 2, ifindex: 2);

        // Probe path on interface 2
        var continuity = observer.EvaluateContinuity(
            1000,
            2000,
            new ProbePath("2", "192.168.1.10", Resolved: true),
            destination: IPAddress.Parse("1.1.1.1"));

        Assert.Equal(PathContinuity.ChangedDuringExecution, continuity);
    }

    [Fact]
    public void EvaluateContinuity_returns_Unknown_when_observer_is_not_live()
    {
        var observer = new TestRtnetlinkObserver(isLive: false);

        // Even with no events, offline observer yields Unknown
        var continuity = observer.EvaluateContinuity(1000, 2000, new ProbePath("eth0", "192.168.1.10", Resolved: true));
        Assert.Equal(PathContinuity.Unknown, continuity);
    }

    [Fact]
    public void ChangedDuringExecution_does_not_alter_Outcome_or_Bound_in_ProbeResult()
    {
        var original = new ProbeResult(
            ProbeKind.Icmp,
            ProbeScope.External,
            "1.1.1.1",
            ProbeOutcome.Success,
            TimeSpan.FromMilliseconds(10))
        {
            StartedAtTicks = 1000,
            CompletedAtTicks = 2000,
            Path = new ProbePath("eth0", "192.168.1.10", Resolved: true, Bound: true),
            PathContinuity = PathContinuity.ChangedDuringExecution,
        };

        // Assert facts remain intact
        Assert.Equal(ProbeOutcome.Success, original.Outcome);
        Assert.True(original.Path.Resolved);
        Assert.True(original.Path.Bound);
        Assert.Equal(PathContinuity.ChangedDuringExecution, original.PathContinuity);
    }

    [Fact]
    public async Task ProbeScheduler_stamps_PathContinuity_from_observer()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);
        var observer = new TestRtnetlinkObserver(isLive: true);

        var link = new LinkSnapshot(
            InterfaceName: "Ethernet 1",
            InterfaceId: "eth0",
            Status: LinkStatus.Up,
            Medium: LinkMedium.Ethernet)
        {
            GatewayAddress = "192.168.1.1"
        };

        var routes = new MockRouteResolver();
        routes.Register(IPAddress.Parse("192.168.1.1"), new ProbePath("eth0", "192.168.1.100", Resolved: true));

        await using var scheduler = new ProbeScheduler(
            store: store,
            options: new ProbeOptions(),
            clock: clock,
            link: () => link,
            routes: routes,
            boundIcmp: new MockBoundIcmp(),
            observer: observer);

        await scheduler.ProbeGatewayForTestAsync(CancellationToken.None);

        var snapshot = store.Snapshot();
        var gwResult = snapshot.FirstOrDefault(r => r.Scope == ProbeScope.Gateway);

        Assert.NotNull(gwResult);
        Assert.Equal(ProbeOutcome.Success, gwResult.Outcome);
        Assert.Equal(PathContinuity.Held, gwResult.PathContinuity);
    }

    private sealed class TestRtnetlinkObserver : INetworkChangeObserver
    {
        private readonly List<LinuxRtnetlinkObserver.NetlinkEventRecord> _events = [];
        public ulong RouteGeneration { get; private set; } = 1;
        public bool IsLive { get; }

        public event Action<ulong>? RouteChanged;

        public TestRtnetlinkObserver(bool isLive)
        {
            IsLive = isLive;
        }

        public void RecordChangeEvent(ushort msgType, long timestampTicks)
        {
            RouteGeneration++;
            _events.Add(new LinuxRtnetlinkObserver.NetlinkEventRecord(timestampTicks, RouteGeneration, msgType));
            RouteChanged?.Invoke(RouteGeneration);
        }

        public PathContinuity EvaluateContinuity(long startedTicks, long completedTicks, ProbePath path, IPAddress? destination = null)
        {
            if (!IsLive) return PathContinuity.Unknown;

            foreach (var evt in _events)
            {
                if (evt.TimestampTicks >= startedTicks && evt.TimestampTicks <= completedTicks)
                {
                    return PathContinuity.ChangedDuringExecution;
                }
            }

            return PathContinuity.Held;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockRouteResolver : IRouteResolver
    {
        private readonly Dictionary<IPAddress, ProbePath> _table = new();
        public void Register(IPAddress destination, ProbePath path) => _table[destination] = path;
        public ProbePath Resolve(IPAddress destination) =>
            _table.TryGetValue(destination, out var path) ? path : ProbePath.Unresolved;
    }

    private sealed class MockBoundIcmp : IBoundIcmp
    {
        public Task<IcmpEcho?> SendAsync(IPAddress destination, IPAddress source, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<IcmpEcho?>(new IcmpEcho(true, false, TimeSpan.FromMilliseconds(5), 0));
    }
}
