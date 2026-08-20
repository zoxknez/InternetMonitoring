using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Classification;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network;
using IEM.Linux.Time;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Proves Invariants 249–254 and Core attribution truth table (§7.11 / §7.14).
/// Proves that the Linux Wi-Fi adapter produces facts, while IEM.Core remains the sole attribution authority.
/// </summary>
public sealed class LinuxWifiAttributionInvariantTests
{
    private readonly StateClassifier _classifier = new();

    private sealed class StubLinkInspector : ILinkInspector
    {
        public LinkSnapshot Snapshot { get; set; }

        public StubLinkInspector(LinkStatus status = LinkStatus.Down, LinkMedium medium = LinkMedium.Wireless)
        {
            Snapshot = new LinkSnapshot("wlan0", "wlan0", status, medium);
        }

        public LinkSnapshot Inspect() => Snapshot;
    }

    private sealed class StubNativeClock : ILinuxNativeClock
    {
        public long CurrentBootTimeSec { get; set; } = 1000;
        public long CurrentBootTimeNsec { get; set; } = 0;

        public void GetTime(int clkId, out LinuxTimeSpec ts)
        {
            ts = new LinuxTimeSpec { TvSec = CurrentBootTimeSec, TvNsec = CurrentBootTimeNsec };
        }
    }

    private sealed class StubRfkillReader : ILinuxRfkillReader
    {
        public bool? ObservationPresent { get; set; } = true;
        public bool HardBlocked { get; set; }
        public bool SoftBlocked { get; set; }

        public LinuxRfkillObservation? ReadObservationForWiphy(uint wiphyIndex, string? ifname = null)
        {
            if (ObservationPresent == false) return null;
            return new LinuxRfkillObservation(0, wiphyIndex, HardBlocked, SoftBlocked, LinuxRfkillEvidenceBasis.DevRfkill);
        }
    }

    private sealed class MockNl80211Socket : ILinuxNl80211Socket
    {
        public GenlFamilyInfo? Family { get; set; } = new(28, "nl80211", 1, 0, 32, new Dictionary<string, uint>());
        public List<LinuxNl80211InterfaceInfo> Interfaces { get; } = new();
        public List<LinuxNl80211BssInfo> BssDump { get; } = new();
        public LinuxNl80211DumpStatus BssDumpStatus { get; set; } = LinuxNl80211DumpStatus.Complete;
        public int DumpBssErrorCode { get; set; } = 0;
        public bool IsAvailable { get; set; } = true;

        public MockNl80211Socket()
        {
            Interfaces.Add(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, 5180, 0x1000UL));
        }

