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
        uint seq = 0,
        IReadOnlyList<uint>? frequencies = null,
        IReadOnlyList<byte[]>? ssids = null,
        byte[]? trailingGarbage = null,
        bool duplicateIfIndex = false,
        bool duplicateWdev = false,
        ushort? unknownAttrType = null,
        byte[]? unknownAttrVal = null,
        int? truncatedWdevLength = null)
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

            if (duplicateIfIndex)
            {
                attrBw.Write((ushort)8);
                attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
                attrBw.Write(ifIndex.Value);
            }
        }

        if (wdev.HasValue)
        {
            if (truncatedWdevLength.HasValue)
            {
                ushort nlaLen = (ushort)(4 + truncatedWdevLength.Value);
                attrBw.Write(nlaLen);
                attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
                attrBw.Write(new byte[truncatedWdevLength.Value]);
            }
            else
            {
                attrBw.Write((ushort)12); // len = 4 + 8
                attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
                attrBw.Write(wdev.Value);

                if (duplicateWdev)
                {
                    attrBw.Write((ushort)12);
                    attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
                    attrBw.Write(wdev.Value);
                }
            }
        }

        if (frequencies != null)
        {
            var freqStream = new MemoryStream();
            var freqBw = new BinaryWriter(freqStream);
            int idx = 1;
            foreach (var f in frequencies)
            {
                freqBw.Write((ushort)8);
                freqBw.Write((ushort)idx++);
                freqBw.Write(f);
            }
            var freqBytes = freqStream.ToArray();
            ushort attrLen = (ushort)(4 + freqBytes.Length);
            attrBw.Write(attrLen);
            attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_SCAN_FREQUENCIES);
            attrBw.Write(freqBytes);
            int pad = (4 - (attrLen % 4)) % 4;
            for (int p = 0; p < pad; p++) attrBw.Write((byte)0);
        }

        if (ssids != null)
        {
            var ssidStream = new MemoryStream();
            var ssidBw = new BinaryWriter(ssidStream);
            int idx = 1;
            foreach (var s in ssids)
            {
                ushort nlaLen = (ushort)(4 + s.Length);
                ssidBw.Write(nlaLen);
                ssidBw.Write((ushort)idx++);
                ssidBw.Write(s);
                int pad = (4 - (nlaLen % 4)) % 4;
                for (int p = 0; p < pad; p++) ssidBw.Write((byte)0);
            }
            var ssidBytes = ssidStream.ToArray();
            ushort attrLen = (ushort)(4 + ssidBytes.Length);
            attrBw.Write(attrLen);
            attrBw.Write(LinuxNl80211Protocol.NL80211_ATTR_SCAN_SSIDS);
            attrBw.Write(ssidBytes);
            int topPad = (4 - (attrLen % 4)) % 4;
            for (int p = 0; p < topPad; p++) attrBw.Write((byte)0);
        }

        if (unknownAttrType.HasValue && unknownAttrVal != null)
        {
            ushort nlaLen = (ushort)(4 + unknownAttrVal.Length);
            attrBw.Write(nlaLen);
            attrBw.Write(unknownAttrType.Value);
            attrBw.Write(unknownAttrVal);
            int pad = (4 - (nlaLen % 4)) % 4;
            for (int p = 0; p < pad; p++) attrBw.Write((byte)0);
        }

        if (trailingGarbage != null)
        {
            attrBw.Write(trailingGarbage);
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

    // 2. Event payload processing with TRIGGER_SCAN + NEW_SCAN_RESULTS and Wildcard domain
    [Fact]
    public void EventObserver_Processes_TriggerScan_And_NewScanResults_Inherits_Domain()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500, CurrentBootTimeNsec = 0 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // 1. TRIGGER_SCAN arrives with no frequencies (AllAllowed) and wildcard active probe
        var triggerBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            ifIndex: 3,
            wdev: 0x1000UL,
            frequencies: null, // AllAllowed
            ssids: new byte[][] { Array.Empty<byte>() }); // Wildcard active

        observer.ProcessEventPayload(triggerBytes, nl80211FamilyId: 28);
        var startedRecord = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(startedRecord);
        Assert.Equal(LinuxWifiScanEventStatus.Started, startedRecord.Status);
        Assert.Equal(LinuxWifiScanFrequencyScope.AllAllowed, startedRecord.Domain?.FrequencyScope);
        Assert.Equal(LinuxWifiScanSsidScope.WildcardActive, startedRecord.Domain?.SsidScope);

        // 2. NEW_SCAN_RESULTS arrives for same adapter
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
        Assert.Equal(2L, record.Revision);
        Assert.NotNull(record.Domain);
        Assert.Equal(LinuxWifiScanFrequencyScope.AllAllowed, record.Domain.FrequencyScope);
        Assert.Equal(LinuxWifiScanSsidScope.WildcardActive, record.Domain.SsidScope);
    }

    // 3. SCAN_ABORTED invalidates completion provenance and bumps revision
    [Fact]
    public void EventObserver_Processes_ScanAborted_Invalidates_Provenance()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // Initial completion
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 500_000_000_000UL, LinuxWifiScanDomain.AllAllowedWildcard());

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
        Assert.Equal(2L, record.Revision);

        // Evaluator with Aborted status must produce null (not false)
        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 501_000_000_000UL);

        Assert.Equal(LinuxWifiScanCompleteness.Partial, snap.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.OpportunisticKernelCache, snap.EvidenceBasis);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 501_000_000_000UL));
    }

    // 4. TRIGGER_SCAN invalidates prior completion and sets Started
    [Fact]
    public void EventObserver_TriggerScan_Sets_Started_And_Invalidates_Prior_Completion()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // Initial completion
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 500_000_000_000UL, LinuxWifiScanDomain.AllAllowedWildcard());
        Assert.Equal(1L, tracker.GetLastScanCompletion(3, 0x1000UL)?.Revision);

        // TRIGGER_SCAN arrives (scan started by another process)
        var triggerBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            ifIndex: 3,
            wdev: 0x1000UL);

        observer.ProcessEventPayload(triggerBytes, nl80211FamilyId: 28);

        var record = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(record);
        Assert.Equal(LinuxWifiScanEventStatus.Started, record.Status);
        Assert.Equal(2L, record.Revision);

        // Evaluator with Started status must produce null (not false)
        var bss = CreateBss("OtherNet", seenMsAgo: 5000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 501_000_000_000UL);

        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 501_000_000_000UL));
    }

    // 5. Scheduled scan lifecycle invalidates negative proof
    [Fact]
    public void EventObserver_ScheduledScan_Events_Invalidate_Negative_Proof()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // 1. Initial valid completed scan
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 500_000_000_000UL, LinuxWifiScanDomain.AllAllowedWildcard());

        // 2. START_SCHED_SCAN arrives
        var startSchedBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_START_SCHED_SCAN,
            ifIndex: 3,
            wdev: 0x1000UL);
        observer.ProcessEventPayload(startSchedBytes, nl80211FamilyId: 28);

        var snap1 = LinuxWifiScanCache.EvaluateScanDump(
            new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { CreateBss("OtherNet", seenMsAgo: 5000) }, LinuxNl80211DumpStatus.Complete),
            ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 501_000_000_000UL);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap1, "HomeMesh", 501_000_000_000UL));

        // 3. SCHED_SCAN_RESULTS arrives -> never eligible for false
        var schedResultsBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_SCHED_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL);
        observer.ProcessEventPayload(schedResultsBytes, nl80211FamilyId: 28);

        var snap2 = LinuxWifiScanCache.EvaluateScanDump(
            new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { CreateBss("OtherNet", seenMsAgo: 5000) }, LinuxNl80211DumpStatus.Complete),
            ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 501_000_000_000UL);
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap2, "HomeMesh", 501_000_000_000UL));
    }

    // 6. Strict ABI parser rejects trailing garbage and malformed NLA
    [Fact]
    public void EventObserver_Strict_ABI_Rejects_TrailingGarbage_And_Duplicate_Singleton_Attrs()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // 1. Valid IFINDEX + 2 trailing garbage bytes -> rejected
        var garbageBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL,
            trailingGarbage: new byte[] { 0xDE, 0xAD });
        observer.ProcessEventPayload(garbageBytes, nl80211FamilyId: 28);
        Assert.Null(tracker.GetLastScanCompletion(3, 0x1000UL));

        // 2. Duplicate IFINDEX -> rejected
        var dupIfIndexBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL,
            duplicateIfIndex: true);
        observer.ProcessEventPayload(dupIfIndexBytes, nl80211FamilyId: 28);
        Assert.Null(tracker.GetLastScanCompletion(3, 0x1000UL));

        // 3. Truncated WDEV (e.g. 4 bytes instead of 8) -> rejected
        var truncWdevBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL,
            truncatedWdevLength: 4);
        observer.ProcessEventPayload(truncWdevBytes, nl80211FamilyId: 28);
        Assert.Null(tracker.GetLastScanCompletion(3, 0x1000UL));

        // 4. Well-formed unknown attribute -> ignored safely
        var unknownAttrBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL,
            unknownAttrType: 9999,
            unknownAttrVal: new byte[] { 1, 2, 3, 4 },
            frequencies: new uint[] { 2412, 5180 },
            ssids: new byte[][] { Array.Empty<byte>() });
        observer.ProcessEventPayload(unknownAttrBytes, nl80211FamilyId: 28);
        Assert.NotNull(tracker.GetLastScanCompletion(3, 0x1000UL));
    }

    // 7. Domain coverage: Explicit SSID scope vs Wildcard vs Passive
    [Fact]
    public void EventObserver_ObservationDomain_Coverage_Rules()
    {
        // 1. Wildcard covers any SSID
        var wildcardDomain = new LinuxWifiScanDomain(
            LinuxWifiScanFrequencyScope.AllAllowed,
            LinuxWifiScanSsidScope.WildcardActive,
            Array.Empty<uint>(),
            new[] { Array.Empty<byte>() });
        Assert.True(wildcardDomain.CoversSsidObservation(System.Text.Encoding.UTF8.GetBytes("HomeMesh")));

        // 2. Explicit SSID containing target covers target
        var targetBytes = System.Text.Encoding.UTF8.GetBytes("HomeMesh");
        var explicitDomain = new LinuxWifiScanDomain(
            LinuxWifiScanFrequencyScope.AllAllowed,
            LinuxWifiScanSsidScope.ExplicitSsids,
            Array.Empty<uint>(),
            new[] { targetBytes, System.Text.Encoding.UTF8.GetBytes("OtherNet") });
        Assert.True(explicitDomain.CoversSsidObservation(targetBytes));

        // 3. Explicit SSID for different network does NOT cover target
        var otherDomain = new LinuxWifiScanDomain(
            LinuxWifiScanFrequencyScope.AllAllowed,
            LinuxWifiScanSsidScope.ExplicitSsids,
            Array.Empty<uint>(),
            new[] { System.Text.Encoding.UTF8.GetBytes("OtherNet") });
        Assert.False(otherDomain.CoversSsidObservation(targetBytes));

        // 4. Passive scan does NOT cover target for negative proof
        var passiveDomain = new LinuxWifiScanDomain(
            LinuxWifiScanFrequencyScope.AllAllowed,
            LinuxWifiScanSsidScope.PassiveOnly,
            Array.Empty<uint>(),
            Array.Empty<byte[]>());
        Assert.False(passiveDomain.CoversSsidObservation(targetBytes));

        // 5. ExplicitSubset frequencies does NOT cover all observation
        var subsetFreqDomain = new LinuxWifiScanDomain(
            LinuxWifiScanFrequencyScope.ExplicitSubset,
            LinuxWifiScanSsidScope.WildcardActive,
            new uint[] { 2412 },
            new[] { Array.Empty<byte>() });
        Assert.False(subsetFreqDomain.CoversSsidObservation(targetBytes));
    }

    // 8. Causal Race Condition Test (The Master Race Test):
    // Old dump: OtherNet only
    // Event arrives during / after GET_SCAN dump -> Revision changed -> must produce null, NEVER false / WifiRadioDown
    [Fact]
    public async Task EventObserver_CausalRace_EventDuringDump_Yields_Null_Never_WifiRadioDown()
    {
        var classifier = new StateClassifier();
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        // Step 1: Initial scan completion at T=950s (valid, revision 1)
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 950_000_000_000UL, LinuxWifiScanDomain.AllAllowedWildcard());
        var preSnapshot = tracker.GetSnapshot(3, 0x1000UL);
        Assert.Equal(1L, preSnapshot?.Revision);

        // Step 2: During GET_SCAN dump, another scan event arrives at T=1000s -> revision becomes 2
        var midDumpEvent = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL,
            frequencies: new uint[] { 2412, 5180 },
            ssids: new byte[][] { Array.Empty<byte>() });

        // Simulate event arrived during dump
        observer.ProcessEventPayload(midDumpEvent, nl80211FamilyId: 28);
        var postSnapshot = tracker.GetSnapshot(3, 0x1000UL);
        Assert.Equal(2L, postSnapshot?.Revision);

        // Evaluator comparing PRE (rev 1) vs POST (rev 2) must reject negative proof -> null
        var dump = await socket.DumpBssAsync(28, 3, 0x1000UL);
        var snap = LinuxWifiScanCache.EvaluateScanDump(
            dump,
            ifIndex: 3,
            wdev: 0x1000UL,
            completionTracker: tracker,
            currentBootTimeNs: 1000_000_000_000UL,
            preSnapshot: preSnapshot,
            postSnapshot: postSnapshot,
            dumpStartedAtBootTimeNs: 999_000_000_000UL,
            dumpCompletedAtBootTimeNs: 1000_000_000_000UL,
            requestedSsid: "HomeMesh");

        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", 1000_000_000_000UL));
    }

    // 9. Full Production End-to-End Composition Test with Wildcard Domain (TRIGGER_SCAN -> NEW_SCAN_RESULTS)
    [Fact]
    public async Task EventObserver_Full_EndToEnd_Production_Composition_Yields_WifiRadioDown()
    {
        var classifier = new StateClassifier();
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000, CurrentBootTimeNsec = 0 };

        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var socket = new MockNl80211Socket();
        var bssOther = CreateBss("OtherNet", seenMsAgo: 5000);
        socket.BssDump.Add(bssOther);
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        // Before scan event: absent target SSID yields null (not false!)
        var visibleBefore = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.Null(visibleBefore);

        // Step 1: Kernel multicast TRIGGER_SCAN arrives with no frequencies (AllAllowed) and wildcard active probe
        var triggerBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            ifIndex: 3,
            wdev: 0x1000UL,
            frequencies: null, // AllAllowed
            ssids: new byte[][] { Array.Empty<byte>() }); // Wildcard active probe

        clock.CurrentBootTimeSec = 998;
        observer.ProcessEventPayload(triggerBytes, nl80211FamilyId: 28);

        // Step 2: Kernel multicast NEW_SCAN_RESULTS arrives for same adapter
        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL);

        clock.CurrentBootTimeSec = 999;
        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        // Step 3: Now radio evaluates SSID visibility at T=1000s with CompletedScan provenance & stable revision
        clock.CurrentBootTimeSec = 1000;
        var visibleAfter = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.False(visibleAfter); // Evaluates to false!

        // Step 4: Link snapshot with Link=Down and SsidVisibleInScan=false
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

        // Step 5: Classifier correctly attributes to WifiRadioDown
        var verdict = classifier.Classify(cycle);
        Assert.Equal(NetworkState.WifiRadioDown, verdict.State);
    }

    // 10. Explicit target SSID in scan event domain proves absence of target
    [Fact]
    public async Task EventObserver_Explicit_Target_SSID_Domain_Yields_False()
    {
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        // TRIGGER_SCAN with AllAllowed frequencies and explicit SSIDs covering HomeMesh
        var triggerBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            ifIndex: 3,
            wdev: 0x1000UL,
            frequencies: null,
            ssids: new byte[][] { System.Text.Encoding.UTF8.GetBytes("HomeMesh"), System.Text.Encoding.UTF8.GetBytes("OtherNet") });

        clock.CurrentBootTimeSec = 998;
        observer.ProcessEventPayload(triggerBytes, nl80211FamilyId: 28);

        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL);

        clock.CurrentBootTimeSec = 999;
        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        clock.CurrentBootTimeSec = 1000;
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.False(visible);
    }

    // 11. Positive fresh BSS is true regardless of scan domain or observer availability
    [Fact]
    public async Task EventObserver_Positive_Fresh_BSS_Always_Yields_True()
    {
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker(); // No scan completion recorded!

        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("HomeMesh", seenMsAgo: 5000)); // Fresh HomeMesh present
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.True(visible);
    }

    // 12. RequestUrgentScan is strictly a NO-OP (0 TRIGGER_SCAN calls issued)
    [Fact]
    public void EventObserver_RequestUrgentScan_Is_Strict_NoOp()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        using var radio = new LinuxNl80211Radio(scanCompletionTracker: tracker);

        // Calling RequestUrgentScan must not throw, must not block, must not trigger scan
        radio.RequestUrgentScan();
        Assert.Null(tracker.GetLastScanCompletion(3, 0x1000UL));
    }

    // --- Phase 3.1-7C-R3-R1 Acceptance Tests ---

    // R3-R1.1: TRIGGER_SCAN with explicit frequencies=[2412] -> ExplicitSubset -> never false
    [Fact]
    public async Task R3_R1_TriggerScan_Explicit_Frequency_Is_ExplicitSubset_Never_False()
    {
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        // TRIGGER_SCAN with single channel 2412 MHz
        var triggerBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            ifIndex: 3,
            wdev: 0x1000UL,
            frequencies: new uint[] { 2412 },
            ssids: new byte[][] { Array.Empty<byte>() });

        observer.ProcessEventPayload(triggerBytes, nl80211FamilyId: 28);
        var startedRecord = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(startedRecord);
        Assert.Equal(LinuxWifiScanFrequencyScope.ExplicitSubset, startedRecord.Domain?.FrequencyScope);

        // NEW_SCAN_RESULTS arrives
        var eventBytes = BuildScanEventDatagram(familyId: 28, genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS, ifIndex: 3, wdev: 0x1000UL);
        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        var completedRecord = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(completedRecord);
        Assert.Equal(LinuxWifiScanFrequencyScope.ExplicitSubset, completedRecord.Domain?.FrequencyScope);

        // Negative proof for HomeMesh (on 5180 MHz) must be NULL, never false!
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.Null(visible);
    }

    // R3-R1.2: TRIGGER_SCAN with 3 channels [2412, 2437, 2462] -> still ExplicitSubset (never AllAllowed)
    [Fact]
    public void R3_R1_TriggerScan_Multiple_Channels_Is_Still_ExplicitSubset()
    {
        var domain = LinuxNl80211EventObserver.ParseScanDomain(
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            scanFreqsBytes: new byte[] { 8, 0, 1, 0, 0x6C, 0x09, 0, 0, 8, 0, 2, 0, 0x85, 0x09, 0, 0, 8, 0, 3, 0, 0x9E, 0x09, 0, 0 },
            scanSsidsBytes: null);

        Assert.Equal(LinuxWifiScanFrequencyScope.ExplicitSubset, domain.FrequencyScope);
        Assert.NotEqual(LinuxWifiScanFrequencyScope.AllAllowed, domain.FrequencyScope);
    }

    // R3-R1.3: TRIGGER_SCAN without SCAN_FREQUENCIES -> AllAllowed
    [Fact]
    public void R3_R1_TriggerScan_No_ScanFrequencies_Yields_AllAllowed()
    {
        var domain = LinuxNl80211EventObserver.ParseScanDomain(
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            scanFreqsBytes: null,
            scanSsidsBytes: null);

        Assert.Equal(LinuxWifiScanFrequencyScope.AllAllowed, domain.FrequencyScope);
        Assert.Equal(LinuxWifiScanSsidScope.PassiveOnly, domain.SsidScope);
    }

    // R3-R1.4: NEW_SCAN_RESULTS without prior Started -> NOT AllAllowed, absence returns null
    [Fact]
    public async Task R3_R1_NewScanResults_Without_Prior_Started_Yields_Null_Absence()
    {
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        // Direct NEW_SCAN_RESULTS without prior TRIGGER_SCAN
        var eventBytes = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS,
            ifIndex: 3,
            wdev: 0x1000UL,
            frequencies: new uint[] { 2412, 5180 },
            ssids: new byte[][] { Array.Empty<byte>() });

        clock.CurrentBootTimeSec = 999;
        observer.ProcessEventPayload(eventBytes, nl80211FamilyId: 28);

        var record = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(record);
        Assert.Equal(LinuxWifiScanFrequencyScope.ExplicitSubset, record.Domain?.FrequencyScope);

        clock.CurrentBootTimeSec = 1000;
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.Null(visible);
    }

    // R3-R1.5: Unscoped Started (without WDEV) invalidates exact-WDEV Completed record
    [Fact]
    public void R3_R1_Unscoped_Started_Invalidates_Exact_Wdev_Completed_Record()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        var clock = new StubNativeClock { CurrentBootTimeSec = 500 };
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        // 1. Initial exact-WDEV Completed record at revision 1
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 500_000_000_000UL, LinuxWifiScanDomain.AllAllowedWildcard());
        var rec1 = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(rec1);
        Assert.Equal(LinuxWifiScanEventStatus.Completed, rec1.Status);
        Assert.Equal(1L, rec1.Revision);

        // 2. Unscoped TRIGGER_SCAN arrives (missing WDEV)
        var unscopedTrigger = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            ifIndex: 3,
            wdev: null); // missing WDEV!

        observer.ProcessEventPayload(unscopedTrigger, nl80211FamilyId: 28);

        // 3. Exact (3, 0x1000UL) must now be invalidated to Started with revision 2!
        var recAfter = tracker.GetLastScanCompletion(3, 0x1000UL);
        Assert.NotNull(recAfter);
        Assert.Equal(LinuxWifiScanEventStatus.Started, recAfter.Status);
        Assert.Equal(2L, recAfter.Revision);
    }

    // R3-R1.6: Old exact-WDEV Completed + unscoped Started + GET_SCAN target absent -> null, never WifiRadioDown
    [Fact]
    public async Task R3_R1_Unscoped_Started_Prevents_False_Negative_And_WifiRadioDown()
    {
        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker();
        var observer = new LinuxNl80211EventObserver(tracker, clock: clock);

        var socket = new MockNl80211Socket();
        socket.BssDump.Add(CreateBss("OtherNet", seenMsAgo: 5000));
        socket.BssDumpStatus = LinuxNl80211DumpStatus.Complete;

        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };
        using var radio = new LinuxNl80211Radio(socket, rfkill, boundInterfaceId: "wlan0", ownsSocket: null, scanCompletionTracker: tracker, clock: clock);

        // 1. Initial valid completed scan
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 950_000_000_000UL, LinuxWifiScanDomain.AllAllowedWildcard());

        // 2. Unscoped TRIGGER_SCAN arrives at T=999s
        var unscopedTrigger = BuildScanEventDatagram(
            familyId: 28,
            genlCmd: LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN,
            ifIndex: 3,
            wdev: null);
        clock.CurrentBootTimeSec = 999;
        observer.ProcessEventPayload(unscopedTrigger, nl80211FamilyId: 28);

        // 3. Querying SSID visibility at T=1000s must yield NULL (never false)
        clock.CurrentBootTimeSec = 1000;
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");
        Assert.Null(visible);
    }
}
