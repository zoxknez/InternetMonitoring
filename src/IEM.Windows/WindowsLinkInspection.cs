using System.Runtime.Versioning;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Windows;

/// <summary>
/// Assembles the link inspector for a Windows host, and owns the background scanning that
/// goes with it.
/// Invariants:
/// WIN_SESSION_INTERFACE_IMMUTABLE: Identity is pinned and cannot change.
/// WIN_INTERFACE_FAILURE_NEVER_CAUSES_RESELECTION: Missing/Down adapter never falls back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLinkInspection : IPlatformLinkInspectionScope
{
    private readonly WlanScanCache _scanCache;

    private WindowsLinkInspection(MonitoredInterfaceIdentity identity, ILinkInspector inspector, WlanScanCache scanCache)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _scanCache = scanCache ?? throw new ArgumentNullException(nameof(scanCache));
    }

    public MonitoredInterfaceIdentity Identity { get; }

    public ILinkInspector Inspector { get; }

    /// <summary>
    /// Builds an inspector that adds wireless detail where the platform can supply it.
    /// Pinned interface identity is resolved once and held immutable for the scope's lifetime.
    /// </summary>
    public static WindowsLinkInspection Create(InterfaceSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identity = WindowsInterfaceResolver.Resolve(request);
        var baseInspector = new SystemLinkInspector(identity.InterfaceId, identity.InterfaceName);

        Guid? monitored = Guid.TryParse(identity.InterfaceId.Trim().Trim('{', '}'), out var guid) ? guid : null;
        var scanCache = new WlanScanCache(monitored);
        scanCache.Start();

        return new WindowsLinkInspection(identity, new WlanLinkInspector(baseInspector, scanCache, monitored), scanCache);
    }

    public static WindowsLinkInspection Create() =>
        Create(InterfaceSelectionRequest.ForAuto());

    public static WindowsLinkInspection Create(string? interfaceName) =>
        Create(string.IsNullOrWhiteSpace(interfaceName)
            ? InterfaceSelectionRequest.ForAuto()
            : InterfaceSelectionRequest.ForExplicit(interfaceName));

    public ValueTask DisposeAsync() => _scanCache.DisposeAsync();
}
