using System;
using System.Collections.Generic;
using System.Linq;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Linux.Wifi;

/// <summary>
/// Verdict state for acceptance gates.
/// Invariant: GATE INCOMPLETE != PASS.
/// </summary>
public static class WifiAcceptanceVerdict
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
    public const string NotTested = "NOT_TESTED";
    public const string NotApplicable = "NOT_APPLICABLE";
}

/// <summary>
/// Pure, deterministic evaluator for Linux bare-metal Wi-Fi acceptance gates (Phase 3.1-7B).
/// Guarantees fail-closed evaluation: missing evidence or incomplete steps can NEVER produce PASS.
/// </summary>
public static class LinuxWifiAcceptanceEvaluator
{
    public static readonly string[] MandatoryGates =
    [
        "ZeroCapabilities",
        "InterfaceIdentity",
        "AssociationTruth",
        "ContinuityTruth",
        "ProductionProjectionTruth",
        "StationPeerTruth",
        "CachedBssTruth",
        "AccessPointEvidence",
        "NumericFidelity"
    ];

    public static readonly string[] OptionalGates =
    [
        "MloHardwareQualification"
    ];

    /// <summary>
    /// Evaluates Linux capabilities from /proc/self/status lines.
    /// Invariant: Zero capability model requires both CapEff and CapAmb present and zero.
    /// </summary>
    public static (string Verdict, string? CapEff, string? CapAmb, string? Reason) EvaluateCapabilities(IEnumerable<string>? statusLines)
    {
        if (statusLines == null)
        {
            return (WifiAcceptanceVerdict.NotTested, null, null, "No status lines available");
        }

        string? capEff = null;
        string? capAmb = null;

        foreach (var line in statusLines)
        {
            if (line.StartsWith("CapEff:", StringComparison.OrdinalIgnoreCase))
            {
                capEff = line.Substring(7).Trim();
            }
            else if (line.StartsWith("CapAmb:", StringComparison.OrdinalIgnoreCase))
            {
                capAmb = line.Substring(7).Trim();
            }
        }

        if (capEff == null || capAmb == null)
        {
            return (WifiAcceptanceVerdict.Fail, capEff, capAmb, "Missing CapEff or CapAmb in status lines");
        }

        bool isCapEffZero = IsZeroHexMask(capEff);
        bool isCapAmbZero = IsZeroHexMask(capAmb);

        if (isCapEffZero && isCapAmbZero)
        {
            return (WifiAcceptanceVerdict.Pass, capEff, capAmb, null);
        }

        return (WifiAcceptanceVerdict.Fail, capEff, capAmb, $"Non-zero capabilities detected (CapEff={capEff}, CapAmb={capAmb})");
    }

    private static bool IsZeroHexMask(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var clean = s.TrimStart('0');
        return clean.Length == 0 || clean == "0";
    }

    /// <summary>
    /// Evaluates InterfaceIdentity gate.
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateInterfaceIdentity(
        string expectedInterface,
        LinkSnapshot? initialSnapshot,
        LinuxComposedAssociationObservation? observation)
    {
        if (string.IsNullOrWhiteSpace(expectedInterface))
        {
            return (WifiAcceptanceVerdict.NotTested, "Expected interface not specified");
        }

        if (initialSnapshot == null || observation == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing initial snapshot or composed observation");
        }

        if (!string.Equals(initialSnapshot.InterfaceName, expectedInterface, StringComparison.OrdinalIgnoreCase))
        {
            return (WifiAcceptanceVerdict.Fail, $"Initial snapshot interface '{initialSnapshot.InterfaceName}' != expected '{expectedInterface}'");
        }

        if (!string.Equals(observation.IfName, expectedInterface, StringComparison.OrdinalIgnoreCase))
        {
            return (WifiAcceptanceVerdict.Fail, $"Composed observation interface '{observation.IfName}' != expected '{expectedInterface}'");
        }

        if (observation.IfIndex <= 0)
        {
            return (WifiAcceptanceVerdict.Fail, $"Invalid observation IfIndex={observation.IfIndex}");
        }

        if (!observation.Wdev.HasValue || observation.Wdev.Value == 0)
        {
            return (WifiAcceptanceVerdict.Fail, "Missing or zero WDEV attribute");
        }

        return (WifiAcceptanceVerdict.Pass, null);
    }

