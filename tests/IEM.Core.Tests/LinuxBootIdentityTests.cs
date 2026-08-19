using System;
using System.IO;
using IEM.Core.Time;
using IEM.Linux.Time;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Phase 3.1-6D Boot Identity & Ambiguity.
/// Verifies /proc/sys/kernel/random/boot_id parsing, /proc/uptime correlation facts,
/// distinction between process/service restart and system reboot, and explicit Ambiguous state handling without synthetic boot identity.
/// </summary>
public sealed class LinuxBootIdentityTests
{
    [Fact]
    public void TryReadBootId_with_valid_uuid_returns_canonical_id()
    {
        var success = LinuxBootFacts.TryReadBootId(
            out var bootId,
            out var reasonCode,
            _ => "2B6B0C26-8EB5-4E3F-B649-411A5FF6B142");

        Assert.True(success);
        Assert.Null(reasonCode);
        Assert.Equal("linux-boot-2b6b0c26-8eb5-4e3f-b649-411a5ff6b142", bootId);
    }

    [Fact]
    public void TryReadBootId_trims_whitespace_and_newlines()
    {
        var success = LinuxBootFacts.TryReadBootId(
            out var bootId,
            out var reasonCode,
            _ => "  \n  2b6b0c26-8eb5-4e3f-b649-411a5ff6b142  \r\n ");

        Assert.True(success);
        Assert.Null(reasonCode);
        Assert.Equal("linux-boot-2b6b0c26-8eb5-4e3f-b649-411a5ff6b142", bootId);
    }

