using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using ManagedNativeWifi;
using IEM.Core.Probes;

namespace IEM.Windows;

/// <summary>
/// Assembles the link inspector for a Windows host, and owns the background scanning that
/// goes with it.
/// <para>
/// A single place to build this matters more than it looks: the console runner, the
/// service and the window must all observe the link identically, or the same fault would
/// be classified differently depending on which one happened to be watching.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLinkInspection : IAsyncDisposable
{
    private readonly WlanScanCache _scanCache;

    private WindowsLinkInspection(ILinkInspector inspector, WlanScanCache scanCache)
    {
        Inspector = inspector;
        _scanCache = scanCache;
    }

    public ILinkInspector Inspector { get; }

    /// <summary>
    /// Builds an inspector that adds wireless detail where the platform can supply it.
    /// <para>
    /// Scanning starts immediately rather than on first need, because the first answer
    /// takes several seconds and the moment it is wanted - an adapter dropping - is the
    /// moment there is no time to wait for one.
    /// </para>
    /// </summary>
    public static WindowsLinkInspection Create(string? interfaceName = null)
    {
        var baseInspector = new SystemLinkInspector(interfaceName);

        var monitored = ResolveInterfaceId(interfaceName);
        var scanCache = new WlanScanCache(monitored);
        scanCache.Start();

        return new WindowsLinkInspection(new WlanLinkInspector(baseInspector, scanCache, monitored), scanCache);
    }

    public ValueTask DisposeAsync() => _scanCache.DisposeAsync();

    /// <summary>The wireless adapter the session is about, if it can be told from the rest.</summary>
    private static Guid? ResolveInterfaceId(string? interfaceName)
    {
        try
        {
            return NativeWifi.EnumerateInterfaces()
                .FirstOrDefault(i => string.Equals(i.Id.ToString(), interfaceName, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(i.Description, interfaceName, StringComparison.OrdinalIgnoreCase))
                ?.Id;
        }
        catch (Exception)
        {
            // The WLAN stack wraps its failures - a stopped WlanSvc, a machine with no
            // wireless hardware at all - in TargetInvocationException, so no narrower catch
            // is honest here. Resolution is advisory: null simply means machine-wide.
            return null;
        }
    }
}
