using System;
using System.Threading.Tasks;
using IEM.Core.Probes;

namespace IEM.Linux.Wifi;

/// <summary>
/// Assembles the link inspector scope for a Linux host, wiring SystemLinkInspector
/// with LinuxWifiLinkInspector, LinuxNl80211Radio, LinuxWifiScanCompletionTracker,
/// and dedicated LinuxNl80211EventObserver.
/// <para>
/// Mirroring WindowsLinkInspection, this provides the authoritative production composition root
/// for Linux link inspection so that service, CLI, and monitor runner observe links identically.
/// </para>
/// </summary>
public sealed class LinuxLinkInspectionScope : IPlatformLinkInspectionScope
{
    private readonly LinuxWifiLinkInspector _inspector;
    private readonly ILinuxNl80211EventObserver? _observer;
    private readonly ILinuxWifiScanCompletionTracker _tracker;

    public LinuxLinkInspectionScope(
        LinuxWifiLinkInspector inspector,
        ILinuxNl80211EventObserver? observer = null,
        ILinuxWifiScanCompletionTracker? tracker = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _observer = observer;
        _tracker = tracker ?? inspector.Radio.ScanCompletionTracker;
    }

    public ILinkInspector Inspector => _inspector;

    public LinuxWifiLinkInspector WifiInspector => _inspector;

    public ILinuxNl80211EventObserver? EventObserver => _observer;

    public ILinuxWifiScanCompletionTracker ScanCompletionTracker => _tracker;

    public async ValueTask DisposeAsync()
    {
        if (_observer != null)
        {
            await _observer.DisposeAsync().ConfigureAwait(false);
        }
        await _inspector.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Factory for Linux link inspection scopes.
/// </summary>
public static class LinuxLinkInspection
{
    /// <summary>
    /// Builds an inspector scope that adds Linux nl80211 wireless detail to the platform's link inspection,
    /// complete with dedicated event observer and shared scan completion tracker.
    /// </summary>
    public static IPlatformLinkInspectionScope Create(string? interfaceName = null)
    {
        var baseInspector = new SystemLinkInspector(interfaceName);
        var tracker = new LinuxWifiScanCompletionTracker();
        var radio = new LinuxNl80211Radio(boundInterfaceId: interfaceName, scanCompletionTracker: tracker);
        var observer = new LinuxNl80211EventObserver(tracker);

        observer.Start();

        var wifiInspector = new LinuxWifiLinkInspector(baseInspector, radio, ownsRadio: true, eventObserver: observer);

        return new LinuxLinkInspectionScope(wifiInspector, observer, tracker);
    }
}
