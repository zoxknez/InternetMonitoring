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
    private readonly ILinuxNl80211Socket? _sharedQuerySocket;

    public LinuxLinkInspectionScope(
        LinuxWifiLinkInspector inspector,
        ILinuxNl80211EventObserver? observer = null,
        ILinuxWifiScanCompletionTracker? tracker = null,
        ILinuxNl80211Socket? sharedQuerySocket = null)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _observer = observer;
        _tracker = tracker ?? inspector.Radio.ScanCompletionTracker;
        _sharedQuerySocket = sharedQuerySocket;
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
        if (_sharedQuerySocket != null)
        {
            _sharedQuerySocket.Dispose();
        }
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
    /// Invariant: Master composition uses one shared query fd + one dedicated event fd.
    /// </summary>
    public static IPlatformLinkInspectionScope Create(string? interfaceName = null)
    {
        var baseInspector = new SystemLinkInspector(interfaceName);
        var querySocket = LinuxNl80211Socket.Create();
        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, querySocket: querySocket, ownsQuerySocket: false);
        var radio = new LinuxNl80211Radio(querySocket, rfkillReader: null, boundInterfaceId: interfaceName, ownsSocket: false, scanCompletionTracker: tracker);

        observer.Start();

        var wifiInspector = new LinuxWifiLinkInspector(baseInspector, radio, ownsRadio: true, eventObserver: observer);

        return new LinuxLinkInspectionScope(wifiInspector, observer, tracker, sharedQuerySocket: querySocket);
    }
}
