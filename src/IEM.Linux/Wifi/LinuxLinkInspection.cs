using System;
using System.Threading.Tasks;
using IEM.Core.Probes;

namespace IEM.Linux.Wifi;

/// <summary>
/// Assembles the link inspector scope for a Linux host, wiring SystemLinkInspector
/// with LinuxWifiLinkInspector and LinuxNl80211Radio.
/// <para>
/// Mirroring WindowsLinkInspection, this provides the authoritative production composition root
/// for Linux link inspection so that service, CLI, and monitor runner observe links identically.
/// </para>
/// </summary>
public sealed class LinuxLinkInspectionScope : IPlatformLinkInspectionScope
{
    private readonly LinuxWifiLinkInspector _inspector;

    public LinuxLinkInspectionScope(LinuxWifiLinkInspector inspector)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    public ILinkInspector Inspector => _inspector;

    public LinuxWifiLinkInspector WifiInspector => _inspector;

    public ValueTask DisposeAsync() => _inspector.DisposeAsync();
}

/// <summary>
/// Factory for Linux link inspection scopes.
/// </summary>
public static class LinuxLinkInspection
{
    /// <summary>
    /// Builds an inspector scope that adds Linux nl80211 wireless detail to the platform's link inspection.
    /// </summary>
    public static IPlatformLinkInspectionScope Create(string? interfaceName = null)
    {
        var baseInspector = new SystemLinkInspector(interfaceName);
        var wifiInspector = new LinuxWifiLinkInspector(baseInspector, interfaceName);

        return new LinuxLinkInspectionScope(wifiInspector);
    }
}
