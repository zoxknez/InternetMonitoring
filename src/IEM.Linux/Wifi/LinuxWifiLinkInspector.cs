using System;
using System.Threading.Tasks;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Core.Time;

namespace IEM.Linux.Wifi;

/// <summary>
/// Adds Linux wireless detail (nl80211 / Generic Netlink) to a link snapshot.
/// <para>
/// A decorator over <see cref="ILinkInspector"/> (e.g. <see cref="SystemLinkInspector"/>),
/// ensuring the platform-neutral inspector remains the authority for link state, while
/// nl80211 fills in signal, access point BSSID, channel, radio status, and station metrics.
/// </para>
/// <para>
/// Invariants 249-258, 262.
/// </para>
/// </summary>
public sealed class LinuxWifiLinkInspector : ILinkInspector, IAsyncDisposable, IDisposable
{
    private readonly ILinkInspector _inner;
    private readonly LinuxNl80211Radio _radio;
    private readonly WirelessDetailReader _wireless;
    private readonly ILinuxNl80211EventObserver? _eventObserver;
    private readonly bool _ownsRadio;

    /// <param name="inner">The inner link inspector (e.g. <see cref="SystemLinkInspector"/>).</param>
    /// <param name="boundInterfaceId">The monitored interface name (e.g. "wlan0"); scopes all Wi-Fi queries strictly to this interface.</param>
    /// <param name="socket">Optional nl80211 Netlink socket for testing or custom lifecycle.</param>
    /// <param name="rfkillReader">Optional rfkill reader for radio state determination.</param>
    /// <param name="clock">Optional monotonic clock for remembered SSID lifetime evaluation.</param>
    /// <param name="ownsSocket">Whether this instance owns the socket lifecycle.</param>
    /// <param name="eventObserver">Optional nl80211 event observer.</param>
    public LinuxWifiLinkInspector(
        ILinkInspector inner,
        string? boundInterfaceId = null,
        ILinuxNl80211Socket? socket = null,
        ILinuxRfkillReader? rfkillReader = null,
        IClock? clock = null,
        bool? ownsSocket = null,
        ILinuxNl80211EventObserver? eventObserver = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _radio = new LinuxNl80211Radio(socket, rfkillReader, boundInterfaceId, ownsSocket);
        _wireless = new WirelessDetailReader(_radio, clock);
        _eventObserver = eventObserver;
        _ownsRadio = true;
    }

    /// <summary>
    /// Constructs a decorator with an existing <see cref="LinuxNl80211Radio"/> instance.
    /// </summary>
    public LinuxWifiLinkInspector(
        ILinkInspector inner,
        LinuxNl80211Radio radio,
        IClock? clock = null,
        bool ownsRadio = false,
        ILinuxNl80211EventObserver? eventObserver = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _wireless = new WirelessDetailReader(_radio, clock);
        _eventObserver = eventObserver;
        _ownsRadio = ownsRadio;
    }

    public LinuxNl80211Radio Radio => _radio;
    public ILinuxNl80211EventObserver? EventObserver => _eventObserver;

    public LinkSnapshot Inspect()
    {
        var snapshot = _inner.Inspect();

        if (snapshot.Medium != LinkMedium.Wireless)
        {
            return snapshot;
        }

        var targetInterface = !string.IsNullOrWhiteSpace(snapshot.InterfaceName)
            ? snapshot.InterfaceName
            : snapshot.InterfaceId;

        var wireless = _wireless.Read(targetInterface);

        // Trouble on a wireless link is the moment the scan answer decides who is at fault,
        // so ask for a fresh one rather than waiting for the healthy interval.
        if (!snapshot.IsUp || wireless?.IsSignalWeak == true)
        {
            _wireless.NoteTrouble();
        }

        return snapshot with { Wireless = wireless };
    }

    public void Dispose()
    {
        if (_ownsRadio)
        {
            _eventObserver?.Dispose();
            _radio.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsRadio)
        {
            if (_eventObserver != null)
            {
                await _eventObserver.DisposeAsync().ConfigureAwait(false);
            }
            await _radio.DisposeAsync().ConfigureAwait(false);
        }
    }
}
