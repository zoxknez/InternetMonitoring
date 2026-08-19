using System;
using IEM.Core.Time;

namespace IEM.Linux.Time;

/// <summary>
/// High-precision Linux time observation provider capturing UTC wall-clock (CLOCK_REALTIME),
/// nanosecond monotonic ticks (CLOCK_MONOTONIC), and boot-elapsed ticks including suspend (CLOCK_BOOTTIME).
/// 
/// Invariants:
/// 97. SUSPEND_TIME_IS_NEVER_INTERPRETED_AS_NETWORK_DOWNTIME
/// 98. WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION
/// 99. MONOTONIC_TIME_IS_NEVER_PRESENTED_AS_ABSOLUTE_UTC
/// 103. CLOCK_DISCONTINUITY_REQUIRES_COMPARISON_WITH_AN_INDEPENDENT_ELAPSED_TIME_SOURCE
/// 108. HOST_SUSPENSION_GAP_NEVER_CONTRIBUTES_NETWORK_OUTAGE_DURATION
/// 111. UNAVAILABLE_TIME_SOURCE_NEVER_SYNTHESIZES_TIME_OR_CONTINUITY
/// 113. PLATFORM_TIME_SOURCE_IS_PROVENANCE_NOT_TEMPORAL_SEMANTICS
/// </summary>
public sealed class LinuxTimeObservationProvider : ITimeObservationProvider
{
    public string ProviderId => "LinuxHighPrecisionTimeProvider";
    public string ProviderVersion => "3.1.0";

    public const long LinuxMonotonicFrequency = 1_000_000_000L; // Nanoseconds per second

    private readonly ILinuxNativeClock _clock;
    private readonly Func<string, string>? _fileReader;

    public LinuxTimeObservationProvider() : this(new LinuxNativeClock(), null)
    {
    }

    internal LinuxTimeObservationProvider(ILinuxNativeClock clock, Func<string, string>? fileReader = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _fileReader = fileReader;
    }

    public ClockSample CaptureClockSample(string bootInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootInstanceId);

        var (utc, monoNanos, activeElapsed, bootElapsed) = SampleClocks();

        return new ClockSample(
            SampleId: $"cs-{Guid.NewGuid():N}",
            BootInstanceId: bootInstanceId,
            CapturedUtc: utc,
            MonotonicTimestamp: monoNanos,
            MonotonicFrequency: LinuxMonotonicFrequency,
            BootElapsedIncludingSuspend: bootElapsed,
            ActiveElapsedExcludingSuspend: activeElapsed);
    }

    public BootObservation CaptureBootObservation(string? knownBootInstanceId = null)
    {
        string bootInstanceId;
        string bootIdentityBasis;

        if (!string.IsNullOrWhiteSpace(knownBootInstanceId))
        {
            bootInstanceId = knownBootInstanceId;
            bootIdentityBasis = "KnownBootInstanceId";
        }
        else
        {
            if (!LinuxBootFacts.TryReadBootId(out var resolvedBootId, out var reasonCode, _fileReader) || resolvedBootId is null)
            {
                throw new InvalidOperationException($"Linux kernel boot_id is unavailable ({reasonCode}). Invariant 111: unavailable time source never synthesizes identity.");
            }

            bootInstanceId = resolvedBootId;
            bootIdentityBasis = "LinuxKernelRandomBootId";
        }

        var (utc, monoNanos, activeElapsed, bootElapsed) = SampleClocks();

        return new BootObservation(
            ObservationId: $"bobs-{Guid.NewGuid():N}",
            BootInstanceId: bootInstanceId,
            BootIdentityBasis: bootIdentityBasis,
            CapturedUtc: utc,
            WallClockSource: "clock_gettime(CLOCK_REALTIME)",
            MonotonicTimestamp: monoNanos,
            MonotonicFrequency: LinuxMonotonicFrequency,
            MonotonicSource: "clock_gettime(CLOCK_MONOTONIC)",
            BootElapsedIncludingSuspend: bootElapsed,
            ActiveElapsedExcludingSuspend: activeElapsed,
            ProviderId: ProviderId,
            ProviderVersion: ProviderVersion);
    }

    private (DateTimeOffset Utc, long MonotonicNanos, TimeSpan ActiveElapsed, TimeSpan BootElapsed) SampleClocks()
    {
        // Deterministic sampling order: REALTIME -> MONOTONIC -> BOOTTIME
        _clock.GetTime(LinuxNativeClock.CLOCK_REALTIME, out var realTime);
        _clock.GetTime(LinuxNativeClock.CLOCK_MONOTONIC, out var monoTime);
        _clock.GetTime(LinuxNativeClock.CLOCK_BOOTTIME, out var bootTime);

        // 1 tick = 100 ns -> TvNsec / 100
        var utc = DateTimeOffset.FromUnixTimeSeconds(realTime.TvSec).AddTicks(realTime.TvNsec / 100);
        var monoNanos = (monoTime.TvSec * 1_000_000_000L) + monoTime.TvNsec;
        var activeElapsed = TimeSpan.FromSeconds(monoTime.TvSec) + TimeSpan.FromTicks(monoTime.TvNsec / 100);
        var bootElapsed = TimeSpan.FromSeconds(bootTime.TvSec) + TimeSpan.FromTicks(bootTime.TvNsec / 100);

        return (utc, monoNanos, activeElapsed, bootElapsed);
    }
}
