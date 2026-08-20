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

internal sealed record LinuxWifiScanSnapshot(
    LinuxWifiScanSource Source,
    LinuxWifiScanCompleteness Completeness,
    TimeSpan? Age,
    IReadOnlyList<LinuxNl80211BssInfo> Bss,
    LinuxNl80211DumpStatus DumpStatus);

/// <summary>
/// Manages cached kernel BSS scan results, freshness evaluation, and SSID visibility tri-state truth.
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
    /// Evaluates a raw Netlink BSS dump into a structured scan snapshot with proven completeness and freshness.
    /// </summary>
    public static LinuxWifiScanSnapshot EvaluateScanDump(
        LinuxNl80211DumpResult<LinuxNl80211BssInfo> dumpResult,
        ulong? currentBootTimeNs = null)
    {
        if (dumpResult == null)
        {
            return new LinuxWifiScanSnapshot(
                LinuxWifiScanSource.KernelBssCache,
                LinuxWifiScanCompleteness.Unknown,
                Age: null,
                Bss: Array.Empty<LinuxNl80211BssInfo>(),
                DumpStatus: LinuxNl80211DumpStatus.Unavailable);
        }

        var completeness = dumpResult.Status switch
        {
            LinuxNl80211DumpStatus.Complete => LinuxWifiScanCompleteness.Complete,
            LinuxNl80211DumpStatus.Interrupted or LinuxNl80211DumpStatus.Incomplete => LinuxWifiScanCompleteness.Partial,
            _ => LinuxWifiScanCompleteness.Unknown
        };

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
            minAge,
            dumpResult.Items ?? Array.Empty<LinuxNl80211BssInfo>(),
            dumpResult.Status);
    }

    /// <summary>
    /// Evaluates tri-state visibility for the requested SSID from a scan snapshot.
    /// Truth Model (Invariant 250):
    /// - true:  Positive evidence of at least one fresh matching BSS (even in Partial dump).
    /// - false: Proven absence (Complete dump, non-empty, overall Age <= MaximumAge, zero matching BSS).
    /// - null:  Stale match, unknown age, incomplete dump without match, empty dump, or blank SSID.
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
                        // Fresh positive proof
                        return true;
                    }
                    hasStaleMatch = true;
                }
            }
        }

        // If a matching BSS entry was seen in the past but is now stale -> indeterminate (null)
        if (hasStaleMatch)
        {
            return null;
        }

        // Proven absence requires: Complete dump + non-empty + overall snapshot Age <= MaximumAge + zero matching BSS
        if (snapshot.Completeness == LinuxWifiScanCompleteness.Complete &&
            snapshot.Bss != null &&
            snapshot.Bss.Count > 0 &&
            snapshot.Age.HasValue &&
            snapshot.Age.Value <= MaximumAge)
        {
            return false;
        }

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
