namespace IEM.Core.Model;

public enum Severity
{
    Ok,
    Info,
    Degraded,
    Outage,
}

/// <summary>
/// Classification of one sample. Ordered loosely by how far the fault sits from the
/// customer: local device first, then the Wi-Fi link, then the router, then upstream.
/// </summary>
public enum NetworkState
{
    /// <summary>Everything the cycle attempted succeeded.</summary>
    Ok,

    // ---- Local device -------------------------------------------------------

    /// <summary>The monitored adapter is not up. A local fault, never the operator's.</summary>
    AdapterDown,

    /// <summary>Sampling stopped for longer than expected: sleep, reboot, or the process was starved.</summary>
    MonitoringGap,

    /// <summary>The application's own speed test was saturating the link. Never counted as a fault.</summary>
    SelfTest,

    // ---- Wi-Fi link (device to router) --------------------------------------

    /// <summary>BSSID changed under the same SSID. Roaming between access points, not an operator fault.</summary>
    WifiRoaming,

    /// <summary>
    /// The SSID vanished from scan results while the adapter stayed up. The router's Wi-Fi
    /// radio failed. This is the classic home fault that gets misreported as "no internet".
    /// </summary>
    WifiRadioDown,

    // ---- Router / CPE -------------------------------------------------------

    /// <summary>Adapter is up but the gateway does not answer.</summary>
    GatewayDown,

    /// <summary>
    /// The gateway answers but nothing beyond it does. Proves the fault is not on the
    /// device-to-router link. It does not by itself prove the operator is at fault:
    /// a failed WAN interface, CPE firmware, a full NAT table, or a dropped PPPoE
    /// session all land here too. Confidence scoring is what separates those.
    /// </summary>
    CpeUpstreamUnreachable,

    /// <summary>The router restarted: uptime reset, ARP relearned, DHCP renewed.</summary>
    CpeReboot,

    // ---- Upstream -----------------------------------------------------------

    /// <summary>Every external probe failed and the gateway state does not narrow it further.</summary>
    InternetDown,

    /// <summary>The operator's resolver failed while a public resolver still answered.</summary>
    DnsIspFailure,

    /// <summary>Every resolver failed while raw IP connectivity still worked.</summary>
    DnsGlobalFailure,

    // ---- Degraded, not down -------------------------------------------------

    /// <summary>ICMP failed but TCP and TLS still worked. Filtering, not an outage.</summary>
    IcmpFiltered,

    /// <summary>Connectivity endpoint answered with something unexpected. An intercepting portal.</summary>
    CaptivePortalSuspected,

    /// <summary>Some external targets failed while others succeeded.</summary>
    PacketLoss,

    HighLatency,

    HighJitter,
}

/// <summary>Where the fault sits. This is the question a complaint actually turns on.</summary>
public enum FaultAttribution
{
    /// <summary>Not a fault at all: roaming, our own test, a monitoring gap.</summary>
    None,

    /// <summary>The customer's own computer.</summary>
    LocalDevice,

    /// <summary>The router or the Wi-Fi it broadcasts. The customer's equipment, not the operator's.</summary>
    Router,

    /// <summary>Beyond the router. The operator's side.</summary>
    Upstream,

    /// <summary>Degraded but not down, and the cause is not pinned to one side.</summary>
    Undetermined,
}

public static class NetworkStateInfo
{
    /// <summary>
    /// States that count as the service being unavailable. Deliberately narrow:
    /// DNS failures and degradation are reported separately so the headline
    /// downtime figure means one thing only.
    /// </summary>
    private static readonly HashSet<NetworkState> OutageStates =
    [
        NetworkState.AdapterDown,
        NetworkState.WifiRadioDown,
        NetworkState.GatewayDown,
        NetworkState.CpeUpstreamUnreachable,
        NetworkState.CpeReboot,
        NetworkState.InternetDown,
    ];

