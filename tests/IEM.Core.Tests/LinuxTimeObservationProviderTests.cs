using System;
using IEM.Core.Time;
using IEM.Linux.Time;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Phase 3.1-6C LinuxTimeObservationProvider.
/// Verifies raw Linux clock fact extraction, nanosecond monotonic precision,
/// rejection of synthetic boot identities in 6C, and parity integration with Core TimeContinuityEvaluator.
/// </summary>
public sealed class LinuxTimeObservationProviderTests
{
    [Fact]
    public void CaptureClockSample_returns_correct_realtime_monotonic_and_boottime_facts()
    {
        var fakeClock = new FakeLinuxNativeClock
        {
            RealTime = new LinuxTimeSpec { TvSec = 1787162400, TvNsec = 500_000_000 }, // 2026-08-19 18:00:00.500 UTC
            MonotonicTime = new LinuxTimeSpec { TvSec = 100, TvNsec = 250_000_000 },  // 100.25 s
            BootTime = new LinuxTimeSpec { TvSec = 125, TvNsec = 750_000_000 },       // 125.75 s
        };

        var provider = new LinuxTimeObservationProvider(fakeClock);
        var sample = provider.CaptureClockSample("boot-test-instance-123");

        Assert.NotNull(sample);
        Assert.Equal("boot-test-instance-123", sample.BootInstanceId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787162400).AddMilliseconds(500), sample.CapturedUtc);
        Assert.Equal(100_250_000_000L, sample.MonotonicTimestamp);
        Assert.Equal(1_000_000_000L, sample.MonotonicFrequency);
        Assert.Equal(TimeSpan.FromSeconds(100) + TimeSpan.FromMilliseconds(250), sample.ActiveElapsedExcludingSuspend);
        Assert.Equal(TimeSpan.FromSeconds(125) + TimeSpan.FromMilliseconds(750), sample.BootElapsedIncludingSuspend);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CaptureClockSample_rejects_empty_or_whitespace_boot_instance_id(string? invalidId)
    {
        var provider = new LinuxTimeObservationProvider(new FakeLinuxNativeClock());
        Assert.ThrowsAny<ArgumentException>(() => provider.CaptureClockSample(invalidId!));
    }

    [Fact]
    public void CaptureBootObservation_with_known_boot_id_preserves_provenance()
    {
        var fakeClock = new FakeLinuxNativeClock
        {
            RealTime = new LinuxTimeSpec { TvSec = 1787162400, TvNsec = 0 },
            MonotonicTime = new LinuxTimeSpec { TvSec = 50, TvNsec = 0 },
            BootTime = new LinuxTimeSpec { TvSec = 50, TvNsec = 0 },
        };

        var provider = new LinuxTimeObservationProvider(fakeClock);
        var bobs = provider.CaptureBootObservation("known-linux-boot-id-456");

        Assert.Equal("known-linux-boot-id-456", bobs.BootInstanceId);
        Assert.Equal("KnownBootInstanceId", bobs.BootIdentityBasis);
        Assert.Equal("clock_gettime(CLOCK_REALTIME)", bobs.WallClockSource);
        Assert.Equal("clock_gettime(CLOCK_MONOTONIC)", bobs.MonotonicSource);
        Assert.Equal(50_000_000_000L, bobs.MonotonicTimestamp);
        Assert.Equal(1_000_000_000L, bobs.MonotonicFrequency);
        Assert.Equal(TimeSpan.FromSeconds(50), bobs.ActiveElapsedExcludingSuspend);
        Assert.Equal(TimeSpan.FromSeconds(50), bobs.BootElapsedIncludingSuspend);
        Assert.Equal("LinuxHighPrecisionTimeProvider", bobs.ProviderId);
        Assert.Equal("3.1.0", bobs.ProviderVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CaptureBootObservation_without_known_boot_id_throws_InvalidOperationException_in_6C(string? invalidId)
    {
        var provider = new LinuxTimeObservationProvider(new FakeLinuxNativeClock());
        var ex = Assert.Throws<InvalidOperationException>(() => provider.CaptureBootObservation(invalidId));
        Assert.Contains("3.1-6D", ex.Message);
    }

    [Fact]
    public void Native_clock_failure_throws_explicit_exception_without_synthetic_fallback()
    {
        var failingClock = new FakeLinuxNativeClock { FailOnClockId = LinuxNativeClock.CLOCK_BOOTTIME };
        var provider = new LinuxTimeObservationProvider(failingClock);

        Assert.Throws<InvalidOperationException>(() => provider.CaptureClockSample("boot-1"));
    }

    [Fact]
    public void Core_TimeContinuityEvaluator_correctly_infers_suspend_gap_from_Linux_clock_samples()
    {
        var fakeClock = new FakeLinuxNativeClock();
        var provider = new LinuxTimeObservationProvider(fakeClock);

        // Sample A: active = 100s, boot = 100s
        fakeClock.RealTime = new LinuxTimeSpec { TvSec = 1787162400, TvNsec = 0 };
        fakeClock.MonotonicTime = new LinuxTimeSpec { TvSec = 100, TvNsec = 0 };
        fakeClock.BootTime = new LinuxTimeSpec { TvSec = 100, TvNsec = 0 };
        var sampleA = provider.CaptureClockSample("boot-1");

        // Sample B: active = 110s, boot = 140s (30s suspend gap)
        fakeClock.RealTime = new LinuxTimeSpec { TvSec = 1787162440, TvNsec = 0 };
        fakeClock.MonotonicTime = new LinuxTimeSpec { TvSec = 110, TvNsec = 0 };
        fakeClock.BootTime = new LinuxTimeSpec { TvSec = 140, TvNsec = 0 };
        var sampleB = provider.CaptureClockSample("boot-1");

        // Core canonical evaluation
        var assessment = TimeContinuityEvaluator.EvaluateTransition(sampleA, sampleB);

        Assert.NotNull(assessment);
        Assert.Equal(ClockContinuityState.SuspendIntervalObserved, assessment.State);
        Assert.Equal(TimeSpan.FromSeconds(10), assessment.ActiveElapsedDelta);
        Assert.Equal(TimeSpan.FromSeconds(40), assessment.BootElapsedDelta);
        Assert.Equal(TimeSpan.FromSeconds(30), assessment.SuspendDuration);
    }

    private sealed class FakeLinuxNativeClock : ILinuxNativeClock
    {
        public LinuxTimeSpec RealTime { get; set; } = new LinuxTimeSpec { TvSec = 1787162400, TvNsec = 0 };
        public LinuxTimeSpec MonotonicTime { get; set; } = new LinuxTimeSpec { TvSec = 10, TvNsec = 0 };
        public LinuxTimeSpec BootTime { get; set; } = new LinuxTimeSpec { TvSec = 10, TvNsec = 0 };
        public int? FailOnClockId { get; set; }

        public void GetTime(int clockId, out LinuxTimeSpec timeSpec)
        {
            if (FailOnClockId.HasValue && FailOnClockId.Value == clockId)
            {
                throw new InvalidOperationException($"Simulated native clock failure on clockId={clockId}");
            }

            timeSpec = clockId switch
            {
                LinuxNativeClock.CLOCK_REALTIME => RealTime,
                LinuxNativeClock.CLOCK_MONOTONIC => MonotonicTime,
                LinuxNativeClock.CLOCK_BOOTTIME => BootTime,
                _ => throw new ArgumentOutOfRangeException(nameof(clockId), clockId, "Unknown clockId"),
            };
        }
    }
}