    /// <summary>
    /// Evaluates AssociationTruth gate.
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateAssociationTruth(LinuxComposedAssociationObservation? observation)
    {
        if (observation == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing composed observation");
        }

        if (observation.State != LinuxWirelessAssociationState.Associated)
        {
            return (WifiAcceptanceVerdict.Fail, $"Observation state is '{observation.State}', expected 'Associated'");
        }

        if (observation.Links.Count == 0)
        {
            return (WifiAcceptanceVerdict.Fail, "Observation is Associated but has 0 associated links");
        }

        var link0 = observation.Links[0];
        if (string.IsNullOrWhiteSpace(link0.Bssid) || link0.BssidBytes == null || link0.BssidBytes.Length != 6)
        {
            return (WifiAcceptanceVerdict.Fail, "Associated link has missing or invalid BSSID");
        }

        if (string.IsNullOrWhiteSpace(link0.DisplaySsid))
        {
            return (WifiAcceptanceVerdict.Fail, "Associated link has missing DisplaySsid");
        }

        return (WifiAcceptanceVerdict.Pass, null);
    }

    /// <summary>
    /// Evaluates ContinuityTruth gate (temporal continuity t0 -> station -> t2).
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateContinuityTruth(LinuxComposedAssociationObservation? observation)
    {
        if (observation == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing composed observation");
        }

        if (!observation.ContinuityVerified)
        {
            return (WifiAcceptanceVerdict.Fail, "Observation failed temporal continuity verification (identity drift detected during query)");
        }

        return (WifiAcceptanceVerdict.Pass, null);
    }

    /// <summary>
    /// Evaluates ProductionProjectionTruth gate (verifies that production LinkSnapshot matches direct observation).
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateProductionProjectionTruth(
        LinkSnapshot? initialSnapshot,
        LinuxComposedAssociationObservation? observation)
    {
        if (initialSnapshot == null || observation == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing initial snapshot or observation");
        }

        if (initialSnapshot.Medium != LinkMedium.Wireless)
        {
            return (WifiAcceptanceVerdict.Fail, $"Initial snapshot Medium is '{initialSnapshot.Medium}', expected 'Wireless'");
        }

        if (observation.State != LinuxWirelessAssociationState.Associated)
        {
            // If not associated, wireless snapshot should have null or unassociated SSID/BSSID
            if (initialSnapshot.Wireless?.Bssid != null)
            {
                return (WifiAcceptanceVerdict.Fail, "Initial snapshot projected BSSID when kernel observation is NotAssociated");
            }
            return (WifiAcceptanceVerdict.Pass, null);
        }

        if (initialSnapshot.Wireless == null)
        {
            return (WifiAcceptanceVerdict.Fail, "Kernel observation is Associated but initial snapshot Wireless is null");
        }

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(observation.Links);
        if (mlo.State == LinuxMloCompositionState.NotMlo)
        {
            // Single-link association
            var link0 = observation.Links[0];
            if (!string.Equals(initialSnapshot.Wireless.Ssid, link0.DisplaySsid, StringComparison.Ordinal))
            {
                return (WifiAcceptanceVerdict.Fail, $"Production SSID '{initialSnapshot.Wireless.Ssid}' != Observation SSID '{link0.DisplaySsid}'");
            }

            if (!string.Equals(initialSnapshot.Wireless.Bssid, link0.Bssid, StringComparison.OrdinalIgnoreCase))
            {
                return (WifiAcceptanceVerdict.Fail, $"Production BSSID '{initialSnapshot.Wireless.Bssid}' != Observation BSSID '{link0.Bssid}'");
            }

            int? expectedChannel = link0.FrequencyMhz.HasValue ? LinuxNl80211Radio.FrequencyMhzToChannel(link0.FrequencyMhz.Value) : null;
            if (initialSnapshot.Wireless.Channel != expectedChannel)
            {
                return (WifiAcceptanceVerdict.Fail, $"Production Channel '{initialSnapshot.Wireless.Channel}' != Observation Channel '{expectedChannel}'");
            }

            return (WifiAcceptanceVerdict.Pass, null);
        }
        else
        {
            // MLO association
            if (!string.Equals(initialSnapshot.Wireless.Ssid, mlo.DisplaySsid, StringComparison.Ordinal))
            {
                return (WifiAcceptanceVerdict.Fail, $"Production MLO SSID '{initialSnapshot.Wireless.Ssid}' != MLO Common SSID '{mlo.DisplaySsid}'");
            }

            if (initialSnapshot.Wireless.Bssid != null)
            {
                return (WifiAcceptanceVerdict.Fail, "Production MLO BSSID must be strictly null (safe Core projection)");
            }

            if (initialSnapshot.Wireless.SignalQualityPercent != null)
            {
                return (WifiAcceptanceVerdict.Fail, "Production MLO SignalQualityPercent must be strictly null");
            }

            return (WifiAcceptanceVerdict.Pass, null);
        }
    }

