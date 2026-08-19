namespace IEM.Core.Probes;

/// <summary>
/// Scoped platform link inspection instance that manages background scanning/events.
/// Invariant 211 / Invariant 275: Platform adapters are injected via factory scopes.
/// </summary>
public interface IPlatformLinkInspectionScope : IAsyncDisposable
{
    ILinkInspector Inspector { get; }
}

/// <summary>
/// Platform factory supplying platform-specific network probes and inspectors.
/// Eliminates direct coupling to platform-specific types (e.g. WindowsLinkInspection, BoundPing).
/// </summary>
public interface IPlatformProbeFactory
{
    ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null);
    IRouteResolver CreateRouteResolver();
    IBoundIcmp CreateBoundIcmp();
    INetworkChangeObserver CreateObserver() => NullNetworkChangeObserver.Instance;
}
