using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using IEM.Linux.Time;

namespace IEM.Linux.Wifi;

internal enum LinuxWifiScanCompleteness
{
    Complete,
    Partial,
    Unknown
}

internal enum LinuxWifiScanSource
{
    KernelBssCache
}

public enum LinuxWifiScanEvidenceBasis
{
    OpportunisticKernelCache,
    CompletedScan,
    Unknown
}

public enum LinuxWifiScanEventStatus
{
    Completed,
    Aborted
}

public sealed record LinuxWifiScanCompletionRecord(
    int IfIndex,
    ulong? Wdev,
    ulong CompletedAtBootTimeNs,
    LinuxWifiScanEventStatus Status);

public interface ILinuxWifiScanCompletionTracker
{
    void RecordScanEvent(int ifIndex, ulong? wdev, LinuxWifiScanEventStatus status, ulong? bootTimeNs = null);
    LinuxWifiScanCompletionRecord? GetLastScanCompletion(int ifIndex, ulong? wdev);
}

public sealed class LinuxWifiScanCompletionTracker : ILinuxWifiScanCompletionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<(int IfIndex, ulong Wdev), LinuxWifiScanCompletionRecord> _records = new();

    public void RecordScanEvent(int ifIndex, ulong? wdev, LinuxWifiScanEventStatus status, ulong? bootTimeNs = null)
    {
        var bootNs = bootTimeNs ?? LinuxWifiScanCache.TryGetCurrentBootTimeNs() ?? 0UL;
        var key = (ifIndex, wdev ?? 0UL);
        lock (_lock)
        {
            _records[key] = new LinuxWifiScanCompletionRecord(ifIndex, wdev, bootNs, status);
        }
    }

    public LinuxWifiScanCompletionRecord? GetLastScanCompletion(int ifIndex, ulong? wdev)
    {
        var key = (ifIndex, wdev ?? 0UL);
        lock (_lock)
        {
            if (_records.TryGetValue(key, out var record))
            {
                return record;
            }
            if (wdev.HasValue && _records.TryGetValue((ifIndex, 0UL), out var fallback))
            {
                return fallback;
            }
            return null;
        }
    }
}

internal sealed record LinuxWifiScanSnapshot(
    LinuxWifiScanSource Source,
    LinuxWifiScanCompleteness Completeness,
    LinuxWifiScanEvidenceBasis EvidenceBasis,
    TimeSpan? Age,
    IReadOnlyList<LinuxNl80211BssInfo> Bss,
    LinuxNl80211DumpStatus DumpStatus);

/// <summary>
/// Manages cached kernel BSS scan results, freshness evaluation, scan completion provenance,
/// and SSID visibility tri-state truth.
/// Invariants 250, 251, 252, 258.
/// </summary>
internal static class LinuxWifiScanCache
{
    /// <summary>
    /// Maximum age of a scan cache snapshot before it is considered stale (3 minutes).
    /// Matches Windows WlanScanCache parity.
    /// </summary>
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Computes the age of a specific BSS observation.
    /// Freshness priority:
    /// 1. LastSeenBootTimeNs compared against current CLOCK_BOOTTIME (if now >= lastSeen).
    /// 2. SeenMsAgo (from NL80211_BSS_SEEN_MS_AGO).
    /// 3. null (Unknown age).
    /// Invariant: Future boot time (lastSeen > now) falls back to SeenMsAgo / null; never clamped to 0.
    /// </summary>
    public static TimeSpan? ComputeBssAge(LinuxNl80211BssInfo bss, ulong? currentBootTimeNs = null)
    {
        if (bss == null) return null;

        if (bss.LastSeenBootTimeNs.HasValue && currentBootTimeNs.HasValue)
        {
            if (currentBootTimeNs.Value >= bss.LastSeenBootTimeNs.Value)
            {
                var diffNs = currentBootTimeNs.Value - bss.LastSeenBootTimeNs.Value;
                return TimeSpan.FromMilliseconds(diffNs / 1_000_000.0);
            }
            // Anomaly guard: lastSeen in future relative to currentBootTimeNs -> fallback to SeenMsAgo
        }

        if (bss.SeenMsAgo.HasValue)
        {
            return TimeSpan.FromMilliseconds(bss.SeenMsAgo.Value);
        }

        return null;
    }