    /// <summary>
    /// Evaluates StationPeerTruth gate (verifies peer MAC correlates strictly with associated BSSID / MLD).
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateStationPeerTruth(LinuxComposedAssociationObservation? observation)
    {
        if (observation == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing composed observation");
        }

        if (observation.State != LinuxWirelessAssociationState.Associated)
        {
            return (WifiAcceptanceVerdict.NotApplicable, "Not associated");
        }

        if (observation.StationInfo == null)
        {
            return (WifiAcceptanceVerdict.Fail, "Associated observation is missing StationInfo (GET_STATION failed or returned null)");
        }

        if (!observation.ContinuityVerified)
        {
            return (WifiAcceptanceVerdict.Fail, "Station observation failed continuity verification");
        }

        var link0 = observation.Links[0];
        string expectedPeer = !string.IsNullOrEmpty(link0.MldAddress) ? link0.MldAddress : link0.Bssid;

        if (!LinuxNl80211Protocol.TryParseMacAddress(observation.StationInfo.PeerMacString, out var stationMacBytes) ||
            !LinuxNl80211Protocol.TryParseMacAddress(expectedPeer, out var expectedMacBytes))
        {
            return (WifiAcceptanceVerdict.Fail, $"Failed parsing peer MAC: station='{observation.StationInfo.PeerMacString}', expected='{expectedPeer}'");
        }

        if (!stationMacBytes.AsSpan().SequenceEqual(expectedMacBytes))
        {
            return (WifiAcceptanceVerdict.Fail, $"Station peer MAC '{observation.StationInfo.PeerMacString}' != expected peer '{expectedPeer}'");
        }

        return (WifiAcceptanceVerdict.Pass, null);
    }

    /// <summary>
    /// Evaluates CachedBssTruth gate.
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateCachedBssTruth(
        LinuxComposedAssociationObservation? observation,
        WirelessAccessPoint? ap)
    {
        if (observation == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing composed observation");
        }

        if (observation.State != LinuxWirelessAssociationState.Associated)
        {
            return (WifiAcceptanceVerdict.NotApplicable, "Not associated");
        }

        if (ap == null)
        {
            return (WifiAcceptanceVerdict.Fail, "Cached BSS query (ReadAccessPointAsync) returned null");
        }

        var link0 = observation.Links[0];
        if (!LinuxNl80211Protocol.TryParseMacAddress(ap.Bssid, out var apMacBytes) ||
            !LinuxNl80211Protocol.TryParseMacAddress(link0.Bssid, out var linkMacBytes))
        {
            return (WifiAcceptanceVerdict.Fail, $"Invalid MAC in AP='{ap.Bssid}' or link='{link0.Bssid}'");
        }

        if (!apMacBytes.AsSpan().SequenceEqual(linkMacBytes))
        {
            return (WifiAcceptanceVerdict.Fail, $"Resolved AP BSSID '{ap.Bssid}' != Associated Link BSSID '{link0.Bssid}'");
        }

        return (WifiAcceptanceVerdict.Pass, null);
    }

    /// <summary>
    /// Evaluates AccessPointEvidence gate (channel, RSSI).
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateAccessPointEvidence(
        LinuxComposedAssociationObservation? observation,
        WirelessAccessPoint? ap)
    {
        if (observation == null || ap == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing observation or access point");
        }

        var link0 = observation.Links[0];
        int? expectedChannel = link0.FrequencyMhz.HasValue ? LinuxNl80211Radio.FrequencyMhzToChannel(link0.FrequencyMhz.Value) : null;

        if (ap.Channel != expectedChannel)
        {
            return (WifiAcceptanceVerdict.Fail, $"AP Channel '{ap.Channel}' != expected channel '{expectedChannel}'");
        }

        return (WifiAcceptanceVerdict.Pass, null);
    }

    /// <summary>
    /// Evaluates NumericFidelity gate (ensures all 4 counter fields are present on both T0 and T1, and non-decreasing).
    /// Invariant: Missing counter fields NEVER produce PASS.
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateNumericFidelity(
        LinuxComposedAssociationObservation? t0,
        LinuxComposedAssociationObservation? t1)
    {
        if (t0 == null || t1 == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "Missing T0 or T1 observation");
        }

