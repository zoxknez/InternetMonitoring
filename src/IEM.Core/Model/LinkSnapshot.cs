namespace IEM.Core.Model;

public enum LinkStatus
{
    Up,
    Down,

    /// <summary>The adapter we were monitoring is no longer present at all.</summary>
    Missing,
}

public enum LinkMedium
{
    Unknown,
    Ethernet,
    Wireless,
    Other,
}

/// <summary>
/// Wireless detail for the monitored adapter.
/// <para>
/// <see cref="SsidVisibleInScan"/> is the decisive signal for the most common home
/// fault: a router whose Wi-Fi radio dies while its WAN link is perfectly healthy.
/// If the SSID disappears from a scan during an outage, the router is at fault and
/// the operator is not.
/// </para>
/// </summary>
public sealed record WirelessSnapshot(
    string? Ssid,
    string? Bssid,
    int? SignalQualityPercent,
    int? Channel)
{
    /// <summary>Signal as actually reported by the adapter, in dBm.</summary>
    public int? MeasuredRssiDbm { get; init; }

    /// <summary>
    /// Signal strength in dBm.
    /// <para>
    /// Prefers the adapter's own reading. Falls back to deriving it from the 0-100 quality
    /// percentage using the documented mapping - 0 is -100 dBm, 100 is -50 dBm - which is
    /// a coarse approximation and only used when nothing better is available.
    /// </para>
    /// </summary>
    public int? RssiDbm => MeasuredRssiDbm
        ?? (SignalQualityPercent is null ? null : -100 + (SignalQualityPercent.Value / 2));

    /// <summary>
    /// Whether the configured SSID was seen in the most recent scan.
    /// <see langword="null"/> when scanning was not available or the last scan is too old.
    /// </summary>
    public bool? SsidVisibleInScan { get; init; }

    /// <summary>
    /// Whether the adapter's radio is switched on.
    /// <para>
    /// Recorded so a user turning Wi-Fi off is never mistaken for a router that stopped
    /// broadcasting. Both make the network vanish; only one is a fault.
    /// </para>
    /// </summary>
    public bool? RadioOn { get; init; }

    /// <summary>Signal below this is weak enough that drops are expected rather than surprising.</summary>
    public const int WeakSignalDbm = -70;

    public bool? IsSignalWeak => RssiDbm is { } rssi ? rssi < WeakSignalDbm : null;
}

/// <summary>State of the monitored network interface at one instant.</summary>
public sealed record LinkSnapshot(
    string InterfaceName,
    string InterfaceId,
    LinkStatus Status,
    LinkMedium Medium)
{
    /// <summary>Negotiated link rate. A 100 Mbit link under a 300 Mbit contract is a local fault, not the operator's.</summary>
    public long? LinkSpeedBitsPerSecond { get; init; }

    public string? GatewayAddress { get; init; }

    public bool HasGateway => !string.IsNullOrWhiteSpace(GatewayAddress);

    /// <summary>
    /// Resolvers handed out by DHCP. Queried directly so a broken operator resolver can be
    /// distinguished from DNS being broken everywhere. On most home networks this is the
    /// router, which forwards to the operator.
    /// </summary>
    public IReadOnlyList<string> DnsServers { get; init; } = [];

    public WirelessSnapshot? Wireless { get; init; }

    /// <summary>
    /// Public address as last observed. A change across an outage means the WAN session was
    /// re-established, which points upstream of the LAN.
    /// </summary>
    public string? PublicAddress { get; init; }

    /// <summary>
    /// What the router says about its own WAN connection.
    /// <para>
    /// Null when the router does not expose UPnP, which is common. Recorded as absent
    /// rather than assumed, because "the router did not say" and "the router said it was
    /// fine" lead to opposite conclusions.
    /// </para>
    /// </summary>
    public Probes.WanStatus? Router { get; init; }

    /// <summary>
    /// True when the router's WAN uptime went backwards, meaning it reconnected.
    /// The difference between a line fault and a router that restarted itself.
    /// </summary>
    public bool RouterReconnected { get; init; }

    /// <summary>
    /// True when the reconnect question was actually answered by a live reading.
    /// <para>
    /// A false <see cref="RouterReconnected"/> means two different things - "checked, no
    /// reboot" and "could not check" - and evidence that narrows on the second as though it
    /// were the first records the opposite of what was known.
    /// </para>
    /// </summary>
    public bool RouterChecked { get; init; }

    /// <summary>
    /// The adapter's hardware address.
    /// <para>
    /// Part of what makes a session evidence about one connection: a report covering two
    /// days on a machine whose adapter changed underneath it is a recording of two different
    /// networks presented as one.
    /// </para>
    /// </summary>
    public string? MacAddress { get; init; }

    public bool IsUp => Status == LinkStatus.Up;

    public static LinkSnapshot Unavailable(string interfaceName = "", string interfaceId = "") =>
        new(interfaceName, interfaceId, LinkStatus.Missing, LinkMedium.Unknown);
}
