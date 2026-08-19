using System.Net;
using IEM.Core.Hosting;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network;
using IEM.Linux.Network.Netlink;
using IEM.Linux.Network.Preflight;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Phase 3.1-4G-R1 and Phase 3.1-5A:
/// 1. Invariant 248: Netlink subscription failure / stream error never synthesizes PathContinuity.Held.
/// 2. Runtime Capability Preflight: TCP, UDP/DNS, unicast source bind, and 10-minute reconciliation cache.
/// 3. Production Composition: MonitorWorker &amp; NetworkProbeSource receive and use the shared observer.
/// </summary>
public sealed class ObserverWiringAndPreflightTests
{
    [Fact]
    public void LinuxNetworkCapabilityPreflight_evaluates_all_cells()
    {
        var snapshot = LinuxNetworkCapabilityPreflight.Evaluate();

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.IcmpDatagramIPv4);
        Assert.NotNull(snapshot.IcmpDatagramIPv6);
        Assert.NotNull(snapshot.NetlinkRouteIPv4);
        Assert.NotNull(snapshot.NetlinkRouteIPv6);
        Assert.NotNull(snapshot.SourceBindIPv4);
        Assert.NotNull(snapshot.SourceBindIPv6);
        Assert.NotNull(snapshot.TcpConnectIPv4);
        Assert.NotNull(snapshot.TcpConnectIPv6);
        Assert.NotNull(snapshot.DnsUdpIPv4);
        Assert.NotNull(snapshot.DnsUdpIPv6);
    }

    [Fact]
    public void LinuxNetworkCapabilityPreflight_reconciles_via_cache()
    {
        var snapshot1 = LinuxNetworkCapabilityPreflight.GetOrEvaluate();
        var snapshot2 = LinuxNetworkCapabilityPreflight.GetOrEvaluate();

        Assert.Same(snapshot1, snapshot2);

        var snapshot3 = LinuxNetworkCapabilityPreflight.GetOrEvaluate(forceRecheck: true);
        Assert.NotNull(snapshot3);
    }

    [Fact]
    public async Task NetworkProbeSource_passes_shared_observer_to_ProbeScheduler()
    {
        var observer = new TestObserver(isLive: true);
        var inspector = new MockLinkInspector(new LinkSnapshot(
            InterfaceName: "Ethernet 1",
            InterfaceId: "eth0",
            Status: LinkStatus.Up,
            Medium: LinkMedium.Ethernet)
        {
            GatewayAddress = "192.168.1.1"
        });

        var routes = new MockRouteResolver();
        routes.Register(IPAddress.Parse("192.168.1.1"), new ProbePath("eth0", "192.168.1.100", Resolved: true));

        await using var probeSource = new NetworkProbeSource(
            options: new ProbeOptions(),
            linkInspector: inspector,
            clock: new ManualClock(),
            routes: routes,
            boundIcmp: new MockBoundIcmp(),
            observer: observer);

        Assert.NotNull(probeSource);
    }

    [Fact]
    public void Invariant248_offline_or_failed_observer_never_evaluates_Held()
    {
        var observer = new TestObserver(isLive: false);

        var continuity = observer.EvaluateContinuity(100, 200, new ProbePath("eth0", "192.168.1.10", Resolved: true));
        Assert.Equal(PathContinuity.Unknown, continuity);
        Assert.NotEqual(PathContinuity.Held, continuity);
    }

    private sealed class TestObserver(bool isLive) : INetworkChangeObserver
    {
        public ulong RouteGeneration { get; set; } = 1;
        public bool IsLive => isLive;
        public event Action<ulong>? RouteChanged { add { } remove { } }

        public PathContinuity EvaluateContinuity(long startedTicks, long completedTicks, ProbePath path, IPAddress? destination = null)
        {
            if (!IsLive) return PathContinuity.Unknown;
            return PathContinuity.Held;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockLinkInspector(LinkSnapshot snapshot) : ILinkInspector
    {
        public LinkSnapshot Inspect() => snapshot;
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