        if (t0.State != LinuxWirelessAssociationState.Associated || t1.State != LinuxWirelessAssociationState.Associated)
        {
            return (WifiAcceptanceVerdict.NotApplicable, "Not associated during interval");
        }

        if (t0.StationInfo == null || t1.StationInfo == null)
        {
            return (WifiAcceptanceVerdict.NotTested, "StationInfo missing in T0 or T1");
        }

        var s0 = t0.StationInfo;
        var s1 = t1.StationInfo;

        if (!s0.RxBytes.HasValue || !s1.RxBytes.HasValue ||
            !s0.TxBytes.HasValue || !s1.TxBytes.HasValue ||
            !s0.RxPackets.HasValue || !s1.RxPackets.HasValue ||
            !s0.TxPackets.HasValue || !s1.TxPackets.HasValue)
        {
            return (WifiAcceptanceVerdict.NotTested, "Station counter fields (Rx/Tx bytes/packets) not provided by driver");
        }

        if (s1.RxBytes.Value < s0.RxBytes.Value)
        {
            return (WifiAcceptanceVerdict.Fail, $"RxBytes decreased: T0={s0.RxBytes.Value}, T1={s1.RxBytes.Value}");
        }

        if (s1.TxBytes.Value < s0.TxBytes.Value)
        {
            return (WifiAcceptanceVerdict.Fail, $"TxBytes decreased: T0={s0.TxBytes.Value}, T1={s1.TxBytes.Value}");
        }

        if (s1.RxPackets.Value < s0.RxPackets.Value)
        {
            return (WifiAcceptanceVerdict.Fail, $"RxPackets decreased: T0={s0.RxPackets.Value}, T1={s1.RxPackets.Value}");
        }

        if (s1.TxPackets.Value < s0.TxPackets.Value)
        {
            return (WifiAcceptanceVerdict.Fail, $"TxPackets decreased: T0={s0.TxPackets.Value}, T1={s1.TxPackets.Value}");
        }

        return (WifiAcceptanceVerdict.Pass, null);
    }

    /// <summary>
    /// Evaluates MloHardwareQualification gate.
    /// PASS if MLO present & valid, NOT_APPLICABLE on non-MLO hardware, FAIL if MLO conflicted/broken.
    /// </summary>
    public static (string Verdict, string? Reason) EvaluateMloHardwareQualification(LinuxComposedAssociationObservation? observation)
    {
        if (observation == null || observation.State != LinuxWirelessAssociationState.Associated)
        {
            return (WifiAcceptanceVerdict.NotApplicable, "Not associated");
        }

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(observation.Links);
        return mlo.State switch
        {
            LinuxMloCompositionState.Valid => (WifiAcceptanceVerdict.Pass, $"MLO hardware qualified with {observation.Links.Count} links"),
            LinuxMloCompositionState.NotMlo => (WifiAcceptanceVerdict.NotApplicable, "Non-MLO single-link hardware"),
            _ => (WifiAcceptanceVerdict.Fail, $"MLO composition state is '{mlo.State}'")
        };
    }

    /// <summary>
    /// Computes overall verdict and exit code.
    /// Exit Codes:
    ///   0 = PASS (all mandatory gates are PASS, optional gates are PASS or NOT_APPLICABLE)
    ///   1 = FAIL (any gate is FAIL)
    ///   2 = NOT_TESTED / GATE_INCOMPLETE (no FAIL, but one or more mandatory gates are NOT_TESTED or missing)
    /// </summary>
    public static (string OverallVerdict, int ExitCode) ComputeOverallVerdict(IReadOnlyDictionary<string, string> verdicts)
    {
        // 1. Any FAIL anywhere -> FAIL (exit 1)
        if (verdicts.Values.Any(v => v == WifiAcceptanceVerdict.Fail))
        {
            return (WifiAcceptanceVerdict.Fail, 1);
        }

        // 2. Any mandatory gate missing or NOT_TESTED -> NOT_TESTED (exit 2)
        foreach (var mandatoryGate in MandatoryGates)
        {
            if (!verdicts.TryGetValue(mandatoryGate, out var verdict) || verdict == WifiAcceptanceVerdict.NotTested)
            {
                return (WifiAcceptanceVerdict.NotTested, 2);
            }

            if (verdict != WifiAcceptanceVerdict.Pass)
            {
                // Mandatory gate cannot be NOT_APPLICABLE for standard Wi-Fi qualification run
                return (WifiAcceptanceVerdict.NotTested, 2);
            }
        }

        // 3. Check optional gates (must not be FAIL, already verified above)
        return (WifiAcceptanceVerdict.Pass, 0);
    }
}
