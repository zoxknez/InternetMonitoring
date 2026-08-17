using IEM.Core.Model;

namespace IEM.Core.Classification;

/// <summary>
/// Turns one <see cref="ProbeCycle"/> into a <see cref="SampleVerdict"/>.
/// <para>
/// Pure and deterministic: same input, same verdict, no clock, no network, no state.
/// Everything that needs history arrives through <see cref="ClassificationContext"/>.
/// That is what makes the whole fault catalogue exhaustively testable.
/// </para>
/// <para>
/// Checks run from the customer's device outwards. The order matters: a fault close to
/// the device explains away everything beyond it, so claiming an operator fault while the
/// adapter is down would be wrong regardless of what the external probes reported.
/// </para>
/// </summary>
public sealed class StateClassifier(ClassifierOptions? options = null)
{
    private readonly ClassifierOptions _options = options ?? ClassifierOptions.Default;

    public SampleVerdict Classify(ProbeCycle cycle, ClassificationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        context ??= ClassificationContext.Empty;

        // Our own speed test saturates the link on purpose. Anything measured during it
        // describes the test, not the service.
        if (cycle.SelfTestRunning)
        {
            return new SampleVerdict(
                NetworkState.SelfTest,
                "Speed test in progress; connectivity assessment suspended.");
        }

        return ClassifyLocalLink(cycle)
            ?? ClassifyRouterAndUpstream(cycle, context)
            ?? ClassifyDns(cycle)
            ?? ClassifyDegradation(cycle, context)
            ?? new SampleVerdict(NetworkState.Ok, "All attempted probes succeeded.");
    }

    /// <summary>Faults on the device itself or on the device-to-router radio link.</summary>
    private static SampleVerdict? ClassifyLocalLink(ProbeCycle cycle)
    {
        if (cycle.Link.IsUp)
        {
            return null;
        }

        // The decisive split for home networks. When a router's Wi-Fi radio dies, the
        // client disassociates and the adapter reports Down - identical, from the
        // adapter's point of view, to the user walking out of range. The difference is
        // whether the network is still being broadcast at all.
        //
        // The radio check is not optional. Switching Wi-Fi off makes every network vanish
        // from the scan, and without this the tool would read that as the router having
        // failed - blaming an outage on equipment when the customer pressed a key.
        var wireless = cycle.Link.Wireless;
        if (cycle.Link.Medium == LinkMedium.Wireless &&
            wireless?.SsidVisibleInScan == false &&
            wireless.RadioOn != false)
        {
            return new SampleVerdict(
                NetworkState.WifiRadioDown,
                $"Adapter is {cycle.Link.Status} and SSID '{wireless.Ssid}' is absent from scan results " +
                "while the radio is on; the access point stopped broadcasting.");
        }

        return new SampleVerdict(
            NetworkState.AdapterDown,
            $"Monitored adapter '{cycle.Link.InterfaceName}' reports {cycle.Link.Status}.");
    }

    /// <summary>Faults at the router or beyond it.</summary>
    private static SampleVerdict? ClassifyRouterAndUpstream(ProbeCycle cycle, ClassificationContext context)
    {
        var gateway = cycle.Gateway;
        var reachedInternet = cycle.AnyExternalReachability;

        if (reachedInternet)
        {
            // Traffic is flowing, so neither the router nor the uplink is down. A gateway
            // that ignores ICMP while the internet works is a router preference, not a fault.
            return null;
        }

        if (context.CpeRebootDetected)
        {
            return new SampleVerdict(
                NetworkState.CpeReboot,
                "No external reachability and router-side signals indicate the CPE restarted.");
        }

        if (gateway.AllSucceeded)
        {
            // The strongest statement a single machine can make: the path to the router is
            // healthy and everything past it is not. Still short of naming the operator -
            // a failed WAN port, CPE firmware, an exhausted NAT table or a dropped PPPoE
            // session all present exactly like this. Confidence scoring separates them.
            return new SampleVerdict(
                NetworkState.CpeUpstreamUnreachable,
                $"Gateway {cycle.Link.GatewayAddress} responds but no external target is reachable " +
                $"(ICMP {cycle.ExternalIcmp.Succeeded}/{cycle.ExternalIcmp.Attempted}, " +
                $"TCP {cycle.ExternalTcp.Succeeded}/{cycle.ExternalTcp.Attempted}, " +
                $"HTTP {cycle.Http.Succeeded}/{cycle.Http.Attempted}).");
        }

        if (gateway.AllFailed)
        {
            return new SampleVerdict(
                NetworkState.GatewayDown,
                $"Adapter is up but gateway {cycle.Link.GatewayAddress} did not respond and " +
                "no external target is reachable.");
        }

        // No gateway configured, or the gateway was not probed. We know the internet is
        // unreachable but cannot say where the path breaks.
        return new SampleVerdict(
            NetworkState.InternetDown,
            "No external target is reachable and gateway state is unknown.");
    }

