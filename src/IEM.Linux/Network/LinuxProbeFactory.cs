using System.Threading.Tasks;
using IEM.Core.Hosting;
using IEM.Core.Model;
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

    public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(InterfaceSelectionRequest request)
    {
        var scope = LinuxLinkInspection.Create(request);
        return ValueTask.FromResult(scope);
    }

    public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null)
    {
        var scope = LinuxLinkInspection.Create(interfaceName);
        return ValueTask.FromResult(scope);
    }

    public IRouteResolver CreateRouteResolver(MonitoredInterfaceIdentity monitoredInterface, INetworkChangeObserver observer)
    {
        Preflight.LinuxNetworkCapabilityPreflight.GetOrEvaluate();
        return new LinuxRouteResolver(observer: observer);
    }

    public IRouteResolver CreateRouteResolver() =>
        CreateRouteResolver(new MonitoredInterfaceIdentity(string.Empty, string.Empty), NullNetworkChangeObserver.Instance);

    public IRouteResolver CreateRouteResolver(INetworkChangeObserver observer) =>
        CreateRouteResolver(new MonitoredInterfaceIdentity(string.Empty, string.Empty), observer);

    public IBoundIcmp CreateBoundIcmp() => LinuxBoundIcmp.Instance;

    public INetworkChangeObserver CreateObserver() => new Netlink.LinuxRtnetlinkObserver();
}