    /// <summary>
    /// Outage states in which the customer's own network was observed working while nothing
    /// beyond it was. Only these carry into the headline figure a complaint rests on.
    /// <para>
    /// <see cref="NetworkState.InternetDown"/> used to be counted here and no longer is. It
    /// means nothing answered <em>and there was no gateway to test against</em> - so the
    /// fault could equally well be the customer's own router, and charging it to the
    /// operator inflates the one number the operator will check first. Those outages are
    /// still recorded in full; they simply stop being claimed as proven.
    /// </para>
    /// </summary>
    private static readonly HashSet<NetworkState> UpstreamStates =
    [
        NetworkState.CpeUpstreamUnreachable,
    ];

    public static bool IsOutage(this NetworkState state) => OutageStates.Contains(state);

    public static bool IsUpstream(this NetworkState state) => UpstreamStates.Contains(state);

    public static Severity SeverityOf(this NetworkState state) => state switch
    {
        NetworkState.Ok => Severity.Ok,

        NetworkState.MonitoringGap or
        NetworkState.SelfTest or
        NetworkState.WifiRoaming => Severity.Info,

        NetworkState.IcmpFiltered or
        NetworkState.CaptivePortalSuspected or
        NetworkState.PacketLoss or
        NetworkState.HighLatency or
        NetworkState.HighJitter or
        NetworkState.DnsIspFailure or
        NetworkState.DnsGlobalFailure => Severity.Degraded,

        _ => Severity.Outage,
    };

    /// <summary>
    /// Who the fault belongs to.
    /// <para>
    /// Deliberately conservative about naming the operator. Only states that positively
    /// rule out the customer's own equipment map to <see cref="FaultAttribution.Upstream"/>;
    /// anything ambiguous stays undetermined rather than being talked up into a complaint
    /// that will not survive contact with the operator.
    /// </para>
    /// </summary>
    public static FaultAttribution AttributionOf(this NetworkState state) => state switch
    {
        NetworkState.Ok or
        NetworkState.MonitoringGap or
        NetworkState.SelfTest or
        NetworkState.WifiRoaming or
        NetworkState.IcmpFiltered => FaultAttribution.None,

        NetworkState.AdapterDown => FaultAttribution.LocalDevice,

        NetworkState.WifiRadioDown or
        NetworkState.GatewayDown or
        NetworkState.CpeReboot => FaultAttribution.Router,

        NetworkState.CpeUpstreamUnreachable => FaultAttribution.Upstream,

        // Nothing answered and the router could not be tested, so the fault could sit
        // anywhere along the path - including on equipment the customer owns.
        NetworkState.InternetDown or

        // Name resolution failed while the path underneath it was carrying traffic. That is
        // a real fault worth reporting, but it is not the connection being down, and rolling
        // it into the outage figure would make that figure mean two different things.
        NetworkState.DnsIspFailure => FaultAttribution.Undetermined,

        _ => FaultAttribution.Undetermined,
    };

    /// <summary>
    /// Ranking used to pick the headline state for an incident that passed through
    /// several states. Higher wins.
    /// </summary>
    public static int Rank(this NetworkState state) => state switch
    {
        NetworkState.AdapterDown => 100,
        NetworkState.WifiRadioDown => 95,
        NetworkState.GatewayDown => 90,
        NetworkState.CpeReboot => 88,
        NetworkState.CpeUpstreamUnreachable => 85,
        NetworkState.InternetDown => 80,
        NetworkState.DnsGlobalFailure => 60,
        NetworkState.DnsIspFailure => 55,
        NetworkState.PacketLoss => 40,
        NetworkState.HighJitter => 35,
        NetworkState.HighLatency => 30,
        NetworkState.CaptivePortalSuspected => 25,
        NetworkState.IcmpFiltered => 20,
        NetworkState.WifiRoaming => 10,
        NetworkState.SelfTest => 5,
        NetworkState.MonitoringGap => 3,
        NetworkState.Ok => 0,
        _ => 0,
    };
}
