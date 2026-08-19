using System.Runtime.Versioning;
using IEM.Core.Probes;

namespace IEM.Windows;

/// <summary>
/// Windows platform implementation of <see cref="IPlatformProbeFactory"/>.
/// Provides Win32/WLAN link inspection, GetBestRoute2 routing, and IcmpSendEcho2Ex ICMP echo.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProbeFactory : IPlatformProbeFactory
{
    public static readonly WindowsProbeFactory Instance = new();

    public ValueTask<IPlatformLinkInspectionScope> CreateLinkInspectionAsync(string? interfaceName = null)
    {
        var inspection = WindowsLinkInspection.Create(interfaceName);
        return ValueTask.FromResult<IPlatformLinkInspectionScope>(inspection);
    }

    public IRouteResolver CreateRouteResolver() => new RouteResolver();

    public IBoundIcmp CreateBoundIcmp() => BoundPing.Instance;
}
