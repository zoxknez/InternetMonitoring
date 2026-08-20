using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Classification;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network.Netlink;
using IEM.Linux.Time;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxWifiEventObserverTests
{
    private sealed class StubNativeClock : ILinuxNativeClock
    {
        public long CurrentBootTimeSec { get; set; } = 1000;
        public long CurrentBootTimeNsec { get; set; } = 0;
        public bool ShouldFail { get; set; }

        public void GetTime(int clkId, out LinuxTimeSpec ts)
        {
            if (ShouldFail)
            {
                throw new IOException("Clock query failed");
            }
            ts = new LinuxTimeSpec { TvSec = CurrentBootTimeSec, TvNsec = CurrentBootTimeNsec };
        }

        public ulong BootTimeNs => ((ulong)CurrentBootTimeSec * 1_000_000_000UL) + (ulong)CurrentBootTimeNsec;
    }

    private sealed class StubRfkillReader : ILinuxRfkillReader
    {
        public bool HardBlocked { get; set; }
        public bool SoftBlocked { get; set; }

        public LinuxRfkillObservation? ReadObservationForWiphy(uint wiphyIndex, string? ifname = null)
        {
            return new LinuxRfkillObservation(0, wiphyIndex, HardBlocked, SoftBlocked, LinuxRfkillEvidenceBasis.DevRfkill);
        }
    }

    private sealed class MockNl80211Socket : ILinuxNl80211Socket
    {
        public GenlFamilyInfo? Family { get; set; } = new(28, "nl80211", 1, 0, 32, new Dictionary<string, uint> { { "scan", 42 } });
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

    private static byte[] BuildScanEventDatagram(
        ushort familyId,
        byte genlCmd,
        int? ifIndex,
        ulong? wdev,
        uint seq = 0)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var attrStream = new MemoryStream();
        var attrBw = new BinaryWriter(attrStream);

        if (ifIndex.HasValue)
        {
            attrBw.Write((ushort)8); // len = 4 + 4
            attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
            attrBw.Write(ifIndex.Value);
        }

        if (wdev.HasValue)
        {
            attrBw.Write((ushort)12); // len = 4 + 8
            attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
            attrBw.Write(wdev.Value);
        }

        var attrBytes = attrStream.ToArray();
        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize + attrBytes.Length;

        // 1. nlmsghdr
        bw.Write(totalLen);
        bw.Write(familyId);
        bw.Write((ushort)0); // flags
        bw.Write(seq);
        bw.Write(0U); // pid

        // 2. genlmsghdr
        bw.Write(genlCmd);
        bw.Write((byte)1); // version
        bw.Write((ushort)0); // reserved

        // 3. attributes
        bw.Write(attrBytes);

        return ms.ToArray();
    }

    private static LinuxNl80211BssInfo CreateBss(
        string ssid = "OfficeNet",
        uint? seenMsAgo = null,
        ulong? lastSeenBootTimeNs = null,
        string bssid = "00:11:22:33:44:55",
        uint freq = 5180)
    {
        return new LinuxNl80211BssInfo(
            IfIndex: 3,
            Bssid: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            BssidString: bssid,
            SsidBytes: System.Text.Encoding.UTF8.GetBytes(ssid),
            DisplaySsid: ssid,
            FrequencyMhz: freq,
            Status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED,
            SignalMbm: -6000,
            SignalQuality: 85,
            SeenMsAgo: seenMsAgo,
            MloLinkId: null,
            MldAddress: null,
            MldAddressString: null,
            LastSeenBootTimeNs: lastSeenBootTimeNs,
            InformationElements: null,
            Wdev: 0x1000UL,
            Generation: 1);
    }

    // 1. Runtime family group "scan" resolved dynamically
    [Fact]
    public void EventObserver_Dynamic_Scan_Multicast_Resolution()
    {
        var mcast = new Dictionary<string, uint> { { "scan", 42 } };
        var family = new GenlFamilyInfo(28, "nl80211", 1, 0, 256, mcast);

        Assert.True(family.MulticastGroups.TryGetValue("scan", out var scanGroupId));
        Assert.Equal(42U, scanGroupId);
    }

    // 2. Event payload processing with NEW_SCAN_RESULTS
    [Fact]
    public void EventObserver_Processes_NewScanResults_Exact_IfIndex_And_Wdev()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500, CurrentBootTimeNsec = 0 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL);

        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        var record = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(record);
        Assert.Equal(LinuxWifiScanEventStatus.Completed, record.Status);
        Assert.Equal(3, record.IfIndex);
        Assert.Equal(0x1000UL, record.Wdev);
        Assert.Equal(500_000_000_000UL, record.ObservedAtBootTimeNs);
    }

    // 3. SCAN_ABORTED invalidates completion provenance
    [Fact]
    public void EventObserver_Processes_ScanAborted_Invalidates_Provenance()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // Initial completion
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 500_000_000_000UL);

        // Aborted event comes in
        var abortBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_SCAN_ABORTED,
            ifIndex: 3,
            wdev: 0x1000UL);

        observer.ProcessEventPayload(abortBytes, nl80211FamilyId: 28);

        var record = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(record);
        Assert.Equal(LinuxWifiScanEventStatus.Aborted, record.Status);

        // Evaluator with Aborted status must produce null (not false)
        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 501_000_000_000UL);

        Assert.Equal(LinuxWifiScanCompleteness.Partial, snap.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.OpportunisticKernelCache, snap.EvidenceBasis);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 501_000_000_000UL));
    }

    // 4. Event on wlan1 never completes wlan0
    [Fact]
    public void EventObserver_Wlan1_Event_Never_Completes_Wlan0()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 4, // wlan1
            wdev: 0x2000UL);

        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        var recordWlan0 = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.Null(recordWlan0);

        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 500_000_000_000UL);

        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 500_000_000_000UL));
    }

    // 5. Completed event without WDEV never proves absence for known-WDEV target
    [Fact]
    public void EventObserver_Completed_Without_Wdev_Never_Proves_Absence_For_Known_Wdev()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: null); // No WDEV in event

        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        // Querying for target with known WDEV=0x1000UL
        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 500_000_000_000UL);

        Assert.Equal(LinuxWifiScanCompleteness.Partial, snap.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.OpportunisticKernelCache, snap.EvidenceBasis);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 500_000_000_000UL));
    }

    // 6. Unknown CLOCK_BOOTTIME at event -> never CompletedScan freshness
    [Fact]
    public void EventObserver_Unknown_BootTime_At_Event_Never_Yields_CompletedScan()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { ShouldFail = true };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL);

        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        var record = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(record);
        Assert.Null(record.ObservedAtBootTimeNs); // Unknown clock

        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 500_000_000_000UL);

        Assert.Equal(LinuxWifiScanCompleteness.Partial, snap.Completeness);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 500_000_000_000UL));
    }

    // 7. Unknown CLOCK_BOOTTIME at evaluation -> never false
    [Fact]
    public void EventObserver_Unknown_BootTime_At_Evaluation_Never_Yields_False()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 500_000_000_000UL);

        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: null);

        Assert.Equal(LinuxWifiScanCompleteness.Partial, snap.Completeness);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", null));
    }

    // 8. Completion timestamp in future -> null
    [Fact]
    public void EventObserver_Future_Completion_Timestamp_Yields_Null()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        // Completion timestamp is in future (600s vs current 500s)
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 600_000_000_000UL);

        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 500_000_000_000UL);

        Assert.Equal(LinuxWifiScanCompleteness.Partial, snap.Completeness);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 500_000_000_000UL));
    }

    // 9. Completion exact 180s boundary -> eligible
    [Fact]
    public void EventObserver_Exact_180s_Boundary_Is_Eligible()
    {
        const ulong nowBootNs = 1_000_000_000_000UL;
        ulong completionBootNs = nowBootNs - (180UL * 1_000_000_000UL); // 180s ago

        var tracker = new LinuxWifiScanCompletionTracker();
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, completionBootNs);

        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);

        Assert.Equal(LinuxWifiScanCompleteness.Complete, snap.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.CompletedScan, snap.EvidenceBasis);
        Assert.False(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", nowBootNs));
    }

    // 10. Completion 180s + 1ms -> stale -> null
    [Fact]
    public void EventObserver_180s_Plus_1ms_Stale_Yields_Null()
    {
        const ulong nowBootNs = 1_000_000_000_000UL;
        ulong completionBootNs = nowBootNs - (180_001UL * 1_000_000UL); // 180.001s ago

        var tracker = new LinuxWifiScanCompletionTracker();
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, completionBootNs);

        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);

        Assert.Equal(LinuxWifiScanCompleteness.Partial, snap.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.OpportunisticKernelCache, snap.EvidenceBasis);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", nowBootNs));
    }

    // 11. Malformed event, wrong family, and unrelated command are ignored
    [Fact]
    public void EventObserver_Ignores_Malformed_WrongFamily_And_UnrelatedCommand()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // 1. Wrong family ID (e.g. 99 instead of 28)
        var wrongFamilyBytes = BuildScanEventDatagram(
            familyId: 99,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL);
        observer.ProcessEventPayload(wrongFamilyBytes, nl80211FamilyId: 28);
        Assert.Null(tracker.GetLastScanCompletion(3, 0x1000UL));

        // 2. Unrelated command (e.g. NL80211_CMD_NEW_STATION = 19)
        var unrelatedCmdBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_STATION,
            ifIndex: 3,
            wdev: 0x1000UL);
        observer.ProcessEventPayload(unrelatedCmdBytes, nl80211FamilyId: 28);
        Assert.Null(tracker.GetLastScanCompletion(3, 0x1000UL));

        // 3. Malformed event without ifindex
        var malformedBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: null, // missing ifindex
            wdev: 0x1000UL);
        observer.ProcessEventPayload(malformedBytes, nl80211FamilyId: 28);
        Assert.Null(tracker.GetLastScanCompletion(3, 0x1000UL));
    }

    // 12. Full Production End-to-End Composition Test (The Master Test)
    // Simulated multicast NEW_SCAN_RESULTS -> real event parser in observer -> shared tracker -> LinuxNl80211Radio -> false -> StateClassifier -> WifiRadioDown
    [Fact]
    public async Task EventObserver_Full_EndToEnd_Production_Composition_Yields_WifiRadioDown()
    {
        var classifier = new StateClassifier();
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000, CurrentBootTimeNsec = 0 };

        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var socket = new MockNl80211Socket();

        // In the scan cache, only OtherNet is present
        var bssOther = CreateBss("OtherNet", seenMsAgo: 5000);
        socket.BssDump.Add(bssOther);
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        // Before scan event: absent target SSID yields null (not false!)
        var visibleBefore = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.Null(visibleBefore);

        // Step 1: Real kernel multicast NEW_SCAN_RESULTS arrives and is parsed by observer
        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL);

        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        // Step 2: Now radio evaluates SSID visibility with CompletedScan provenance
        var visibleAfter = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.False(visibleAfter); // Evaluates to false!

        // Step 3: Link snapshot with Link=Down and SsidVisibleInScan=false
        var link = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Down, LinkMedium.Wireless)
        {
            Wireless = new WirelessSnapshot("HomeMesh", "00:11:22:33:44:55", 85, 36)
            {
                SsidVisibleInScan = visibleAfter, // false
                RadioOn = true,
                MeasuredRssiDbm = -60
            }
        };

        var cycle = CycleBuilder.Wireless().WithLink(link).Build();

        // Step 4: Classifier correctly attributes to WifiRadioDown
        var verdict = classifier.Classify(cycle);
        Assert.Equal(NetworkState.WifiRadioDown, verdict.State);
    }
}
