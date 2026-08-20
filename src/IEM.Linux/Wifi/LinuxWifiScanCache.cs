using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using IEM.Linux.Time;

namespace IEM.Linux.Wifi;

public enum LinuxWifiScanCompleteness
{
    Complete,
    Partial,
    Unknown
}

public enum LinuxWifiScanSource
{
    KernelBssCache
}

public enum LinuxWifiScanEvidenceBasis
{
    OpportunisticKernelCache,
    CompletedScan,
    Unknown
}

public enum LinuxWifiScanFrequencyScope
{
    Unknown,
    AllAllowed,
    ExplicitSubset
}

public enum LinuxWifiScanSsidScope
{
    Unknown,
    PassiveOnly,
    WildcardActive,
    ExplicitSsids
}

public sealed record LinuxWifiScanDomain(
    LinuxWifiScanFrequencyScope FrequencyScope,
    LinuxWifiScanSsidScope SsidScope,
    IReadOnlyList<uint> FrequenciesMhz,
    IReadOnlyList<byte[]> Ssids)
{
    public static readonly LinuxWifiScanDomain Unknown = new(
        LinuxWifiScanFrequencyScope.Unknown,
        LinuxWifiScanSsidScope.Unknown,
        Array.Empty<uint>(),
        Array.Empty<byte[]>());

    public static LinuxWifiScanDomain AllAllowedWildcard() => new(
        LinuxWifiScanFrequencyScope.AllAllowed,
        LinuxWifiScanSsidScope.WildcardActive,
        Array.Empty<uint>(),
        new[] { Array.Empty<byte>() });

    /// <summary>
    /// Checks if this observation domain provides affirmative proof that the requested SSID
    /// would have been observed if it were actively in the air.
    /// Invariant: FrequencyScope must be AllAllowed AND (SsidScope is WildcardActive OR ExplicitSsids contains exact requested bytes).
    /// </summary>
    public bool CoversSsidObservation(byte[] requestedSsidBytes)
    {
        if (FrequencyScope != LinuxWifiScanFrequencyScope.AllAllowed)
        {
            return false;
        }

        if (SsidScope == LinuxWifiScanSsidScope.WildcardActive)
        {
            return true;
        }

        if (SsidScope == LinuxWifiScanSsidScope.ExplicitSsids && Ssids != null && requestedSsidBytes != null)
        {
            foreach (var ssid in Ssids)
            {
                if (ssid != null && ssid.AsSpan().SequenceEqual(requestedSsidBytes))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

public enum LinuxWifiScanEventStatus
{
    Started,
    Completed,
    Aborted,
    ScheduledStarted,
    ScheduledResults,
    ScheduledStopped,
    Unknown
}

public sealed record LinuxWifiScanCompletionRecord(
    int IfIndex,
    ulong? Wdev,
    ulong? ObservedAtBootTimeNs,
    LinuxWifiScanEventStatus Status,
    long Revision,
    LinuxWifiScanDomain? Domain);

public sealed record LinuxWifiScanTrackerSnapshot(
    long Revision,
    int IfIndex,
    ulong? Wdev,
    LinuxWifiScanEventStatus Status,
    ulong? ObservedAtBootTimeNs,
    LinuxWifiScanDomain? Domain);

public interface ILinuxWifiScanCompletionTracker
{
    void RecordScanEvent(
        int ifIndex,
        ulong? wdev,
        LinuxWifiScanEventStatus status,
        ulong? bootTimeNs = null,
        LinuxWifiScanDomain? domain = null);

    LinuxWifiScanCompletionRecord? GetLastScanCompletion(int ifIndex, ulong? wdev);
    LinuxWifiScanTrackerSnapshot? GetSnapshot(int ifIndex, ulong? wdev);
}

public sealed class LinuxWifiScanCompletionTracker : ILinuxWifiScanCompletionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<(int IfIndex, ulong Wdev), LinuxWifiScanCompletionRecord> _records = new();
    private long _revisionCounter = 0;

    public void RecordScanEvent(
        int ifIndex,
        ulong? wdev,
        LinuxWifiScanEventStatus status,
        ulong? bootTimeNs = null,
        LinuxWifiScanDomain? domain = null)
    {
        var bootNs = bootTimeNs ?? LinuxWifiScanCache.TryGetCurrentBootTimeNs();
        lock (_lock)
        {
            _revisionCounter++;
            var rev = _revisionCounter;
            var effectiveDomain = domain ?? LinuxWifiScanDomain.Unknown;

            if (!wdev.HasValue && status is LinuxWifiScanEventStatus.Started
                                      or LinuxWifiScanEventStatus.Aborted
                                      or LinuxWifiScanEventStatus.ScheduledStarted
                                      or LinuxWifiScanEventStatus.ScheduledResults
                                      or LinuxWifiScanEventStatus.ScheduledStopped)
            {
                // Invalidate all records for this ifindex on generic abort, unscoped start, or sched event without WDEV
                var matchingKeys = _records.Keys.Where(k => k.IfIndex == ifIndex).ToList();
                foreach (var k in matchingKeys)
                {
                    _records[k] = new LinuxWifiScanCompletionRecord(ifIndex, k.Wdev == 0UL ? null : k.Wdev, bootNs, status, rev, effectiveDomain);
                }
                _records[(ifIndex, 0UL)] = new LinuxWifiScanCompletionRecord(ifIndex, null, bootNs, status, rev, effectiveDomain);
            }
            else
            {
                var key = (ifIndex, wdev ?? 0UL);
                _records[key] = new LinuxWifiScanCompletionRecord(ifIndex, wdev, bootNs, status, rev, effectiveDomain);
            }
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
            // Strict scoping: never fallback from known WDEV to 0UL for negative-proof completion
            return null;
        }
    }

    public LinuxWifiScanTrackerSnapshot? GetSnapshot(int ifIndex, ulong? wdev)
    {
        var key = (ifIndex, wdev ?? 0UL);
        lock (_lock)
        {
            if (_records.TryGetValue(key, out var record))
            {
                return new LinuxWifiScanTrackerSnapshot(
                    record.Revision,
                    record.IfIndex,
                    record.Wdev,
                    record.Status,
                    record.ObservedAtBootTimeNs,
                    record.Domain);
            }
            return null;
        }
    }
}

public sealed record LinuxWifiScanSnapshot(
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
public static class LinuxWifiScanCache
{
    /// <summary>
    /// Maximum age of a scan cache snapshot before it is considered stale (3 minutes).
    /// Matches Windows WlanScanCache parity.
    /// </summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(3);

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
    /// RF scan completeness requires affirmative scan-completion provenance (e.g. NL80211_CMD_NEW_SCAN_RESULTS)
    /// with known, proven freshness, causal precedence, revision stability, exact adapter/WDEV identity match,
    /// and proven frequency/SSID observation domain coverage.
    /// </summary>
    public static LinuxWifiScanSnapshot EvaluateScanDump(
        LinuxNl80211DumpResult<LinuxNl80211BssInfo> dumpResult,
        int? ifIndex = null,
        ulong? wdev = null,
        ILinuxWifiScanCompletionTracker? completionTracker = null,
        ulong? currentBootTimeNs = null,
        LinuxWifiScanTrackerSnapshot? preSnapshot = null,
        LinuxWifiScanTrackerSnapshot? postSnapshot = null,
        ulong? dumpStartedAtBootTimeNs = null,
        ulong? dumpCompletedAtBootTimeNs = null,
        string? requestedSsid = null)
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
        else if (transportComplete && ifIndex.HasValue)
        {
            if (preSnapshot != null && postSnapshot != null)
            {
                // Causal Negative Proof Evaluation:
                // 1. Revision stable across dump (no events arrived during GET_SCAN)
                // 2. Both PRE and POST status == Completed
                // 3. Exact IFINDEX and exact WDEV match
                // 4. Timestamps known: completion <= dumpStartedAt, and dumpCompletedAt - completion <= MaximumAge
                // 5. Domain covers requested SSID
                bool revisionStable = preSnapshot.Revision == postSnapshot.Revision;
                bool statusCompleted = preSnapshot.Status == LinuxWifiScanEventStatus.Completed &&
                                       postSnapshot.Status == LinuxWifiScanEventStatus.Completed;
                bool ifIndexMatch = preSnapshot.IfIndex == ifIndex.Value;
                bool wdevMatch = (!wdev.HasValue && !preSnapshot.Wdev.HasValue) ||
                                 (wdev.HasValue && preSnapshot.Wdev.HasValue && wdev.Value == preSnapshot.Wdev.Value);

                if (revisionStable && statusCompleted && ifIndexMatch && wdevMatch &&
                    preSnapshot.ObservedAtBootTimeNs.HasValue &&
                    dumpStartedAtBootTimeNs.HasValue &&
                    dumpCompletedAtBootTimeNs.HasValue &&
                    preSnapshot.ObservedAtBootTimeNs.Value <= dumpStartedAtBootTimeNs.Value &&
                    dumpCompletedAtBootTimeNs.Value >= preSnapshot.ObservedAtBootTimeNs.Value)
                {
                    var scanAgeMs = (dumpCompletedAtBootTimeNs.Value - preSnapshot.ObservedAtBootTimeNs.Value) / 1_000_000.0;
                    if (scanAgeMs <= MaximumAge.TotalMilliseconds)
                    {
                        byte[]? reqBytes = requestedSsid != null ? System.Text.Encoding.UTF8.GetBytes(requestedSsid) : null;
                        bool domainCovered = preSnapshot.Domain != null && (reqBytes == null || preSnapshot.Domain.CoversSsidObservation(reqBytes));

                        if (domainCovered)
                        {
                            completeness = LinuxWifiScanCompleteness.Complete;
                            evidenceBasis = LinuxWifiScanEvidenceBasis.CompletedScan;
                        }
                    }
                }
            }
            else if (completionTracker != null)
            {
                var scanRecord = completionTracker.GetLastScanCompletion(ifIndex.Value, wdev);
                if (scanRecord != null &&
                    scanRecord.Status == LinuxWifiScanEventStatus.Completed &&
                    scanRecord.ObservedAtBootTimeNs.HasValue &&
                    currentBootTimeNs.HasValue &&
                    currentBootTimeNs.Value >= scanRecord.ObservedAtBootTimeNs.Value)
                {
                    bool wdevMatch = (!wdev.HasValue && !scanRecord.Wdev.HasValue) ||
                                     (wdev.HasValue && scanRecord.Wdev.HasValue && wdev.Value == scanRecord.Wdev.Value);

                    if (wdevMatch)
                    {
                        var scanAgeMs = (currentBootTimeNs.Value - scanRecord.ObservedAtBootTimeNs.Value) / 1_000_000.0;
                        if (scanAgeMs <= MaximumAge.TotalMilliseconds)
                        {
                            byte[]? reqBytes = requestedSsid != null ? System.Text.Encoding.UTF8.GetBytes(requestedSsid) : null;
                            bool domainCovered = scanRecord.Domain != null && (reqBytes == null || scanRecord.Domain.CoversSsidObservation(reqBytes));

                            if (domainCovered)
                            {
                                completeness = LinuxWifiScanCompleteness.Complete;
                                evidenceBasis = LinuxWifiScanEvidenceBasis.CompletedScan;
                            }
                        }
                    }
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
