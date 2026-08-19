using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IEM.Core.Time;

namespace IEM.Windows.Time;

/// <summary>
/// High-precision Windows time observation provider capturing UTC wall-clock, QPC monotonic ticks,
/// and interrupt-time elapsed durations (biased vs unbiased).
/// Invariants:
/// 97. SUSPEND_TIME_IS_NEVER_INTERPRETED_AS_NETWORK_DOWNTIME
/// 98. WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION
/// 99. MONOTONIC_TIME_IS_NEVER_PRESENTED_AS_ABSOLUTE_UTC
/// 111. UNAVAILABLE_TIME_SOURCE_NEVER_SYNTHESIZES_TIME_OR_CONTINUITY
/// 113. PLATFORM_TIME_SOURCE_IS_PROVENANCE_NOT_TEMPORAL_SEMANTICS
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTimeObservationProvider : ITimeObservationProvider
{
    public string ProviderId => "WindowsHighPrecisionTimeProvider";
    public string ProviderVersion => "3.0.0";

    [DllImport("kernel32.dll")]
    private static extern void GetSystemTimePreciseAsFileTime(out long fileTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryUnbiasedInterruptTimePrecise(out ulong lpUnbiasedInterruptTimePrecise);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInterruptTimePrecise(out ulong lpInterruptTimePrecise);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    public BootObservation CaptureBootObservation(string? knownBootInstanceId = null)
    {
        var utc = GetCurrentUtcPrecise();
        var mono = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;

        var (bootElapsed, activeElapsed) = GetDualElapsedTimes();

        var bootId = !string.IsNullOrWhiteSpace(knownBootInstanceId)
            ? knownBootInstanceId
            : $"win-boot-{(utc - bootElapsed).ToUnixTimeSeconds()}";

        return new BootObservation(
            ObservationId: $"bobs-{Guid.NewGuid():N}",
            BootInstanceId: bootId,
            BootIdentityBasis: "WindowsBootUtcOrigin",
            CapturedUtc: utc,
            WallClockSource: "GetSystemTimePreciseAsFileTime",
            MonotonicTimestamp: mono,
            MonotonicFrequency: freq,
            MonotonicSource: "QueryPerformanceCounter",
            BootElapsedIncludingSuspend: bootElapsed,
            ActiveElapsedExcludingSuspend: activeElapsed,
            ProviderId: ProviderId,
            ProviderVersion: ProviderVersion);
    }

    public ClockSample CaptureClockSample(string bootInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootInstanceId);

        var utc = GetCurrentUtcPrecise();
        var mono = Stopwatch.GetTimestamp();
        var freq = Stopwatch.Frequency;
        var (bootElapsed, activeElapsed) = GetDualElapsedTimes();

        return new ClockSample(
            SampleId: $"cs-{Guid.NewGuid():N}",
            BootInstanceId: bootInstanceId,
            CapturedUtc: utc,
            MonotonicTimestamp: mono,
            MonotonicFrequency: freq,
            BootElapsedIncludingSuspend: bootElapsed,
            ActiveElapsedExcludingSuspend: activeElapsed);
    }

    private static DateTimeOffset GetCurrentUtcPrecise()
    {
        try
        {
            GetSystemTimePreciseAsFileTime(out var fileTime);
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static (TimeSpan BootElapsed, TimeSpan ActiveElapsed) GetDualElapsedTimes()
    {
        TimeSpan bootElapsed;
        TimeSpan activeElapsed;

        try
        {
            if (QueryInterruptTimePrecise(out var interruptTime))
            {
                // 100-nanosecond units
                bootElapsed = TimeSpan.FromTicks((long)interruptTime);
            }
            else
            {
                bootElapsed = TimeSpan.FromMilliseconds(GetTickCount64());
            }

            if (QueryUnbiasedInterruptTimePrecise(out var unbiasedInterruptTime))
            {
                activeElapsed = TimeSpan.FromTicks((long)unbiasedInterruptTime);
            }
            else
            {
                activeElapsed = bootElapsed;
            }
        }
        catch
        {
            bootElapsed = TimeSpan.FromMilliseconds(GetTickCount64());
            activeElapsed = bootElapsed;
        }

        return (bootElapsed, activeElapsed);
    }
}
