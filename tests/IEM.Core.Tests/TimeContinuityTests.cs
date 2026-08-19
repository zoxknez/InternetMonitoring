using IEM.Core.Time;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-12: Boot Identity & Time Continuity.
/// Invariants 97-113.
/// </summary>
public sealed class TimeContinuityTests
{
    private const long Freq = 10_000_000; // 10 MHz QPC standard frequency

    private static ClockSample CreateSample(
        string id,
        string bootId,
        DateTimeOffset utc,
        long monotonicTicks,
        TimeSpan bootElapsed,
        TimeSpan activeElapsed) =>
        new(id, bootId, utc, monotonicTicks, Freq, bootElapsed, activeElapsed);

    [Fact]
    public void Fresh_boot_creates_boot_identity()
    {
        var now = DateTimeOffset.UtcNow;
        var obs = new BootObservation(
            "bobs-1", "boot-A", "Origin", now, "GetSystemTimePreciseAsFileTime",
            1000, Freq, "QPC", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), "WinProvider", "3.0.0");

        var assessment = TimeContinuityEvaluator.EvaluateBoot(null, obs);

        Assert.Equal(BootContinuityState.Established, assessment.State);
        Assert.Equal("boot-A", assessment.BootInstanceId);
    }

    [Fact]
    public void Second_observation_same_boot_preserves_identity()
    {
        var now = DateTimeOffset.UtcNow;
        var obs1 = new BootObservation("b1", "boot-A", "Origin", now, "SysTime", 1000, Freq, "QPC", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), "Win", "3.0");
        var obs2 = new BootObservation("b2", "boot-A", "Origin", now.AddMinutes(5), "SysTime", 1000 + 5 * 60 * Freq, Freq, "QPC", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), "Win", "3.0");

        var a1 = TimeContinuityEvaluator.EvaluateBoot(null, obs1);
        var a2 = TimeContinuityEvaluator.EvaluateBoot(obs1, obs2);

        Assert.Equal(BootContinuityState.Continued, a2.State);
        Assert.Equal("boot-A", a2.BootInstanceId);
    }

    [Fact]
    public void Service_restart_same_boot_preserves_boot_identity_Invariant_110()
    {
        // Invariant 110: SERVICE_RESTART_NEVER_IMPLIES_HOST_REBOOT
        var now = DateTimeOffset.UtcNow;
        var obsBeforeRestart = new BootObservation("b1", "boot-A", "Origin", now, "SysTime", 1000, Freq, "QPC", TimeSpan.FromHours(2), TimeSpan.FromHours(2), "Win", "3.0");
        var obsAfterRestart = new BootObservation("b2", "boot-A", "Origin", now.AddSeconds(10), "SysTime", 1000 + 10 * Freq, Freq, "QPC", TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(10)), TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(10)), "Win", "3.0");

        var a2 = TimeContinuityEvaluator.EvaluateBoot(obsBeforeRestart, obsAfterRestart);

        Assert.Equal(BootContinuityState.Continued, a2.State);
        Assert.Equal("boot-A", a2.BootInstanceId);
    }

    [Fact]
    public void Actual_reboot_starts_new_boot_identity_Invariants_101_and_107()
    {
        // Invariant 101 & 107: BOOT_IDENTITY_CHANGE_SPLITS_TIME_CONTINUITY & MONOTONIC_DURATION_IS_NEVER_COMPUTED_ACROSS_BOOT_INSTANCES
        var now = DateTimeOffset.UtcNow;
        var s1 = CreateSample("s1", "boot-A", now, 40_000 * Freq, TimeSpan.FromHours(5), TimeSpan.FromHours(5));
        var s2 = CreateSample("s2", "boot-B", now.AddMinutes(2), 200 * Freq, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        var assessment = TimeContinuityEvaluator.EvaluateTransition(s1, s2);

        Assert.Equal(ClockContinuityState.BootBoundaryObserved, assessment.State);
        Assert.Equal(TimeSpan.Zero, assessment.MonotonicDelta); // Never computed across boot instances!
    }

    [Fact]
    public void Wall_clock_forward_jump_does_not_change_elapsed_duration_Invariants_98_and_103()
    {
        // Invariant 98: WALL_CLOCK_NEVER_DEFINES_ELAPSED_DURATION
        // Invariant 103: CLOCK_DISCONTINUITY_REQUIRES_COMPARISON_WITH_AN_INDEPENDENT_ELAPSED_TIME_SOURCE
        var now = DateTimeOffset.Parse("2026-08-19T12:00:00Z");
        var s1 = CreateSample("s1", "boot-A", now, 1000 * Freq, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

        // Wall clock jumps forward +30 seconds, while real elapsed is 5 seconds
        var s2 = CreateSample("s2", "boot-A", now.AddSeconds(35), 1005 * Freq, TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(5)), TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(5)));

        var assessment = TimeContinuityEvaluator.EvaluateTransition(s1, s2);

        Assert.Equal(ClockContinuityState.ForwardAdjustmentObserved, assessment.State);
        Assert.Equal(TimeSpan.FromSeconds(5), assessment.MonotonicDelta); // Monotonic elapsed is true 5s!
        Assert.Equal(TimeSpan.FromSeconds(35), assessment.WallClockDelta);
        Assert.Equal(TimeSpan.FromSeconds(30), assessment.Divergence);
    }

    [Fact]
    public void Wall_clock_backward_jump_does_not_reverse_event_order_Invariant_105()
    {
        // Invariant 105: EVENT_ORDER_WITHIN_A_BOOT_IS_NEVER_DERIVED_FROM_WALL_CLOCK_ALONE
        var now = DateTimeOffset.Parse("2026-08-19T12:00:00Z");
        var s1 = CreateSample("s1", "boot-A", now, 1000 * Freq, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

        // Clock adjusted backwards -30 minutes
        var s2 = CreateSample("s2", "boot-A", now.AddMinutes(-30).AddSeconds(10), 1010 * Freq, TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(10)), TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(10)));

        var assessment = TimeContinuityEvaluator.EvaluateTransition(s1, s2);

        Assert.Equal(ClockContinuityState.BackwardAdjustmentObserved, assessment.State);
        Assert.Equal(TimeSpan.FromSeconds(10), assessment.MonotonicDelta); // True monotonic duration is +10s
        Assert.True(s2.MonotonicTimestamp > s1.MonotonicTimestamp); // Event 2 happened strictly after Event 1
    }

    [Fact]
    public void Suspend_increases_boot_elapsed_more_than_active_elapsed_Invariants_97_and_108()
    {
        // Invariant 97: SUSPEND_TIME_IS_NEVER_INTERPRETED_AS_NETWORK_DOWNTIME
        // Invariant 108: HOST_SUSPENSION_GAP_NEVER_CONTRIBUTES_NETWORK_OUTAGE_DURATION
        var now = DateTimeOffset.Parse("2026-08-19T23:00:00Z");
        var s1 = CreateSample("s1", "boot-A", now, 1000 * Freq, TimeSpan.FromHours(10), TimeSpan.FromHours(10));

        // Computer sleeps for 8 hours. BootElapsed grew by 8h, ActiveElapsed grew by only 1 minute.
        var s2 = CreateSample("s2", "boot-A", now.AddHours(8), 1060 * Freq, TimeSpan.FromHours(18), TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(1)));

        var assessment = TimeContinuityEvaluator.EvaluateTransition(s1, s2);

        Assert.Equal(ClockContinuityState.SuspendIntervalObserved, assessment.State);
        Assert.True(assessment.SuspendDuration >= TimeSpan.FromHours(7).Add(TimeSpan.FromMinutes(55)));
        Assert.Contains("Suspend/Sleep", assessment.ReasonCodes[0]);
    }

    [Fact]
    public void Resume_does_not_create_new_boot_identity_Invariant_109()
    {
        // Invariant 109: SUSPEND_RESUME_NEVER_CREATES_A_NEW_BOOT_INSTANCE_BY_DEFAULT
        var now = DateTimeOffset.UtcNow;
        var obsBefore = new BootObservation("b1", "boot-A", "Origin", now, "SysTime", 1000, Freq, "QPC", TimeSpan.FromHours(10), TimeSpan.FromHours(10), "Win", "3.0");
        var obsAfter = new BootObservation("b2", "boot-A", "Origin", now.AddHours(8), "SysTime", 1060, Freq, "QPC", TimeSpan.FromHours(18), TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(1)), "Win", "3.0");

        var assessment = TimeContinuityEvaluator.EvaluateBoot(obsBefore, obsAfter);

        Assert.Equal(BootContinuityState.Continued, assessment.State);
        Assert.Equal("boot-A", assessment.BootInstanceId);
    }

    [Fact]
    public void Clock_discontinuity_does_not_claim_unproven_cause_Invariant_104()
    {
        // Invariant 104: CLOCK_DISCONTINUITY_NEVER_IDENTIFIES_AN_UNPROVEN_ADJUSTMENT_CAUSE
        var now = DateTimeOffset.Parse("2026-08-19T12:00:00Z");
        var s1 = CreateSample("s1", "boot-A", now, 1000 * Freq, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        var s2 = CreateSample("s2", "boot-A", now.AddSeconds(14), 1010 * Freq, TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(10)), TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(10)));

        var assessment = TimeContinuityEvaluator.EvaluateTransition(s1, s2);

        Assert.Equal(ClockContinuityState.ForwardAdjustmentObserved, assessment.State);
        Assert.DoesNotContain("NTP", assessment.ReasonCodes[0]);
        Assert.DoesNotContain("User", assessment.ReasonCodes[0]);
    }

    [Fact]
    public void Deleting_derived_time_cache_rebuilds_identical_assessments_Invariant_112()
    {
        // Invariant 112: TIME_CONTINUITY_IS_REBUILDABLE_FROM_PERSISTED_TEMPORAL_EVIDENCE
        var now = DateTimeOffset.UtcNow;
        var samples = new List<ClockSample>
        {
            CreateSample("s1", "boot-A", now, 1000 * Freq, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            CreateSample("s2", "boot-A", now.AddMinutes(1), 1060 * Freq, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)),
            CreateSample("s3", "boot-A", now.AddMinutes(2), 1120 * Freq, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3)),
        };

        var history1 = TimeContinuityEvaluator.RebuildHistory(samples);
        var history2 = TimeContinuityEvaluator.RebuildHistory(samples);

        Assert.Equal(history1.Count, history2.Count);
        for (var i = 0; i < history1.Count; i++)
        {
            Assert.Equal(history1[i].State, history2[i].State);
            Assert.Equal(history1[i].MonotonicDelta, history2[i].MonotonicDelta);
            Assert.Equal(history1[i].InterpretationRefId, history2[i].InterpretationRefId);
        }
    }

    [Fact]
    public void Golden_acceptance_scenario_T0_to_T8()
    {
        // T0: Boot A, UTC 12:00, BootElapsed 2h00, Active 2h00 -> Continuous
        var t0 = DateTimeOffset.Parse("2026-08-19T12:00:00Z");
        var s0 = CreateSample("s0", "boot-A", t0, 1000 * Freq, TimeSpan.FromHours(2), TimeSpan.FromHours(2));

        // T1: UTC 12:10, BootElapsed 2h10, Active 2h10 -> Continuous
        var t1 = t0.AddMinutes(10);
        var s1 = CreateSample("s1", "boot-A", t1, 1000 * Freq + 600 * Freq, TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(10)), TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(10)));
        var a01 = TimeContinuityEvaluator.EvaluateTransition(s0, s1);
        Assert.Equal(ClockContinuityState.Continuous, a01.State);

        // T2: UTC moves backward to 11:40 (elapsed is 1 min) -> BackwardAdjustmentObserved, order T1 < T2
        var t2 = DateTimeOffset.Parse("2026-08-19T11:40:00Z");
        var s2 = CreateSample("s2", "boot-A", t2, s1.MonotonicTimestamp + 60 * Freq, TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(11)), TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(11)));
        var a12 = TimeContinuityEvaluator.EvaluateTransition(s1, s2);
        Assert.Equal(ClockContinuityState.BackwardAdjustmentObserved, a12.State);
        Assert.True(s2.MonotonicTimestamp > s1.MonotonicTimestamp);

        // T3/T4: machine sleeps 60 min -> SuspendIntervalObserved ~60m, NOT network outage
        var t4 = t2.AddMinutes(61);
        var s4 = CreateSample("s4", "boot-A", t4, s2.MonotonicTimestamp + 60 * Freq, TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(12)), TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(12)));
        var a24 = TimeContinuityEvaluator.EvaluateTransition(s2, s4);
        Assert.Equal(ClockContinuityState.SuspendIntervalObserved, a24.State);
        Assert.Equal(TimeSpan.FromMinutes(60), a24.SuspendDuration);

        // T5: Service restart -> Boot A preserved
        var obsBefore = new BootObservation("b1", "boot-A", "Basis", t4, "SysTime", s4.MonotonicTimestamp, Freq, "QPC", s4.BootElapsedIncludingSuspend, s4.ActiveElapsedExcludingSuspend, "Win", "3.0");
        var obsAfter = new BootObservation("b2", "boot-A", "Basis", t4.AddSeconds(5), "SysTime", s4.MonotonicTimestamp + 5 * Freq, Freq, "QPC", s4.BootElapsedIncludingSuspend.Add(TimeSpan.FromSeconds(5)), s4.ActiveElapsedExcludingSuspend.Add(TimeSpan.FromSeconds(5)), "Win", "3.0");
        var aBootRestart = TimeContinuityEvaluator.EvaluateBoot(obsBefore, obsAfter);
        Assert.Equal(BootContinuityState.Continued, aBootRestart.State);

        // T6/T7: Machine reboots -> Boot B, BootBoundaryObserved
        var s7 = CreateSample("s7", "boot-B", t4.AddMinutes(5), 100 * Freq, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var a47 = TimeContinuityEvaluator.EvaluateTransition(s4, s7);
        Assert.Equal(ClockContinuityState.BootBoundaryObserved, a47.State);
        Assert.Equal(TimeSpan.Zero, a47.MonotonicDelta);

        // T8: Wall clock jumps forward -> ForwardAdjustmentObserved, Boot B unchanged
        var s8 = CreateSample("s8", "boot-B", s7.CapturedUtc.AddMinutes(10), s7.MonotonicTimestamp + 60 * Freq, s7.BootElapsedIncludingSuspend.Add(TimeSpan.FromMinutes(1)), s7.ActiveElapsedExcludingSuspend.Add(TimeSpan.FromMinutes(1)));
        var a78 = TimeContinuityEvaluator.EvaluateTransition(s7, s8);
        Assert.Equal(ClockContinuityState.ForwardAdjustmentObserved, a78.State);

        // Rebuild from all samples
        var samples = new[] { s0, s1, s2, s4, s7, s8 };
        var rebuilt = TimeContinuityEvaluator.RebuildHistory(samples);
        Assert.Equal(5, rebuilt.Count);
        Assert.Equal(ClockContinuityState.Continuous, rebuilt[0].State);
        Assert.Equal(ClockContinuityState.BackwardAdjustmentObserved, rebuilt[1].State);
        Assert.Equal(ClockContinuityState.SuspendIntervalObserved, rebuilt[2].State);
        Assert.Equal(ClockContinuityState.BootBoundaryObserved, rebuilt[3].State);
        Assert.Equal(ClockContinuityState.ForwardAdjustmentObserved, rebuilt[4].State);
    }
}
