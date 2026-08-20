using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Full-fidelity acceptance simulator for Linux Bare-Metal Wi-Fi Acceptance (Phases 7B-7B .. 7B-7F).
/// Simulates kernel nl80211 responses, station traffic bursts, and MLO composition without requiring physical Linux hardware.
/// </summary>
public sealed class LinuxWifiAcceptanceSimulationTests
{
    private sealed class SimulatedNl80211Socket : ILinuxNl80211Socket
    {
        public GenlFamilyInfo Family { get; set; } = new(28, "nl80211", 1, 0, 32, new Dictionary<string, uint>());
        public List<LinuxNl80211InterfaceInfo> Interfaces { get; set; } = new();
        public List<LinuxNl80211BssInfo> BssDump { get; set; } = new();
        public LinuxNl80211StationInfo? Station { get; set; }

        public Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default) =>
            Task.FromResult<GenlFamilyInfo?>(Family);

        public Task<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> DumpInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Interfaces, LinuxNl80211DumpStatus.Complete));

        public Task<LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>> DumpWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Complete));

        public Task<LinuxNl80211DumpResult<LinuxNl80211BssInfo>> DumpBssAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(BssDump, LinuxNl80211DumpStatus.Complete));

        public Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<LinuxNl80211InterfaceInfo>(Interfaces));

        public Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<LinuxNl80211WiphyInfo>());

        public Task<LinuxNl80211SingleResult<LinuxNl80211StationInfo>> GetStationAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, byte[] peerMac, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(Station, LinuxNl80211DumpStatus.Complete));

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SimulatedRfkillReader : ILinuxRfkillReader
    {
        public LinuxRfkillObservation? ReadObservationForWiphy(uint wiphyIndex, string? ifname = null) =>
            new(0, wiphyIndex, false, false, LinuxRfkillEvidenceBasis.DevRfkill);
    }

    private sealed class SimulatedBaseInspector : ILinkInspector
    {
        public LinkSnapshot Snapshot { get; set; } = new("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless);
        public LinkSnapshot Inspect() => Snapshot;
    }

    [Fact]
    public async Task Simulate_7B_7B_And_7B_7C_SingleLink_BareMetal_Acceptance()
    {
        var socket = new SimulatedNl80211Socket();
        var rfkill = new SimulatedRfkillReader();
        var baseInspector = new SimulatedBaseInspector();

        // 1. Configure interface wlan0
        socket.Interfaces.Add(new LinuxNl80211InterfaceInfo(
            IfIndex: 3,
            IfName: "wlan0",
            WiphyIndex: 0,
            WiphyName: "phy0",
            MacAddress: new byte[] { 0x50, 0x3e, 0xaa, 0x11, 0x22, 0x33 },
            IfType: LinuxNl80211Protocol.NL80211_IFTYPE_STATION,
            Ssid: System.Text.Encoding.UTF8.GetBytes("HomeMesh_5G"),
            Frequency: 5180,
            Wdev: 0x100000001UL));

        // 2. Configure associated BSS
        var bss = new LinuxNl80211BssInfo(
            IfIndex: 3,
            Bssid: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            BssidString: "00:11:22:33:44:55",
            SsidBytes: System.Text.Encoding.UTF8.GetBytes("HomeMesh_5G"),
            DisplaySsid: "HomeMesh_5G",
            FrequencyMhz: 5180,
            Status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED,
            SignalMbm: -5800,
            SignalQuality: 90,
            SeenMsAgo: 5,
            MloLinkId: null,
            MldAddress: null,
            MldAddressString: null,
            LastSeenBootTimeNs: 12345678000UL,
            InformationElements: null,
            Wdev: 0x100000001UL,
            Generation: 10);

        socket.BssDump.Add(bss);

        // 3. Configure T0 Station Telemetry
        socket.Station = new LinuxNl80211StationInfo(
            IfIndex: 3,
            PeerMac: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            PeerMacString: "00:11:22:33:44:55",
            Generation: 10,
            SignalDbm: -58,
            SignalAverageDbm: -57,
            RxBytes: 104857600UL, // 100 MB
            TxBytes: 52428800UL,  // 50 MB
            RxPackets: 75000,
            TxPackets: 42000,
            TxRetries: 12,
            TxFailed: 0,
            ConnectedTimeSeconds: 3600,
            TxRate: new LinuxNl80211RateInfo(866700000UL, 8667, 9, 9, 2, null, null, null, null, null, null, null, false, true, false, false, true),
            RxRate: null,
            ExpectedThroughputKbps: 650000,
            RxDurationUsec: null,
            TxDurationUsec: null,
            AssociationBootTimeNs: 10000000000UL);

        // 4. Run Production Wi-Fi Link Inspector
        var inspector = new LinuxWifiLinkInspector(baseInspector, "wlan0", socket, rfkill);
        var initialSnapshot = inspector.Inspect();

        Assert.NotNull(initialSnapshot.Wireless);
        Assert.Equal("HomeMesh_5G", initialSnapshot.Wireless.Ssid);
        Assert.Equal("00:11:22:33:44:55", initialSnapshot.Wireless.Bssid);
        Assert.Equal(36, initialSnapshot.Wireless.Channel);
        Assert.Equal(-58, initialSnapshot.Wireless.MeasuredRssiDbm);

        // 5. Query Direct Composed Observation T0
        var radio = inspector.Radio;
        var t0 = await radio.ReadComposedAssociationObservationAsync("wlan0");
        Assert.NotNull(t0);
        Assert.Equal(LinuxWirelessAssociationState.Associated, t0.State);
        Assert.True(t0.ContinuityVerified);

        // 6. Simulate Traffic Burst -> Advance Counters for T1
        socket.Station = socket.Station with
        {
            RxBytes = socket.Station.RxBytes + 5242880UL, // +5 MB
            TxBytes = socket.Station.TxBytes + 1048576UL, // +1 MB
            RxPackets = socket.Station.RxPackets + 3500,
            TxPackets = socket.Station.TxPackets + 800
        };

        var t1 = await radio.ReadComposedAssociationObservationAsync("wlan0");
        Assert.NotNull(t1);

        // 7. Query Cached AP Evidence
        var ap = await radio.ReadAccessPointAsync("wlan0", "HomeMesh_5G", "00:11:22:33:44:55");
        Assert.NotNull(ap);
        Assert.Equal(36, ap.Channel);
        Assert.Equal(-58, ap.Rssi);

        // 8. Run Full Evaluator Matrix
        var verdicts = new Dictionary<string, string>();
        var statusLines = new[] { "Name: iem", "CapEff:\t0000000000000000", "CapAmb:\t0000000000000000" };

        var (zeroCap, _, _, _) = LinuxWifiAcceptanceEvaluator.EvaluateCapabilities(statusLines);
        verdicts["ZeroCapabilities"] = zeroCap;

        var (ifIdent, _) = LinuxWifiAcceptanceEvaluator.EvaluateInterfaceIdentity("wlan0", initialSnapshot, t0);
        verdicts["InterfaceIdentity"] = ifIdent;

        var (assoc, _) = LinuxWifiAcceptanceEvaluator.EvaluateAssociationTruth(t0);
        verdicts["AssociationTruth"] = assoc;

        var (continuity, _) = LinuxWifiAcceptanceEvaluator.EvaluateContinuityTruth(t0);
        verdicts["ContinuityTruth"] = continuity;

        var (prodProj, _) = LinuxWifiAcceptanceEvaluator.EvaluateProductionProjectionTruth(initialSnapshot, t0);
        verdicts["ProductionProjectionTruth"] = prodProj;

        var (staPeer, _) = LinuxWifiAcceptanceEvaluator.EvaluateStationPeerTruth(t0);
        verdicts["StationPeerTruth"] = staPeer;

        var (cachedBss, _) = LinuxWifiAcceptanceEvaluator.EvaluateCachedBssTruth(t0, ap);
        verdicts["CachedBssTruth"] = cachedBss;

        var (apEv, _) = LinuxWifiAcceptanceEvaluator.EvaluateAccessPointEvidence(t0, ap);
        verdicts["AccessPointEvidence"] = apEv;

        var (numFid, _) = LinuxWifiAcceptanceEvaluator.EvaluateNumericFidelity(t0, t1);
        verdicts["NumericFidelity"] = numFid;

        var (mloQual, _) = LinuxWifiAcceptanceEvaluator.EvaluateMloHardwareQualification(t0);
        verdicts["MloHardwareQualification"] = mloQual;

        // 9. Compute Overall Suite Verdict
        var (overall, exitCode) = LinuxWifiAcceptanceEvaluator.ComputeOverallVerdict(verdicts);

        Assert.Equal(WifiAcceptanceVerdict.Pass, overall);
        Assert.Equal(0, exitCode);
        Assert.Equal(WifiAcceptanceVerdict.NotApplicable, verdicts["MloHardwareQualification"]);

        // 10. Generate Simulated Acceptance Artifacts
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InternetEvidenceMonitor.slnx")) && !File.Exists(Path.Combine(dir.FullName, "InternetEvidenceMonitor.sln")))
        {
            dir = dir.Parent;
        }
        var rootDir = dir?.FullName ?? AppContext.BaseDirectory;
        var acceptanceDir = Path.Combine(rootDir, "artifacts/acceptance/3.1-7B");
        Directory.CreateDirectory(acceptanceDir);

        var reportJsonPath = Path.Combine(acceptanceDir, "wifi-simulated.json");
        var reportMdPath = Path.Combine(acceptanceDir, "wifi-simulated.md");

        var reportObj = new
        {
            OverallVerdict = overall,
            ExitCode = exitCode,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
            Architecture = "X64",
            OsDescription = "Linux Simulated (Kernel 6.8.0-generic cfg80211/nl80211)",
            CapEff = "0000000000000000",
            CapAmb = "0000000000000000",
            Verdicts = verdicts,
            Snapshot = initialSnapshot,
            ObservationT0 = t0,
            ObservationT1 = t1,
            AccessPoint = ap
        };

        await File.WriteAllTextAsync(reportJsonPath, JsonSerializer.Serialize(reportObj, new JsonSerializerOptions { WriteIndented = true }));

        var mdContent = $@"# 3.1-7B · Linux Bare-Metal Wi-Fi Simulated Acceptance Report

- **Overall Verdict**: **{overall}** (Exit Code `{exitCode}`)
- **Timestamp UTC**: `{DateTimeOffset.UtcNow:o}`
- **Adapter**: `wlan0` (phy0) - Simulating Intel AX200 Wi-Fi 6 (802.11ax / nl80211)
- **Zero Capabilities**: `CapEff=0000000000000000`, `CapAmb=0000000000000000` (PASS)

## Gate Verdicts Matrix

| Gate | Category | Verdict | Note |
|---|---|---|---|
| `ZeroCapabilities` | Mandatory | **PASS** | Strict CapEff=0, CapAmb=0 verified |
| `InterfaceIdentity` | Mandatory | **PASS** | IFINDEX=3, WDEV=0x100000001, WIPHY=0 (phy0) |
| `AssociationTruth` | Mandatory | **PASS** | Associated to 'HomeMesh_5G' (BSSID 00:11:22:33:44:55, 5180 MHz / Ch 36) |
| `ContinuityTruth` | Mandatory | **PASS** | Temporal continuity verified across multi-part queries |
| `ProductionProjectionTruth` | Mandatory | **PASS** | Production LinkSnapshot matches direct nl80211 observation |
| `StationPeerTruth` | Mandatory | **PASS** | Station peer matches associated BSSID |
| `CachedBssTruth` | Mandatory | **PASS** | Cached passive GET_SCAN dump resolves BSSID without active scan |
| `AccessPointEvidence` | Mandatory | **PASS** | Channel 36 and RSSI -58 dBm verified |
| `NumericFidelity` | Mandatory | **PASS** | RX/TX bytes & packets strictly non-decreasing over traffic interval |
| `MloHardwareQualification` | Optional | **NOT_APPLICABLE** | Non-MLO single-link hardware (Wi-Fi 6) |
";
        await File.WriteAllTextAsync(reportMdPath, mdContent);
    }

    [Fact]
    public async Task Simulate_7B_7D_Disconnect_Reconnect_Transition_No_Stale_Leaks()
    {
        var socket = new SimulatedNl80211Socket();
        var rfkill = new SimulatedRfkillReader();
        var baseInspector = new SimulatedBaseInspector();

        // 1. Initial Associated State
        socket.Interfaces.Add(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", new byte[] { 0x50, 0x3e, 0xaa, 0x11, 0x22, 0x33 }, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, System.Text.Encoding.UTF8.GetBytes("NetA"), 5180, 0x100000001UL));
        socket.BssDump.Add(new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 }, "00:11:22:33:44:01", System.Text.Encoding.UTF8.GetBytes("NetA"), "NetA", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 5, null, null, null, null, null, 0x100000001UL, 1));
        socket.Station = new LinuxNl80211StationInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 }, "00:11:22:33:44:01", 1, -60, -59, 1000, 500, 10, 5, 0, 0, 10, null, null, null, null, null, 10000UL);

        var inspector = new LinuxWifiLinkInspector(baseInspector, "wlan0", socket, rfkill);
        var snap1 = inspector.Inspect();
        Assert.NotNull(snap1.Wireless);
        Assert.Equal("NetA", snap1.Wireless.Ssid);
        Assert.Equal("00:11:22:33:44:01", snap1.Wireless.Bssid);

        // 2. Disconnect Transition (BSS dump returns empty associated list, link status Down)
        socket.BssDump.Clear();
        socket.Station = null;
        baseInspector.Snapshot = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless);

        var obsDisc = await inspector.Radio.ReadComposedAssociationObservationAsync("wlan0");
        Assert.NotNull(obsDisc);
        Assert.Equal(LinuxWirelessAssociationState.NotAssociated, obsDisc.State);
        Assert.Null(obsDisc.StationInfo);

        var snapDisc = inspector.Inspect();
        Assert.Null(snapDisc.Wireless?.Bssid); // BSSID must strictly be cleared on disconnect

        // 3. Reconnect Transition to NetB
        baseInspector.Snapshot = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless);
        socket.BssDump.Add(new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 }, "00:11:22:33:44:02", System.Text.Encoding.UTF8.GetBytes("NetB"), "NetB", 5200, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -5500, 92, 2, null, null, null, null, null, 0x100000001UL, 2));
        socket.Station = new LinuxNl80211StationInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 }, "00:11:22:33:44:02", 2, -55, -54, 50, 20, 2, 1, 0, 0, 1, null, null, null, null, null, 20000UL);

        var snapRecon = inspector.Inspect();
        Assert.NotNull(snapRecon.Wireless);
        Assert.Equal("NetB", snapRecon.Wireless.Ssid);
        Assert.Equal("00:11:22:33:44:02", snapRecon.Wireless.Bssid);
        Assert.Equal(40, snapRecon.Wireless.Channel);
        Assert.Equal(-55, snapRecon.Wireless.MeasuredRssiDbm);
    }
}
