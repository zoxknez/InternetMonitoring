using IEM.Core.Hosting;
using IEM.Core.Probes;
using IEM.Linux.Network.Icmp;

namespace IEM.Linux.Network;

/// <summary>
/// Platform factory supplying Linux network probes, FIB route resolvers, and unprivileged datagram ICMP echo senders.
/// Invariants 211, 271-275.
/// </summary>
public sealed class LinuxProbeFactory : IPlatformProbeFactory
{
    public static LinuxProbeFactory Instance { get; } = new();

    public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null)
    {
        var inspector = new SystemLinkInspector(interfaceName);
        return ValueTask.FromResult<IPlatformLinkInspectionScope>(new BasicLinkInspectionScope(inspector));
    }

    public IRouteResolver CreateRouteResolver() =>
        CreateRouteResolver(NullNetworkChangeObserver.Instance);

    public IRouteResolver CreateRouteResolver(INetworkChangeObserver observer)
    {
        // Trigger runtime capability preflight on startup / session initiation
        Preflight.LinuxNetworkCapabilityPreflight.GetOrEvaluate();
        return new LinuxRouteResolver(observer: observer);
    }

    public IBoundIcmp CreateBoundIcmp() => LinuxBoundIcmp.Instance;

    public INetworkChangeObserver CreateObserver() => new Netlink.LinuxRtnetlinkObserver();

    private sealed class BasicLinkInspectionScope(ILinkInspector inspector) : IPlatformLinkInspectionScope
    {
        public ILinkInspector Inspector { get; } = inspector;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