    [Theory]
    [InlineData("not-a-valid-uuid")]
    [InlineData("2b6b0c26-8eb5-4e3f-b649-411a5ff6b14")]
    [InlineData("2b6b0c26-8eb5-4e3f-b649-411a5ff6b142z")]
    public void TryReadBootId_with_malformed_uuid_returns_BOOT_ID_MALFORMED(string malformed)
    {
        var success = LinuxBootFacts.TryReadBootId(
            out var bootId,
            out var reasonCode,
            _ => malformed);

        Assert.False(success);
        Assert.Null(bootId);
        Assert.Equal(LinuxBootFacts.ReasonBootIdMalformed, reasonCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\r\t  ")]
    public void TryReadBootId_with_empty_file_returns_BOOT_ID_EMPTY(string empty)
    {
        var success = LinuxBootFacts.TryReadBootId(
            out var bootId,
            out var reasonCode,
            _ => empty);

        Assert.False(success);
        Assert.Null(bootId);
        Assert.Equal(LinuxBootFacts.ReasonBootIdEmpty, reasonCode);
    }

    [Fact]
    public void TryReadBootId_with_missing_file_returns_BOOT_ID_UNAVAILABLE()
    {
        var success = LinuxBootFacts.TryReadBootId(
            out var bootId,
            out var reasonCode,
            _ => throw new FileNotFoundException());

        Assert.False(success);
        Assert.Null(bootId);
        Assert.Equal(LinuxBootFacts.ReasonBootIdUnavailable, reasonCode);
    }

    [Fact]
    public void TryReadBootId_with_io_exception_returns_BOOT_ID_READ_FAILED()
    {
        var success = LinuxBootFacts.TryReadBootId(
            out var bootId,
            out var reasonCode,
            _ => throw new UnauthorizedAccessException());

        Assert.False(success);
        Assert.Null(bootId);
        Assert.Equal(LinuxBootFacts.ReasonBootIdReadFailed, reasonCode);
    }

    [Fact]
    public void TryReadProcUptime_parses_first_field_with_invariant_culture()
    {
        var success = LinuxBootFacts.TryReadProcUptime(
            out var uptime,
            out var reasonCode,
            _ => "45231.84 180927.36\n");

        Assert.True(success);
        Assert.Null(reasonCode);
        Assert.Equal(TimeSpan.FromSeconds(45231.84), uptime);
    }

    [Theory]
    [InlineData("invalid-uptime")]
    [InlineData("-12.50 10.0")]
    public void TryReadProcUptime_with_malformed_value_returns_BOOT_UPTIME_MALFORMED(string malformed)
    {
        var success = LinuxBootFacts.TryReadProcUptime(
            out var uptime,
            out var reasonCode,
            _ => malformed);

        Assert.False(success);
        Assert.Equal(TimeSpan.Zero, uptime);
        Assert.Equal(LinuxBootFacts.ReasonBootUptimeMalformed, reasonCode);
    }

    [Fact]
    public void CorrelateUptime_when_within_tolerance_returns_Consistent()
    {
        var status = LinuxBootFacts.CorrelateUptime(
            bootElapsedFromClock: TimeSpan.FromSeconds(100.02),
            out var procUptime,
            out var reasonCode,
            fileReader: _ => "100.00 400.00\n",
            tolerance: TimeSpan.FromMilliseconds(50));

        Assert.Equal(UptimeCorrelationStatus.Consistent, status);
        Assert.Null(reasonCode);
        Assert.Equal(TimeSpan.FromSeconds(100.00), procUptime);
    }

    [Fact]
    public void CorrelateUptime_when_outside_tolerance_returns_BOOT_UPTIME_CORRELATION_MISMATCH()
    {
        var status = LinuxBootFacts.CorrelateUptime(
            bootElapsedFromClock: TimeSpan.FromSeconds(130.00),
            out var procUptime,
            out var reasonCode,
            fileReader: _ => "100.00 400.00\n",
            tolerance: TimeSpan.FromSeconds(1));

        Assert.Equal(UptimeCorrelationStatus.Mismatch, status);
        Assert.Equal(LinuxBootFacts.ReasonBootUptimeCorrelationMismatch, reasonCode);
        Assert.Equal(TimeSpan.FromSeconds(100.00), procUptime);
    }

    [Fact]
    public void CorrelateUptime_when_uptime_unavailable_returns_Unavailable_and_preserves_valid_boot_id_in_provider()
    {
        var status = LinuxBootFacts.CorrelateUptime(
            bootElapsedFromClock: TimeSpan.FromSeconds(100.00),
            out var procUptime,
            out var reasonCode,
            fileReader: _ => throw new FileNotFoundException());

        Assert.Equal(UptimeCorrelationStatus.Unavailable, status);
        Assert.Equal(LinuxBootFacts.ReasonBootUptimeUnavailable, reasonCode);
        Assert.Null(procUptime);

        // Verify provider still succeeds with valid BootObservation even if /proc/uptime is missing
        var fakeClock = new FakeLinuxClockForIdentity();
        var provider = new LinuxTimeObservationProvider(
            fakeClock,
            path => path == LinuxBootFacts.BootIdPath
                ? "2b6b0c26-8eb5-4e3f-b649-411a5ff6b142\n"
                : throw new FileNotFoundException());

        var bobs = provider.CaptureBootObservation();
        Assert.NotNull(bobs);
        Assert.Equal("linux-boot-2b6b0c26-8eb5-4e3f-b649-411a5ff6b142", bobs.BootInstanceId);
    }

    private sealed class FakeLinuxClockForIdentity : ILinuxNativeClock
    {
        public void GetTime(int clockId, out LinuxTimeSpec timeSpec)
        {
            timeSpec = new LinuxTimeSpec { TvSec = 100, TvNsec = 0 };
        }
    }

    [Fact]
    public void TimeContinuityEvaluator_EvaluateBoot_on_initial_observation_returns_Established()
    {
        var obs = CreateBootObservation("linux-boot-aaa");
        var assessment = TimeContinuityEvaluator.EvaluateBoot(previous: null, current: obs);

        Assert.Equal("linux-boot-aaa", assessment.BootInstanceId);
        Assert.Equal(BootContinuityState.Established, assessment.State);
    }

    [Fact]
    public void TimeContinuityEvaluator_EvaluateBoot_on_service_restart_with_same_boot_id_returns_Continued()
    {
        var obs1 = CreateBootObservation("linux-boot-aaa");
        var obs2 = CreateBootObservation("linux-boot-aaa");

        var assessment = TimeContinuityEvaluator.EvaluateBoot(previous: obs1, current: obs2);

        Assert.Equal("linux-boot-aaa", assessment.BootInstanceId);
        Assert.Equal(BootContinuityState.Continued, assessment.State);
    }

    [Fact]
    public void TimeContinuityEvaluator_EvaluateBoot_on_reboot_with_changed_boot_id_returns_Changed()
    {
        var obs1 = CreateBootObservation("linux-boot-aaa");
        var obs2 = CreateBootObservation("linux-boot-bbb");

        var assessment = TimeContinuityEvaluator.EvaluateBoot(previous: obs1, current: obs2);

        Assert.Equal("linux-boot-bbb", assessment.BootInstanceId);
        Assert.Equal(BootContinuityState.Changed, assessment.State);
    }

    [Fact]
    public void TimeContinuityEvaluator_EvaluateBoot_with_null_current_observation_returns_Ambiguous_with_null_BootInstanceId()
    {
        var assessment = TimeContinuityEvaluator.EvaluateBoot(previous: null, current: null);

        Assert.Null(assessment.BootInstanceId);
        Assert.Equal(BootContinuityState.Ambiguous, assessment.State);
        Assert.Contains(LinuxBootFacts.ReasonBootIdentityAmbiguous, assessment.ReasonCodes);
        Assert.Contains(LinuxBootFacts.ReasonBootIdUnavailable, assessment.ReasonCodes);
    }

    [Fact]
    public void TimeContinuityEvaluator_EvaluateBoot_with_previous_known_and_null_current_includes_known_reason_code()
    {
        var obsPrev = CreateBootObservation("linux-boot-aaa");
        var assessment = TimeContinuityEvaluator.EvaluateBoot(previous: obsPrev, current: null);

        Assert.Null(assessment.BootInstanceId);
        Assert.Equal(BootContinuityState.Ambiguous, assessment.State);
        Assert.Contains("PREVIOUS_BOOT_ID_KNOWN_CURRENT_UNAVAILABLE", assessment.ReasonCodes);
        Assert.Contains(LinuxBootFacts.ReasonBootIdentityAmbiguous, assessment.ReasonCodes);
    }

    private static BootObservation CreateBootObservation(string bootId)
    {
        return new BootObservation(
            ObservationId: $"bobs-{Guid.NewGuid():N}",
            BootInstanceId: bootId,
            BootIdentityBasis: "LinuxKernelRandomBootId",
            CapturedUtc: DateTimeOffset.UtcNow,
            WallClockSource: "clock_gettime(CLOCK_REALTIME)",
            MonotonicTimestamp: 100_000_000_000L,
            MonotonicFrequency: 1_000_000_000L,
            MonotonicSource: "clock_gettime(CLOCK_MONOTONIC)",
            BootElapsedIncludingSuspend: TimeSpan.FromSeconds(100),
            ActiveElapsedExcludingSuspend: TimeSpan.FromSeconds(100),
            ProviderId: "LinuxHighPrecisionTimeProvider",
            ProviderVersion: "3.1.0");
    }
}