    /// <summary>
    /// Whether a specific BSS is provably fresh (Age <= MaximumAge).
    /// </summary>
    public static bool IsBssFresh(LinuxNl80211BssInfo bss, ulong? currentBootTimeNs = null)
    {
        var age = ComputeBssAge(bss, currentBootTimeNs);
        return age.HasValue && age.Value <= MaximumAge;
    }

    /// <summary>
    /// Evaluates a raw Netlink BSS dump into a structured scan snapshot with proven completeness,
    /// scan-completion provenance, and freshness.
    /// Invariant 250: NLMSG_DONE transport completeness alone does NOT prove RF scan completeness.
    /// RF scan completeness requires affirmative scan-completion provenance (e.g. NL80211_CMD_NEW_SCAN_RESULTS).
    /// </summary>
    public static LinuxWifiScanSnapshot EvaluateScanDump(
        LinuxNl80211DumpResult<LinuxNl80211BssInfo> dumpResult,
        int? ifIndex = null,
        ulong? wdev = null,
        ILinuxWifiScanCompletionTracker? completionTracker = null,
        ulong? currentBootTimeNs = null)
    {
        if (dumpResult == null)
        {
            return new LinuxWifiScanSnapshot(
                LinuxWifiScanSource.KernelBssCache,
                LinuxWifiScanCompleteness.Unknown,
                LinuxWifiScanEvidenceBasis.Unknown,
                Age: null,
                Bss: Array.Empty<LinuxNl80211BssInfo>(),
                DumpStatus: LinuxNl80211DumpStatus.Unavailable);
        }

        var transportComplete = dumpResult.Status == LinuxNl80211DumpStatus.Complete;

        var completeness = LinuxWifiScanCompleteness.Partial;
        var evidenceBasis = LinuxWifiScanEvidenceBasis.OpportunisticKernelCache;

        if (dumpResult.Status is LinuxNl80211DumpStatus.KernelError or LinuxNl80211DumpStatus.Malformed or LinuxNl80211DumpStatus.Unavailable)
        {
            completeness = LinuxWifiScanCompleteness.Unknown;
            evidenceBasis = LinuxWifiScanEvidenceBasis.Unknown;
        }
        else if (transportComplete && ifIndex.HasValue && completionTracker != null)
        {
            var scanRecord = completionTracker.GetLastScanCompletion(ifIndex.Value, wdev);
            if (scanRecord != null && scanRecord.Status == LinuxWifiScanEventStatus.Completed)
            {
                // Verify scan completion event freshness
                bool scanEventFresh = true;
                if (currentBootTimeNs.HasValue && scanRecord.CompletedAtBootTimeNs > 0)
                {
                    if (currentBootTimeNs.Value >= scanRecord.CompletedAtBootTimeNs)
                    {
                        var scanAgeMs = (currentBootTimeNs.Value - scanRecord.CompletedAtBootTimeNs) / 1_000_000.0;
                        scanEventFresh = scanAgeMs <= MaximumAge.TotalMilliseconds;
                    }
                    else
                    {
                        scanEventFresh = false; // Scan timestamp in future relative to query clock
                    }
                }

                if (scanEventFresh)
                {
                    completeness = LinuxWifiScanCompleteness.Complete;
                    evidenceBasis = LinuxWifiScanEvidenceBasis.CompletedScan;
                }
            }
        }

        TimeSpan? minAge = null;
        if (dumpResult.Items != null && dumpResult.Items.Count > 0)
        {
            foreach (var bss in dumpResult.Items)
            {
                var bssAge = ComputeBssAge(bss, currentBootTimeNs);
                if (bssAge.HasValue)
                {
                    if (!minAge.HasValue || bssAge.Value < minAge.Value)
                    {
                        minAge = bssAge.Value;
                    }
                }
            }
        }

        return new LinuxWifiScanSnapshot(
            LinuxWifiScanSource.KernelBssCache,
            completeness,
            evidenceBasis,
            minAge,
            dumpResult.Items ?? Array.Empty<LinuxNl80211BssInfo>(),
            dumpResult.Status);
    }

