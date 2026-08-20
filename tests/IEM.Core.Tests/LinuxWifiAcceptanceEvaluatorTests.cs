using System;
using System.Collections.Generic;
using IEM.Core.Model;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxWifiAcceptanceEvaluatorTests
{
    [Fact]
    public void Mandatory_NOT_TESTED_Returns_ExitCode_2()
    {
        var verdicts = new Dictionary<string, string>
        {
            ["ZeroCapabilities"] = WifiAcceptanceVerdict.Pass,
            ["InterfaceIdentity"] = WifiAcceptanceVerdict.Pass,
            ["AssociationTruth"] = WifiAcceptanceVerdict.Pass,
            ["ContinuityTruth"] = WifiAcceptanceVerdict.Pass,
            ["ProductionProjectionTruth"] = WifiAcceptanceVerdict.Pass,
            ["StationPeerTruth"] = WifiAcceptanceVerdict.Pass,
            ["CachedBssTruth"] = WifiAcceptanceVerdict.Pass,
            ["AccessPointEvidence"] = WifiAcceptanceVerdict.Pass,
            ["NumericFidelity"] = WifiAcceptanceVerdict.NotTested, // Incomplete / missing counter
            ["MloHardwareQualification"] = WifiAcceptanceVerdict.NotApplicable
        };

        var (overall, exitCode) = LinuxWifiAcceptanceEvaluator.ComputeOverallVerdict(verdicts);

        Assert.Equal(WifiAcceptanceVerdict.NotTested, overall);
        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Mandatory_FAIL_Returns_ExitCode_1()
    {
        var verdicts = new Dictionary<string, string>
        {
            ["ZeroCapabilities"] = WifiAcceptanceVerdict.Pass,
            ["InterfaceIdentity"] = WifiAcceptanceVerdict.Pass,
            ["AssociationTruth"] = WifiAcceptanceVerdict.Fail, // Direct failure
            ["ContinuityTruth"] = WifiAcceptanceVerdict.Pass,
            ["ProductionProjectionTruth"] = WifiAcceptanceVerdict.Pass,
            ["StationPeerTruth"] = WifiAcceptanceVerdict.Pass,
            ["CachedBssTruth"] = WifiAcceptanceVerdict.Pass,
            ["AccessPointEvidence"] = WifiAcceptanceVerdict.Pass,
            ["NumericFidelity"] = WifiAcceptanceVerdict.NotTested,
            ["MloHardwareQualification"] = WifiAcceptanceVerdict.NotApplicable
        };

        var (overall, exitCode) = LinuxWifiAcceptanceEvaluator.ComputeOverallVerdict(verdicts);

        Assert.Equal(WifiAcceptanceVerdict.Fail, overall);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void All_Mandatory_PASS_Returns_ExitCode_0()
    {
        var verdicts = new Dictionary<string, string>
        {
            ["ZeroCapabilities"] = WifiAcceptanceVerdict.Pass,
            ["InterfaceIdentity"] = WifiAcceptanceVerdict.Pass,
            ["AssociationTruth"] = WifiAcceptanceVerdict.Pass,
            ["ContinuityTruth"] = WifiAcceptanceVerdict.Pass,
            ["ProductionProjectionTruth"] = WifiAcceptanceVerdict.Pass,
            ["StationPeerTruth"] = WifiAcceptanceVerdict.Pass,
            ["CachedBssTruth"] = WifiAcceptanceVerdict.Pass,
            ["AccessPointEvidence"] = WifiAcceptanceVerdict.Pass,
            ["NumericFidelity"] = WifiAcceptanceVerdict.Pass,
            ["MloHardwareQualification"] = WifiAcceptanceVerdict.NotApplicable // Optional gate
        };

        var (overall, exitCode) = LinuxWifiAcceptanceEvaluator.ComputeOverallVerdict(verdicts);

        Assert.Equal(WifiAcceptanceVerdict.Pass, overall);
        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("0000000000000000", "0000000000000000", WifiAcceptanceVerdict.Pass)]
    [InlineData("0", "0", WifiAcceptanceVerdict.Pass)]
    [InlineData("0000000000000001", "0000000000000000", WifiAcceptanceVerdict.Fail)]
    [InlineData("0000000000000000", "0000000000002000", WifiAcceptanceVerdict.Fail)]
    public void Capabilities_Zero_Hex_Mask_Evaluation(string capEff, string capAmb, string expectedVerdict)
    {
        var lines = new[]
        {
            "Name: iem",
            $"CapEff:\t{capEff}",
            $"CapAmb:\t{capAmb}"
        };

        var (verdict, eff, amb, _) = LinuxWifiAcceptanceEvaluator.EvaluateCapabilities(lines);

        Assert.Equal(expectedVerdict, verdict);
        Assert.Equal(capEff, eff);
        Assert.Equal(capAmb, amb);
    }

    [Fact]
    public void Missing_CapEff_Or_CapAmb_Never_Passes()
    {
        // 1. Null status lines
        var (v1, _, _, _) = LinuxWifiAcceptanceEvaluator.EvaluateCapabilities(null);
        Assert.Equal(WifiAcceptanceVerdict.NotTested, v1);

        // 2. Missing CapAmb
        var (v2, _, _, _) = LinuxWifiAcceptanceEvaluator.EvaluateCapabilities(new[] { "CapEff:\t0000000000000000" });
        Assert.Equal(WifiAcceptanceVerdict.Fail, v2);

        // 3. Missing CapEff
        var (v3, _, _, _) = LinuxWifiAcceptanceEvaluator.EvaluateCapabilities(new[] { "CapAmb:\t0000000000000000" });
        Assert.Equal(WifiAcceptanceVerdict.Fail, v3);
    }

    [Fact]
    public void Missing_Station_Counters_Never_Pass_NumericFidelity()
    {
        var t0 = CreateObservation(rxBytes: null, txBytes: 100, rxPackets: 10, txPackets: 10);
        var t1 = CreateObservation(rxBytes: 50, txBytes: 150, rxPackets: 15, txPackets: 15);

        var (verdict, _) = LinuxWifiAcceptanceEvaluator.EvaluateNumericFidelity(t0, t1);

        Assert.Equal(WifiAcceptanceVerdict.NotTested, verdict);
    }

    [Fact]
    public void Decreasing_Station_Counters_Fail_NumericFidelity()
    {
        var t0 = CreateObservation(rxBytes: 500, txBytes: 100, rxPackets: 10, txPackets: 10);
        var t1 = CreateObservation(rxBytes: 400, txBytes: 150, rxPackets: 15, txPackets: 15); // RxBytes decreased

        var (verdict, _) = LinuxWifiAcceptanceEvaluator.EvaluateNumericFidelity(t0, t1);

        Assert.Equal(WifiAcceptanceVerdict.Fail, verdict);
    }

    [Fact]
    public void Monotonic_Station_Counters_Pass_NumericFidelity()
    {
        var t0 = CreateObservation(rxBytes: 500, txBytes: 100, rxPackets: 10, txPackets: 10);
        var t1 = CreateObservation(rxBytes: 600, txBytes: 150, rxPackets: 15, txPackets: 15);

        var (verdict, _) = LinuxWifiAcceptanceEvaluator.EvaluateNumericFidelity(t0, t1);

        Assert.Equal(WifiAcceptanceVerdict.Pass, verdict);
    }

    [Fact]
    public void InterfaceIdentity_Must_Match_Requested_Interface()
    {
        var snapshot = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless);
        var obs = CreateObservation();

        // 1. Exact match -> PASS
        var (v1, _) = LinuxWifiAcceptanceEvaluator.EvaluateInterfaceIdentity("wlan0", snapshot, obs);
        Assert.Equal(WifiAcceptanceVerdict.Pass, v1);

        // 2. Snapshot interface mismatch -> FAIL
        var wrongSnapshot = new LinkSnapshot("eth0", "eth0", LinkStatus.Up, LinkMedium.Ethernet);
        var (v2, _) = LinuxWifiAcceptanceEvaluator.EvaluateInterfaceIdentity("wlan0", wrongSnapshot, obs);
        Assert.Equal(WifiAcceptanceVerdict.Fail, v2);

        // 3. Requested interface mismatch -> FAIL
        var (v3, _) = LinuxWifiAcceptanceEvaluator.EvaluateInterfaceIdentity("wlan1", snapshot, obs);
        Assert.Equal(WifiAcceptanceVerdict.Fail, v3);
    }

    [Fact]
    public void ProductionProjection_Matches_Direct_Nl80211_Observation()
    {
        var obs = CreateObservation();
        var matchingSnapshot = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("OfficeWiFi", "00:11:22:33:44:55", 85, 36)
        };

        var (v1, _) = LinuxWifiAcceptanceEvaluator.EvaluateProductionProjectionTruth(matchingSnapshot, obs);
        Assert.Equal(WifiAcceptanceVerdict.Pass, v1);

        var mismatchedSsid = matchingSnapshot with
        {
            Wireless = new WirelessSnapshot("WrongSSID", "00:11:22:33:44:55", 85, 36)
        };
        var (v2, _) = LinuxWifiAcceptanceEvaluator.EvaluateProductionProjectionTruth(mismatchedSsid, obs);
        Assert.Equal(WifiAcceptanceVerdict.Fail, v2);

        var mismatchedBssid = matchingSnapshot with
        {
            Wireless = new WirelessSnapshot("OfficeWiFi", "00:11:22:33:44:99", 85, 36)
        };
        var (v3, _) = LinuxWifiAcceptanceEvaluator.EvaluateProductionProjectionTruth(mismatchedBssid, obs);
        Assert.Equal(WifiAcceptanceVerdict.Fail, v3);
    }

    [Fact]
    public void StationPeer_Must_Match_Associated_Peer()
    {
        var obsMatching = CreateObservation(peerMac: "00:11:22:33:44:55");
        var (v1, _) = LinuxWifiAcceptanceEvaluator.EvaluateStationPeerTruth(obsMatching);
        Assert.Equal(WifiAcceptanceVerdict.Pass, v1);

        var obsMismatch = CreateObservation(peerMac: "00:11:22:33:44:99");
        var (v2, _) = LinuxWifiAcceptanceEvaluator.EvaluateStationPeerTruth(obsMismatch);
        Assert.Equal(WifiAcceptanceVerdict.Fail, v2);

        var obsMissingStation = new LinuxComposedAssociationObservation(
            IfIndex: 3,
            IfName: "wlan0",
            WiphyIndex: 0,
            State: LinuxWirelessAssociationState.Associated,
            Links: new[]
            {
                new LinuxAssociatedBssLink("00:11:22:33:44:55", new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, null, null, null, null, "OfficeWiFi", 5180, -6500, 85, 10, null)
            },
            StationInfo: null,
            Wdev: 0x1000UL,
            Generation: 10,
            DumpStatus: LinuxNl80211DumpStatus.Complete,
            ContinuityVerified: true);

        var (v3, _) = LinuxWifiAcceptanceEvaluator.EvaluateStationPeerTruth(obsMissingStation);
        Assert.Equal(WifiAcceptanceVerdict.Fail, v3);
    }

    private static LinuxComposedAssociationObservation CreateObservation(
        ulong? rxBytes = 1000,
        ulong? txBytes = 500,
        uint? rxPackets = 100,
        uint? txPackets = 50,
        string peerMac = "00:11:22:33:44:55")
    {
        var sta = new LinuxNl80211StationInfo(
            IfIndex: 3,
            PeerMac: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            PeerMacString: peerMac,
            Generation: 10,
            SignalDbm: -65,
            SignalAverageDbm: -64,
            RxBytes: rxBytes,
            TxBytes: txBytes,
            RxPackets: rxPackets,
            TxPackets: txPackets,
            TxRetries: 0,
            TxFailed: 0,
            ConnectedTimeSeconds: 120,
            TxRate: new LinuxNl80211RateInfo(300000000UL, 3000, 7, null, null, null, null, null, null, null, null, null, true, false, false, false, true),
            RxRate: null,
            ExpectedThroughputKbps: 300000,
            RxDurationUsec: null,
            TxDurationUsec: null,
            AssociationBootTimeNs: 1234567890UL);

        var link = new LinuxAssociatedBssLink(
            Bssid: "00:11:22:33:44:55",
            BssidBytes: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            MloLinkId: null,
            MldAddress: null,
            MldAddressBytes: null,
            SsidBytes: System.Text.Encoding.UTF8.GetBytes("OfficeWiFi"),
            DisplaySsid: "OfficeWiFi",
            FrequencyMhz: 5180,
            SignalMbm: -6500,
            SignalUnspec: 85,
            SeenMsAgo: 10,
            LastSeenBootTimeNs: null);

        return new LinuxComposedAssociationObservation(
            IfIndex: 3,
            IfName: "wlan0",
            WiphyIndex: 0,
            State: LinuxWirelessAssociationState.Associated,
            Links: new[] { link },
            StationInfo: sta,
            Wdev: 0x1000UL,
            Generation: 10,
            DumpStatus: LinuxNl80211DumpStatus.Complete,
            ContinuityVerified: true);
    }
}