        public Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default) =>
            Task.FromResult(IsAvailable ? Family : null);

        public Task<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> DumpInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Interfaces, LinuxNl80211DumpStatus.Complete));

        public Task<LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>> DumpWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Complete));

        public Task<LinuxNl80211DumpResult<LinuxNl80211BssInfo>> DumpBssAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(BssDump, BssDumpStatus, DumpBssErrorCode, SawDone: BssDumpStatus == LinuxNl80211DumpStatus.Complete));

        public Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interfaces);

        public Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<LinuxNl80211WiphyInfo>());

        public Task<LinuxNl80211SingleResult<LinuxNl80211StationInfo>> GetStationAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, byte[] peerMac, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Incomplete));

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static ProbeCycle BuildCycle(LinkSnapshot link, bool externalFail = true)
    {
        var builder = CycleBuilder.Wireless().WithLink(link);
        if (externalFail)
        {
            builder.AllExternalFail();
        }
        return builder.Build();
    }

    private static LinuxNl80211BssInfo CreateBss(string ssid, uint seenMsAgo, uint status = 0)
    {
        return new LinuxNl80211BssInfo(
            IfIndex: 3,
            Bssid: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            BssidString: "00:11:22:33:44:55",
            SsidBytes: System.Text.Encoding.UTF8.GetBytes(ssid),
            DisplaySsid: ssid,
            FrequencyMhz: 5180,
            Status: status,
            SignalMbm: -6000,
            SignalQuality: 85,
            SeenMsAgo: seenMsAgo,
            MloLinkId: null,
            MldAddress: null,
            MldAddressString: null,
            LastSeenBootTimeNs: null,
            InformationElements: null,
            Wdev: 0x1000UL,
            Generation: 1);
    }

    // 1. Fresh CompletedScan + SSID absent + RadioOn=true + Link=Down -> SsidVisible=false -> WifiRadioDown
    [Fact]
    public void Invariant250_Scenario1_Fresh_CompletedScan_Absent_Yields_WifiRadioDown()
    {
        var inner = new StubLinkInspector(LinkStatus.Down, LinkMedium.Wireless);
        var socket = new MockNl80211Socket();
        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker();
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 1000_000_000_000UL);

        // Associated BSS present in remembered state, but in scan dump only OtherNet is fresh
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.False(visible); // CompletedScan provenance permits false

        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = false,
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.WifiRadioDown, verdict.State);
    }

    // 1b. R1 Characterization: Opportunistic cache (no scan completion event) + SSID absent -> SsidVisible=null -> MUST be AdapterDown, NEVER WifiRadioDown
    [Fact]
    public void Invariant250_Scenario1b_Opportunistic_Cache_Absent_MustYield_AdapterDown_Never_WifiRadioDown()
    {
        var inner = new StubLinkInspector(LinkStatus.Down, LinkMedium.Wireless);
        var socket = new MockNl80211Socket();
        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };

        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        // No scan completion tracker -> Opportunistic cache only
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.Null(visible); // Invariant 250: absence in opportunistic cache is unknown (null)

        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = visible, // null
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State); // MUST be AdapterDown
        Assert.NotEqual(NetworkState.WifiRadioDown, verdict.State); // MUST NEVER be WifiRadioDown
    }

    // 2. Fresh complete scan + SSID present + RadioOn=true + Link=Down -> SsidVisible=true -> AdapterDown
    [Fact]
    public void Invariant250_Scenario2_Fresh_Complete_Scan_Present_Yields_AdapterDown()
    {
        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("HomeMesh", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.True(visible);

        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = true,
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    // 3. Stale scan + SSID absent -> SsidVisible=null -> AdapterDown
    [Fact]
    public void Invariant250_Scenario3_Stale_Scan_Absent_Yields_AdapterDown()
    {
        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 240000)); // > 3 min
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.Null(visible);

        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = null,
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    // 4. Incomplete scan + SSID absent -> null -> AdapterDown
    [Fact]
    public void Invariant250_Scenario4_Incomplete_Scan_Absent_Yields_AdapterDown()
    {
        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Incomplete;

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.Null(visible);

        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = null,
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    // 5. Interrupted scan + SSID absent -> null -> AdapterDown
    [Fact]
    public void Invariant250_Scenario5_Interrupted_Scan_Absent_Yields_AdapterDown()
    {
        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Interrupted;

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.Null(visible);

        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = null,
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    // 6. Empty complete GET_SCAN, no scan-done evidence -> null -> AdapterDown
    [Fact]
    public void Invariant250_Scenario6_Empty_Complete_Scan_Yields_AdapterDown()
    {
        var socket = new MockNl80211Socket();
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.Null(visible);

        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = null,
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    // 7. Fresh partial result contains target SSID -> true
    [Fact]
    public void Invariant250_Scenario7_Fresh_Partial_Present_Yields_True()
    {
        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("HomeMesh", seenMsAgo: 2000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Interrupted;

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.True(visible);
    }

    // 8. Stale result contains target SSID -> null
    [Fact]
    public void Invariant250_Scenario8_Stale_Present_Yields_Null()
    {
        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("HomeMesh", seenMsAgo: 240000)); // Stale
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var visible = radio.IsSsidVisible("HomeMesh");
        Assert.Null(visible); // Indeterminate
    }

    // 9. RadioOn=null + fresh complete SSID absent -> SsidVisible=false may exist -> classifier nevertheless AdapterDown
    [Fact]
    public void Invariant249_Scenario9_RadioOn_Null_Yields_AdapterDown()
    {
        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = false,
                RadioOn = null,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    // 10. RadioOn=false -> never WifiRadioDown (produces AdapterDown)
    [Fact]
    public void Invariant249_Scenario10_RadioOn_False_Never_Yields_WifiRadioDown()
    {
        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = false,
                RadioOn = false,
                MeasuredRssiDbm = -60
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link));
        Assert.Equal(NetworkState.AdapterDown, verdict.State);
    }

    // 11. RSSI=-85 + link UP + probes successful -> RSSI is telemetry only -> no connectivity outage
    [Fact]
    public void Invariant253_Scenario11_Low_Rssi_With_Successful_Probes_Remains_Ok()
    {
        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 10, 36)
            {
                SsidVisibleInScan = true,
                RadioOn = true,
                MeasuredRssiDbm = -85
            }
        };

        var verdict = _classifier.Classify(BuildCycle(link, externalFail: false));
        Assert.Equal(NetworkState.Ok, verdict.State);
    }

    // 12. nl80211 unavailable -> Wireless metadata unknown -> generic link/probes remain active
    [Fact]
    public void Invariant252_Scenario12_Nl80211_Unavailable_Preserves_Generic_Monitoring()
    {
        var inner = new StubLinkInspector(LinkStatus.Up, LinkMedium.Wireless);
        var socket = new MockNl80211Socket { IsAvailable = false };
        var inspector = new LinuxWifiLinkInspector(inner, "wlan0", socket);

        var snapshot = inspector.Inspect();
        Assert.NotNull(snapshot);
        Assert.Equal(LinkStatus.Up, snapshot.Status);
        Assert.Null(snapshot.Wireless); // Gracefully absent

        var verdict = _classifier.Classify(BuildCycle(snapshot, externalFail: false));
        Assert.Equal(NetworkState.Ok, verdict.State);
    }
}