    /// <summary>
    /// Runs only once IP connectivity is confirmed working, so a DNS verdict here always
    /// means name resolution specifically - never a dressed-up outage.
    /// </summary>
    private static SampleVerdict? ClassifyDns(ProbeCycle cycle)
    {
        var isp = cycle.DnsIsp;
        var pub = cycle.DnsPublic;
        var system = cycle.DnsSystem;

        // Operator resolver broken while a public one answers. The service is degraded and
        // it is the operator's doing, but the line itself is up.
        //
        // The public answer must be fresh, and both queries go out bound to the same source
        // address - otherwise this says nothing about which resolver is at fault, only that
        // two different queries took two different paths.
        if (isp.AllFailed && pub.AnyFreshSuccess)
        {
            return new SampleVerdict(
                NetworkState.DnsIspFailure,
                "Operator-assigned resolver failed while a public resolver answered over the same path.");
        }

        var attempted = isp.Attempted + pub.Attempted + system.Attempted;
        var succeeded = isp.Succeeded + pub.Succeeded + system.Succeeded;

        if (attempted > 0 && succeeded == 0)
        {
            return new SampleVerdict(
                NetworkState.DnsGlobalFailure,
                $"All {attempted} resolvers failed while IP connectivity still worked.");
        }

        return null;
    }

    /// <summary>Everything still working, but not working well.</summary>
    private SampleVerdict? ClassifyDegradation(ProbeCycle cycle, ClassificationContext context)
    {
        if (cycle.CaptivePortalSuspected)
        {
            return new SampleVerdict(
                NetworkState.CaptivePortalSuspected,
                "Connectivity endpoint returned unexpected content, which is characteristic of an intercepting portal.");
        }

        // ICMP is widely rate-limited and filtered. Calling this an outage while TCP and
        // TLS sail through would manufacture incidents that do not exist.
        //
        // Only fresh successes count. A handshake that succeeded before the trouble began
        // proves nothing about the trouble, and letting it count here is exactly how a real
        // outage gets quietly reclassified as harmless filtering.
        if (cycle.ExternalIcmp.AllFailed &&
            (cycle.ExternalTcp.AnyFreshSuccess || cycle.ExternalTls.AnyFreshSuccess || cycle.Http.AnyFreshSuccess))
        {
            return new SampleVerdict(
                NetworkState.IcmpFiltered,
                $"All {cycle.ExternalIcmp.Attempted} ICMP targets failed while TCP/TLS/HTTP succeeded.");
        }

        if (cycle.ExternalIcmp.SomeFailed)
        {
            return new SampleVerdict(
                NetworkState.PacketLoss,
                $"{cycle.ExternalIcmp.Failed} of {cycle.ExternalIcmp.Attempted} external ICMP targets failed " +
                $"({cycle.ExternalIcmp.LossPercent:F1}% loss).");
        }

        if (context.Jitter is { } jitter && jitter >= _options.HighJitterThreshold)
        {
            return new SampleVerdict(
                NetworkState.HighJitter,
                $"Round-trip variation {jitter.TotalMilliseconds:F1} ms exceeds the " +
                $"{_options.HighJitterThreshold.TotalMilliseconds:F0} ms threshold.");
        }

        if (cycle.AverageExternalRoundTrip is { } rtt && rtt >= _options.HighLatencyThreshold)
        {
            return new SampleVerdict(
                NetworkState.HighLatency,
                $"Average external round trip {rtt.TotalMilliseconds:F1} ms exceeds the " +
                $"{_options.HighLatencyThreshold.TotalMilliseconds:F0} ms threshold.");
        }

        // Reported last: roaming only matters as an explanation when nothing else is wrong.
        // When it coincides with a real failure it belongs in the confidence score instead,
        // where it lowers certainty that the operator is at fault.
        if (context.BssidChanged)
        {
            return new SampleVerdict(
                NetworkState.WifiRoaming,
                $"Access point changed to BSSID {cycle.Link.Wireless?.Bssid} under the same SSID.");
        }

        return null;
    }
}
