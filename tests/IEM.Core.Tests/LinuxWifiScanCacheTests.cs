using System;
using System.Collections.Generic;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxWifiScanCacheTests
{
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

    [Theory]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(179999, true)]
    [InlineData(180000, true)] // 3 min exact boundary -> fresh
    [InlineData(180001, false)] // > 3 min -> stale
    [InlineData(300000, false)]
    public void ComputeBssAge_SeenMsAgo_Boundary_Evaluation(uint seenMsAgo, bool expectedFresh)
    {
        var bss = CreateBss(seenMsAgo: seenMsAgo);
        var age = LinuxWifiScanCache.ComputeBssAge(bss);
        var isFresh = LinuxWifiScanCache.IsBssFresh(bss);

        Assert.NotNull(age);
        Assert.Equal(TimeSpan.FromMilliseconds(seenMsAgo), age.Value);
        Assert.Equal(expectedFresh, isFresh);
    }

    [Fact]
    public void ComputeBssAge_LastSeenBootTimeNs_Precedence_And_Boundary()
    {
        const ulong nowBootNs = 1_000_000_000_000UL; // 1000s in ns

        // 1. Exact zero age
        var bss0 = CreateBss(seenMsAgo: 5000, lastSeenBootTimeNs: nowBootNs);
        var age0 = LinuxWifiScanCache.ComputeBssAge(bss0, nowBootNs);
        Assert.Equal(TimeSpan.Zero, age0);
        Assert.True(LinuxWifiScanCache.IsBssFresh(bss0, nowBootNs));

        // 2. Exactly 180s ago (180,000,000,000 ns)
        ulong bss180s = nowBootNs - (180UL * 1_000_000_000UL);
        var bssFresh = CreateBss(seenMsAgo: 5000, lastSeenBootTimeNs: bss180s);
        var ageFresh = LinuxWifiScanCache.ComputeBssAge(bssFresh, nowBootNs);
        Assert.Equal(TimeSpan.FromMinutes(3), ageFresh);
        Assert.True(LinuxWifiScanCache.IsBssFresh(bssFresh, nowBootNs));

        // 3. Exactly 180s + 1ms ago
        ulong bss180s1ms = nowBootNs - (180_001UL * 1_000_000UL);
        var bssStale = CreateBss(seenMsAgo: 5000, lastSeenBootTimeNs: bss180s1ms);
        var ageStale = LinuxWifiScanCache.ComputeBssAge(bssStale, nowBootNs);
        Assert.Equal(TimeSpan.FromMilliseconds(180001), ageStale);
        Assert.False(LinuxWifiScanCache.IsBssFresh(bssStale, nowBootNs));

        // 4. Future boot time anomaly -> fallback to SeenMsAgo
        ulong bssFuture = nowBootNs + 1_000_000_000UL;
        var bssAnomaly = CreateBss(seenMsAgo: 2000, lastSeenBootTimeNs: bssFuture);
        var ageAnomaly = LinuxWifiScanCache.ComputeBssAge(bssAnomaly, nowBootNs);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), ageAnomaly); // Falls back to SeenMsAgo
    }

    [Fact]
    public void EvaluateScanDump_Completeness_And_ScanCompletionProvenance()
    {
        var bss1 = CreateBss(ssid: "Net1", seenMsAgo: 50000);
        var bss2 = CreateBss(ssid: "Net2", seenMsAgo: 10000); // freshest
        var bss3 = CreateBss(ssid: "Net3", seenMsAgo: 120000);

        var tracker = new LinuxWifiScanCompletionTracker();
        const ulong nowBootNs = 1_000_000_000_000UL;

        // 1. Complete transport dump WITHOUT scan-completion provenance -> Partial / OpportunisticKernelCache
        var dumpTransportComplete = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(
            new[] { bss1, bss2, bss3 },
            LinuxNl80211DumpStatus.Complete);

        var snapOpportunistic = LinuxWifiScanCache.EvaluateScanDump(dumpTransportComplete, ifIndex: 3, wdev: 0x1000UL, completionTracker: null, currentBootTimeNs: nowBootNs);
        Assert.Equal(LinuxWifiScanCompleteness.Partial, snapOpportunistic.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.OpportunisticKernelCache, snapOpportunistic.EvidenceBasis);
        Assert.Equal(TimeSpan.FromMilliseconds(10000), snapOpportunistic.Age);

        // 2. Complete transport dump WITH affirmative scan-completion event on this adapter -> Complete / CompletedScan
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, nowBootNs - (5_000UL * 1_000_000UL)); // 5s ago
        var snapCompletedScan = LinuxWifiScanCache.EvaluateScanDump(dumpTransportComplete, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);
        Assert.Equal(LinuxWifiScanCompleteness.Complete, snapCompletedScan.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.CompletedScan, snapCompletedScan.EvidenceBasis);

        // 3. Interrupted dump -> Partial / OpportunisticKernelCache
        var dumpInterrupted = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(
            new[] { bss1 },
            LinuxNl80211DumpStatus.Interrupted);

        var snapInterrupted = LinuxWifiScanCache.EvaluateScanDump(dumpInterrupted, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);
        Assert.Equal(LinuxWifiScanCompleteness.Partial, snapInterrupted.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.OpportunisticKernelCache, snapInterrupted.EvidenceBasis);

        // 4. KernelError dump -> Unknown / Unknown
        var dumpError = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(
            Array.Empty<LinuxNl80211BssInfo>(),
            LinuxNl80211DumpStatus.KernelError,
            ErrorCode: -19);

        var snapError = LinuxWifiScanCache.EvaluateScanDump(dumpError, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);
        Assert.Equal(LinuxWifiScanCompleteness.Unknown, snapError.Completeness);
        Assert.Equal(LinuxWifiScanEvidenceBasis.Unknown, snapError.EvidenceBasis);
    }

    [Fact]
    public void EvaluateSsidVisibility_Fresh_Positive_Works_On_Opportunistic_Cache()
    {
        // Fresh matching SSID in opportunistic cache (no scan-completion event) -> true!
        var bss = CreateBss(ssid: "HomeMesh", seenMsAgo: 2000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: null);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "homemesh"); // case-insensitive
        Assert.True(visible);
    }

    [Fact]
    public void EvaluateSsidVisibility_Opportunistic_Cache_Absence_Returns_Null_Never_False()
    {
        // Transport complete, fresh other network, but NO scan-completion event -> null (not false!)
        var bss = CreateBss(ssid: "OtherNet", seenMsAgo: 2000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: null);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh");
        Assert.Null(visible); // Invariant 250: Opportunistic cache absence is unknown (null)
    }

    [Fact]
    public void EvaluateSsidVisibility_CompletedScan_Absence_Returns_False()
    {
        // Transport complete AND affirmative CompletedScan provenance -> false
        var tracker = new LinuxWifiScanCompletionTracker();
        const ulong nowBootNs = 1_000_000_000_000UL;
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, nowBootNs - (2_000UL * 1_000_000UL));

        var bss = CreateBss(ssid: "OtherNet", seenMsAgo: 2000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", nowBootNs);
        Assert.False(visible); // Proven absence under completed scan
    }

    [Fact]
    public void EvaluateSsidVisibility_Different_Adapter_Scan_Event_DoesNotProve_Absence()
    {
        // Scan completed on wlan1 (ifindex 4), but query is on wlan0 (ifindex 3)
        var tracker = new LinuxWifiScanCompletionTracker();
        const ulong nowBootNs = 1_000_000_000_000UL;
        tracker.RecordScanEvent(4, 0x2000UL, LinuxWifiScanEventStatus.Completed, nowBootNs); // wlan1

        var bss = CreateBss(ssid: "OtherNet", seenMsAgo: 2000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", nowBootNs);
        Assert.Null(visible); // Adapter-scoped: wlan1 event cannot prove absence on wlan0
    }

    [Fact]
    public void EvaluateSsidVisibility_Scan_Aborted_Yields_Null()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        const ulong nowBootNs = 1_000_000_000_000UL;
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Aborted, nowBootNs);

        var bss = CreateBss(ssid: "OtherNet", seenMsAgo: 2000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", nowBootNs);
        Assert.Null(visible); // Aborted scan cannot prove absence
    }

    [Fact]
    public void EvaluateSsidVisibility_Stale_ScanCompletion_Event_Yields_Null()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        const ulong nowBootNs = 1_000_000_000_000UL;
        // Scan completed 4 minutes ago (> 3 min)
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, nowBootNs - (240_000UL * 1_000_000UL));

        var bss = CreateBss(ssid: "OtherNet", seenMsAgo: 2000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", nowBootNs);
        Assert.Null(visible); // Stale scan event cannot prove absence
    }

    [Fact]
    public void EvaluateSsidVisibility_Stale_Matching_Ssid_Returns_Null_Never_False()
    {
        // Target SSID is in the cache, but is 4 minutes old (> 3 min)
        var bss = CreateBss(ssid: "HomeMesh", seenMsAgo: 240000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh");
        Assert.Null(visible); // Indeterminate, NEVER false!
    }

    [Fact]
    public void EvaluateSsidVisibility_Empty_BSS_With_CompletedScan_Returns_False()
    {
        var tracker = new LinuxWifiScanCompletionTracker();
        const ulong nowBootNs = 1_000_000_000_000UL;
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, nowBootNs - (2_000UL * 1_000_000UL));

        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: nowBootNs);

        var visible = LinuxWifiScanCache.EvaluateSsidVisibility(snap, "HomeMesh", nowBootNs);
        Assert.False(visible); // Empty airwaves under proven completed scan
    }

    [Fact]
    public void EvaluateSsidVisibility_Blank_Or_Whitespace_Ssid_Returns_Null()
    {
        var bss = CreateBss(ssid: "HomeMesh", seenMsAgo: 2000);
        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL);

        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, ""));
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "   "));
        Assert.Null(LinuxWifiScanCache.EvaluateSsidVisibility(snap, null!));
    }

    [Fact]
    public void EvaluateSsidVisibility_Hidden_ZeroLength_Ssid_DoesNotMatch_RegularSsid()
    {
        var hiddenBss = new LinuxNl80211BssInfo(
            3,
            new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x99 },
            "00:11:22:33:44:99",
            Array.Empty<byte>(),
            "",
            5180,
            0,
            -7000,
            70,
            1000,
            null, null, null, null, null, 0x1000UL, 1);

        var tracker = new LinuxWifiScanCompletionTracker();
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 1_000_000_000_000UL);

        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { hiddenBss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 1_000_000_000_000UL);

        Assert.False(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "MyWiFi", 1_000_000_000_000UL));
    }

    [Fact]
    public void EvaluateSsidVisibility_Duplicate_Same_Ssid_Multiple_Bssids_Mesh()
    {
        var ap1 = CreateBss(ssid: "MeshHome", seenMsAgo: 1000, bssid: "00:11:22:33:44:01", freq: 2412);
        var ap2 = CreateBss(ssid: "MeshHome", seenMsAgo: 2000, bssid: "00:11:22:33:44:02", freq: 5180);
        var ap3 = CreateBss(ssid: "MeshHome", seenMsAgo: 3000, bssid: "00:11:22:33:44:03", freq: 5975);

        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { ap1, ap2, ap3 }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL);

        Assert.True(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "MeshHome"));
    }

    [Fact]
    public void EvaluateSsidVisibility_Mixed_Fresh_And_Stale_Same_Ssid_Returns_True_If_Any_Fresh()
    {
        var apStale = CreateBss(ssid: "OfficeMesh", seenMsAgo: 240000, bssid: "00:11:22:33:44:01", freq: 2412);
        var apFresh = CreateBss(ssid: "OfficeMesh", seenMsAgo: 10000, bssid: "00:11:22:33:44:02", freq: 5180);

        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { apStale, apFresh }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL);

        Assert.True(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "OfficeMesh"));
    }

    [Fact]
    public void EvaluateSsidVisibility_Malformed_NonUtf8_Ssid_DoesNotThrow_And_Matches_DisplaySsid()
    {
        byte[] rawMalformed = new byte[] { 0xFF, 0xFE, 0x41, 0x42 };
        string display = System.Text.Encoding.UTF8.GetString(rawMalformed); // contains \uFFFD

        var bss = new LinuxNl80211BssInfo(
            3,
            new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x88 },
            "00:11:22:33:44:88",
            rawMalformed,
            display,
            5180,
            0,
            -6000,
            85,
            1000,
            null, null, null, null, null, 0x1000UL, 1);

        var tracker = new LinuxWifiScanCompletionTracker();
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 1_000_000_000_000UL);

        var dump = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);
        var snap = LinuxWifiScanCache.EvaluateScanDump(dump, ifIndex: 3, wdev: 0x1000UL, completionTracker: tracker, currentBootTimeNs: 1_000_000_000_000UL);

        Assert.True(LinuxWifiScanCache.EvaluateSsidVisibility(snap, display, 1_000_000_000_000UL));
        Assert.False(LinuxWifiScanCache.EvaluateSsidVisibility(snap, "NormalSsid", 1_000_000_000_000UL));
    }
}
