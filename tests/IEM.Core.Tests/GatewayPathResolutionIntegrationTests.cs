using System.Net;
using IEM.Core.Hosting;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Gateway & Path Resolution Integration.
/// Invariants 271-275 (Phase 3.1-4F):
/// 1. LinuxRouteResolver.Resolve() always returns Bound=false (prediction vs fact).
/// 2. SystemLinkInspector != authoritative kernel FIB routing.
/// 3. Gateway probe coherence: Interface mismatch or unresolved gateway route results in Skipped (never false GatewayDown).
/// 4. Source address family must strictly match gateway IP family.
/// 5. Independent per-destination paths preserved (MultiplePathsInUse on disagreement).
/// </summary>
public sealed class GatewayPathResolutionIntegrationTests
{
    [Fact]
    public void LinuxRouteResolver_Resolve_never_sets_Bound_true()
    {
        var resolver = new LinuxRouteResolver();
        var path = resolver.Resolve(IPAddress.Loopback);

        // Standalone route resolution is a route prediction, never a bound socket fact
        Assert.False(path.Bound);
    }

    [Fact]
    public async Task ProbeScheduler_skips_gateway_probe_on_interface_mismatch()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);
        var routes = new MockRouteResolver();

        // Monitored interface is eth0, but route resolver reports default gateway routes out wlan0
        var link = new LinkSnapshot(
            InterfaceName: "Ethernet 1",
            InterfaceId: "eth0",
            Status: LinkStatus.Up,
            Medium: LinkMedium.Ethernet)
        {
            GatewayAddress = "192.168.1.1"
        };

        routes.Register(IPAddress.Parse("192.168.1.1"), new ProbePath("wlan0", "192.168.1.50", Resolved: true));

        var boundIcmp = new MockBoundIcmp(succeed: true);

        await using var scheduler = new ProbeScheduler(
            store: store,
            options: new ProbeOptions(),
            clock: clock,
            link: () => link,
            routes: routes,
            boundIcmp: boundIcmp);

        await scheduler.ProbeGatewayForTestAsync(CancellationToken.None);

        var snapshot = store.Snapshot();
        var gwResult = snapshot.FirstOrDefault(r => r.Scope == ProbeScope.Gateway);

        Assert.NotNull(gwResult);
        Assert.Equal(ProbeOutcome.Skipped, gwResult.Outcome);
        Assert.Contains("interface mismatch", gwResult.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeScheduler_skips_gateway_probe_when_gateway_route_unresolved()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);
        var routes = new MockRouteResolver();

        var link = new LinkSnapshot(
            InterfaceName: "Ethernet 1",
            InterfaceId: "eth0",
            Status: LinkStatus.Up,
            Medium: LinkMedium.Ethernet)
        {
            GatewayAddress = "192.168.1.1"
        };

        // Route to gateway is unresolved
        routes.Register(IPAddress.Parse("192.168.1.1"), ProbePath.Unresolved);

        var boundIcmp = new MockBoundIcmp(succeed: true);

        await using var scheduler = new ProbeScheduler(
            store: store,
            options: new ProbeOptions(),
            clock: clock,
            link: () => link,
            routes: routes,
            boundIcmp: boundIcmp);

        await scheduler.ProbeGatewayForTestAsync(CancellationToken.None);

        var snapshot = store.Snapshot();
        var gwResult = snapshot.FirstOrDefault(r => r.Scope == ProbeScope.Gateway);

        Assert.NotNull(gwResult);
        Assert.Equal(ProbeOutcome.Skipped, gwResult.Outcome);
        Assert.Contains("unresolved", gwResult.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeScheduler_executes_gateway_probe_when_coherent()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);
        var routes = new MockRouteResolver();

        var link = new LinkSnapshot(
            InterfaceName: "Ethernet 1",
            InterfaceId: "eth0",
            Status: LinkStatus.Up,
            Medium: LinkMedium.Ethernet)
        {
            GatewayAddress = "192.168.1.1"
        };

        // Coherent route: eth0 -> 192.168.1.1 via eth0 source 192.168.1.100
        routes.Register(IPAddress.Parse("192.168.1.1"), new ProbePath("eth0", "192.168.1.100", Resolved: true));

        var boundIcmp = new MockBoundIcmp(succeed: true);

        await using var scheduler = new ProbeScheduler(
            store: store,
            options: new ProbeOptions(),
            clock: clock,
            link: () => link,
            routes: routes,
            boundIcmp: boundIcmp);

        await scheduler.ProbeGatewayForTestAsync(CancellationToken.None);

        var snapshot = store.Snapshot();
        var gwResult = snapshot.FirstOrDefault(r => r.Scope == ProbeScope.Gateway);

        Assert.NotNull(gwResult);
        Assert.Equal(ProbeOutcome.Success, gwResult.Outcome);
        Assert.True(gwResult.Path.Bound);
        Assert.Equal("eth0", gwResult.Path.InterfaceId);
        Assert.Equal("192.168.1.100", gwResult.Path.SourceAddress);
    }

    private sealed class MockRouteResolver : IRouteResolver
    {
        private readonly Dictionary<IPAddress, ProbePath> _table = new();

        public void Register(IPAddress destination, ProbePath path) => _table[destination] = path;

        public ProbePath Resolve(IPAddress destination) =>
            _table.TryGetValue(destination, out var path) ? path : ProbePath.Unresolved;
    }

    private sealed class MockBoundIcmp(bool succeed) : IBoundIcmp
    {
        public Task<IcmpEcho?> SendAsync(
            IPAddress destination,
            IPAddress source,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (succeed)
            {
                return Task.FromResult<IcmpEcho?>(new IcmpEcho(
                    Succeeded: true,
                    TimedOut: false,
                    RoundTrip: TimeSpan.FromMilliseconds(5),
                    Status: 0));
            }

            return Task.FromResult<IcmpEcho?>(new IcmpEcho(
                Succeeded: false,
                TimedOut: true,
                RoundTrip: timeout,
                Status: 11010));
        }
    }
}