    /// <summary>
    /// Evaluates tri-state visibility for the requested SSID from a scan snapshot.
    /// Truth Model (Invariants 250, 258):
    /// - true:  Positive evidence of at least one fresh matching BSS (even in Partial / Opportunistic cache).
    /// - false: Proven absence from a CompletedScan (transport complete, affirmative scan-completion event on this adapter, overall Age &lt;= MaximumAge, zero matching BSS).
    /// - null:  Opportunistic cache absence without scan-completion provenance, stale match, unknown age, incomplete dump without match, or blank SSID.
    /// </summary>
    public static bool? EvaluateSsidVisibility(
        LinuxWifiScanSnapshot snapshot,
        string ssid,
        ulong? currentBootTimeNs = null)
    {
        if (string.IsNullOrWhiteSpace(ssid) || snapshot == null)
        {
            return null;
        }

        bool hasStaleMatch = false;

        if (snapshot.Bss != null && snapshot.Bss.Count > 0)
        {
            foreach (var bss in snapshot.Bss)
            {
                if (string.Equals(bss.DisplaySsid, ssid, StringComparison.OrdinalIgnoreCase))
                {
                    if (IsBssFresh(bss, currentBootTimeNs))
                    {
                        // Positive proof requires only a single fresh observation
                        return true;
                    }
                    hasStaleMatch = true;
                }
            }
        }

        // If a matching BSS entry was seen in the past but is now stale -> indeterminate (null, never false)
        if (hasStaleMatch)
        {
            return null;
        }

        // Proven negative absence strictly requires:
        // 1. EvidenceBasis == CompletedScan (proven affirmative scan completion on this adapter)
        // 2. Completeness == Complete
        // 3. DumpStatus == Complete
        // 4. Either non-empty BSS list with Age <= MaximumAge OR empty BSS list with proven scan completion
        // 5. Zero matching BSS
        if (snapshot.EvidenceBasis == LinuxWifiScanEvidenceBasis.CompletedScan &&
            snapshot.Completeness == LinuxWifiScanCompleteness.Complete &&
            snapshot.DumpStatus == LinuxNl80211DumpStatus.Complete)
        {
            if (snapshot.Bss == null || snapshot.Bss.Count == 0)
            {
                return false;
            }

            if (snapshot.Age.HasValue && snapshot.Age.Value <= MaximumAge)
            {
                return false;
            }
        }

        // Opportunistic cache without affirmative scan-completion event cannot prove absence
        return null;
    }

    /// <summary>
    /// Attempts to read current CLOCK_BOOTTIME in nanoseconds via native libc.
    /// Returns null if not on Linux or if clock query fails.
    /// </summary>
    internal static ulong? TryGetCurrentBootTimeNs(ILinuxNativeClock? clock = null)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || clock != null)
            {
                var nativeClock = clock ?? new LinuxNativeClock();
                nativeClock.GetTime(LinuxNativeClock.CLOCK_BOOTTIME, out var ts);
                if (ts.TvSec >= 0 && ts.TvNsec >= 0 && ts.TvNsec < 1_000_000_000)
                {
                    return ((ulong)ts.TvSec * 1_000_000_000UL) + (ulong)ts.TvNsec;
                }
            }
        }
        catch
        {
            // Best effort fallback to SeenMsAgo
        }
        return null;
    }
}
