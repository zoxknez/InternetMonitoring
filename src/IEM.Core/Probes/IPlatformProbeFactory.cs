using IEM.Core.Hosting;
using IEM.Core.Model;

namespace IEM.Core.Probes;

/// <summary>
/// Scoped platform link inspection instance that manages background scanning/events.
/// Invariant 211 / Invariant 275: Platform adapters are injected via factory scopes.
/// </summary>
public interface IPlatformLinkInspectionScope : IAsyncDisposable
{
    MonitoredInterfaceIdentity Identity { get; }

    ILinkInspector Inspector { get; }
}

/// <summary>
/// Platform factory supplying platform-specific network probes and inspectors.
/// Eliminates direct coupling to platform-specific types (e.g. WindowsLinkInspection, BoundPing).
/// </summary>
public interface IPlatformProbeFactory
{
    ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(InterfaceSelectionRequest request);

    ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null) =>
        CreateLinkInspectionAsync(new InterfaceSelectionRequest(
            InterfaceId: null,
            InterfaceName: interfaceName,
            Mode: string.IsNullOrWhiteSpace(interfaceName) ? InterfaceSelectionMode.Auto : InterfaceSelectionMode.Explicit));

    IRouteResolver CreateRouteResolver(MonitoredInterfaceIdentity monitoredInterface, INetworkChangeObserver observer);

    IRouteResolver CreateRouteResolver() =>
        CreateRouteResolver(new MonitoredInterfaceIdentity(string.Empty, string.Empty), NullNetworkChangeObserver.Instance);

    IRouteResolver CreateRouteResolver(INetworkChangeObserver observer) =>
        CreateRouteResolver(new MonitoredInterfaceIdentity(string.Empty, string.Empty), observer);

    IBoundIcmp CreateBoundIcmp();

    INetworkChangeObserver CreateObserver() => NullNetworkChangeObserver.Instance;
}
