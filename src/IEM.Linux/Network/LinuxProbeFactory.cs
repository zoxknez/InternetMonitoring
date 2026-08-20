using System.Threading.Tasks;
using IEM.Core.Hosting;
using IEM.Core.Probes;
using IEM.Linux.Network.Icmp;
using IEM.Linux.Wifi;

namespace IEM.Linux.Network;

/// <summary>
/// Platform factory supplying Linux network probes, FIB route resolvers, unprivileged datagram ICMP echo senders,
/// and nl80211 wireless link inspection.
/// Invariants 211, 249-258, 262, 271-275.
/// </summary>
public sealed class LinuxProbeFactory : IPlatformProbeFactory
{
    public static LinuxProbeFactory Instance { get; } = new();

    public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null)
    {
        var scope = LinuxLinkInspection.Create(interfaceName);
        return ValueTask.FromResult(scope);
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
}
