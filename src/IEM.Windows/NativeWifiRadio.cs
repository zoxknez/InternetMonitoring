using System.Runtime.Versioning;
using IEM.Core.Probes;
using ManagedNativeWifi;

namespace IEM.Windows;

/// <summary>
/// The Windows Native Wi-Fi API, behind the interface the rules are written against.
/// <para>
/// Nothing here decides anything. Every method is one call to the platform, scoped to one
/// adapter, with failure turned into <see langword="null"/> - so that the judgements built
/// on these readings live in <see cref="WirelessDetailReader"/> where they can be tested
/// without a radio, and this layer stays small enough to check by reading it.
/// </para>
/// <para>
/// Scoping to the adapter is not a detail. A machine can have a built-in card and a USB
/// dongle, and answering with whichever the API happened to list first describes the wrong
/// link - which breaks the one guard that keeps the classifier honest about a vanished SSID.
/// </para>
/// </summary>
/// <param name="interfaceId">
/// The monitored adapter, where one is known. Null falls back to machine-wide enumeration,
/// which is the portable shape for a single-radio machine.
/// </param>
[SupportedOSPlatform("windows")]
public sealed class NativeWifiRadio(WlanScanCache scanCache, Guid? interfaceId = null) : IWirelessRadio
{
    private readonly WlanScanCache _scanCache = scanCache ?? throw new ArgumentNullException(nameof(scanCache));

    /// <summary>
    /// Whether the radio is on, for the monitored adapter and no other.
    /// <para>
    /// Null when the adapter is not among those listed: not knowing is its own answer here.
    /// Reporting it as off would let a vanished SSID be read as the customer having pressed a
    /// key, and reporting it as on would blame the router for the same thing.
    /// </para>
    /// </summary>
    public bool? IsRadioOn(string interfaceId)
    {
        if (!Guid.TryParse(interfaceId, out var id))
        {
            return null;
        }

        try
        {
            foreach (var connection in NativeWifi.EnumerateInterfaceConnections())
            {
                if (connection.Id == id)
                {
                    return connection.IsRadioOn;
                }
            }
        }
#pragma warning disable CA1031 // Wireless detail is enrichment; losing it must not stop monitoring.
        catch (Exception)
#pragma warning restore CA1031
        {
            // No wireless adapter, the radio switched off mid-call, or the service lacking
            // rights. Reported as absent rather than fabricated.
        }

        return null;
    }

    /// <summary>
    /// The current association on the monitored adapter, read from the association itself
    /// rather than inferred from a scan.
    /// </summary>
    public WirelessAssociation? ReadAssociation(string interfaceId)
    {
        if (!Guid.TryParse(interfaceId, out var id))
        {
            return null;
        }

        try
        {
            // Returns a result code alongside the value, so a failed read is distinguishable
            // from an adapter that is simply not connected.
            var (result, connection) = NativeWifi.GetCurrentConnection(id);

            if (result != ActionResult.Success ||
                connection is null ||
                connection.InterfaceState != InterfaceState.Connected)
            {
                return null;
            }

            return new WirelessAssociation(
                connection.Ssid?.ToString(),
                connection.Bssid?.ToString(),
                connection.SignalQuality);
        }
#pragma warning disable CA1031 // As above: enrichment that must never take monitoring down.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// The access point with this exact BSSID from the monitored adapter's own scan.
    /// <para>
    /// Null when the association is not in the last scan, which happens routinely: scans lag,
    /// and a mesh node can be serving this machine while the scan that would list it is
    /// seconds old. The identity is known either way; the radio detail is not, and stays
    /// missing rather than being borrowed from a different access point.
    /// </para>
    /// </summary>
    public WirelessAccessPoint? ReadAccessPoint(string ssid, string bssid)
    {
        try
        {
            // The monitored adapter's own scan only. Machine-wide enumeration filled signal
            // and channel from whichever radio saw the access point loudest, which on a
            // two-adapter machine is a measurement of the wrong link.
            var networks = interfaceId is { } only
                ? NativeWifi.EnumerateBssNetworks(only).Item2
                : NativeWifi.EnumerateBssNetworks();

            foreach (var network in networks)
            {
                if (string.Equals(network.Ssid.ToString(), ssid, StringComparison.Ordinal) &&
                    string.Equals(network.Bssid.ToString(), bssid, StringComparison.OrdinalIgnoreCase))
                {
                    return new WirelessAccessPoint(bssid, network.Channel, network.Rssi);
                }
            }
        }
#pragma warning disable CA1031 // As above.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return null;
    }

    public bool? IsSsidVisible(string ssid) => _scanCache.IsVisible(ssid);

    public void RequestUrgentScan() => _scanCache.RequestUrgentScan();
}
