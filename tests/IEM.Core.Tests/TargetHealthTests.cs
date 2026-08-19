using IEM.Core.Health;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-7: Target Health & Explicit Exclusion.
/// Invariants 39, 40, 41, 42, 43, 44, 45.
/// </summary>
public sealed class TargetHealthTests
{
    private static readonly ProbeMethodology DefaultMethodology = new(20, 500, 1500);

    private static TargetProbeStatistics CreateMockWindowStats(
        string targetId,
        string address,
        TargetAddressFamily family,
        int replies,
        int timeouts,
        DateTimeOffset time)
    {
        var attempts = new List<TargetProbeAttempt>();
        for (var i = 1; i <= replies; i++)
        {
            attempts.Add(new TargetProbeAttempt(
                $"att-{i}", targetId, address, family, TargetProbeType.IcmpEcho,
                i, 32, time.AddMilliseconds(i * 100), 1500, ProbeOutcomeType.ReplyReceived, address, 15.0));
        }
        for (var i = 1; i <= timeouts; i++)
        {
            attempts.Add(new TargetProbeAttempt(
                $"to-{i}", targetId, address, family, TargetProbeType.IcmpEcho,
                replies + i, 32, time.AddMilliseconds((replies + i) * 100), 1500, ProbeOutcomeType.NoReplyBeforeTimeout));
        }

        return TargetProbeStatistics.CreateFromAttempts(targetId, address, family, TargetProbeType.IcmpEcho, DefaultMethodology, attempts);
    }

    [Fact]
    public void Fresh_target_starts_Unknown()
    {
        var target = new TargetIdentity("CloudflareDNS", "Cloudflare Primary", "1.1.1.1", TargetAddressFamily.IPv4);
        var evaluator = new TargetHealthEvaluator(target);

        Assert.Equal(TargetHealthState.Unknown, evaluator.CurrentState);
        Assert.Equal(TargetCapabilityState.Unknown, evaluator.CurrentCapability);
        Assert.Equal(EvidenceContribution.Full, evaluator.CurrentContribution);
        Assert.Equal(1.0, evaluator.CurrentWeight);
        Assert.Empty(evaluator.History);
    }

    [Fact]
    public void One_timeout_does_not_make_target_unhealthy()
    {
        var target = new TargetIdentity("CloudflareDNS", "Cloudflare Primary", "1.1.1.1", TargetAddressFamily.IPv4);
        var evaluator = new TargetHealthEvaluator(target);
        var now = DateTimeOffset.UtcNow;

        // Window 1: 20/20 replies -> Healthy
        evaluator.EvaluateWindow(CreateMockWindowStats("CloudflareDNS", "1.1.1.1", TargetAddressFamily.IPv4, 20, 0, now));
        Assert.Equal(TargetHealthState.Healthy, evaluator.CurrentState);

        // Window 2: 19 replies, 1 timeout (5% loss, at healthy threshold) -> remains Healthy
        evaluator.EvaluateWindow(CreateMockWindowStats("CloudflareDNS", "1.1.1.1", TargetAddressFamily.IPv4, 19, 1, now.AddMinutes(1)));
        Assert.Equal(TargetHealthState.Healthy, evaluator.CurrentState);
        Assert.Equal(EvidenceContribution.Full, evaluator.CurrentContribution);
    }

    [Fact]
    public void Repeated_isolated_failures_can_degrade_and_suspend_target()
    {
        var target = new TargetIdentity("TargetC", "Target C", "192.0.2.1", TargetAddressFamily.IPv4);
        var policy = new TargetHealthPolicy
        {
            FailureWindowsToDegrade = 2,
            FailureWindowsToUnresponsive = 3,
            RecoveryWindowsRequired = 2,
        };
        var evaluator = new TargetHealthEvaluator(target, policy);
        var now = DateTimeOffset.UtcNow;

        // Window 1: Clean
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetC", "192.0.2.1", TargetAddressFamily.IPv4, 20, 0, now));
        Assert.Equal(TargetHealthState.Healthy, evaluator.CurrentState);

        // Window 2: First bad window (0/20) - waiting confirmation
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetC", "192.0.2.1", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(1)));
        Assert.Equal(TargetHealthState.Healthy, evaluator.CurrentState); // Hysteresis prevents immediate flip

        // Window 3: Second consecutive bad window -> Degraded
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetC", "192.0.2.1", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(2)));
        Assert.Equal(TargetHealthState.Degraded, evaluator.CurrentState);
        Assert.Equal(EvidenceContribution.Reduced, evaluator.CurrentContribution);
        Assert.Equal(0.5, evaluator.CurrentWeight);

        // Window 4: Third consecutive bad/silent window -> Unresponsive & Suspended
        // Invariant 42: TARGET_EXCLUSION_IS_ALWAYS_VISIBLE_AND_REASONED
        var snap4 = evaluator.EvaluateWindow(CreateMockWindowStats("TargetC", "192.0.2.1", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(3)));
        Assert.Equal(TargetHealthState.Unresponsive, evaluator.CurrentState);
        Assert.Equal(EvidenceContribution.Suspended, evaluator.CurrentContribution);
        Assert.Equal(0.0, evaluator.CurrentWeight);
        Assert.Contains(snap4.ReasonCodes, r => r.Contains("suspendovan"));
    }

    [Fact]
    public void All_targets_failing_together_does_not_mark_one_target_bad_Invariant_43()
    {
        // Invariant 43: SHARED_FAILURE_NEVER_BECOMES_TARGET_FAILURE_BY_DEFAULT
        var targetA = new TargetIdentity("TargetA", "Target A", "1.1.1.1", TargetAddressFamily.IPv4);
        var evaluatorA = new TargetHealthEvaluator(targetA);
        var now = DateTimeOffset.UtcNow;

        // Healthy initially
        evaluatorA.EvaluateWindow(CreateMockWindowStats("TargetA", "1.1.1.1", TargetAddressFamily.IPv4, 20, 0, now));
        Assert.Equal(TargetHealthState.Healthy, evaluatorA.CurrentState);

        // 3 consecutive failed windows, but peer context reports all 3 peers failing (e.g. local cable unplugged or ISP outage)
        var sharedFailureContext = new PeerContext(TotalPeers: 3, RespondingPeers: 0, FailingPeers: 3);

        for (var i = 1; i <= 3; i++)
        {
            var snap = evaluatorA.EvaluateWindow(
                CreateMockWindowStats("TargetA", "1.1.1.1", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(i)),
                sharedFailureContext);

            Assert.Equal(TargetHealthState.Healthy, evaluatorA.CurrentState); // Does NOT degrade target
            Assert.Contains(snap.ReasonCodes, r => r.Contains("zajednički mrežni prekid"));
        }
    }

    [Fact]
    public void A_target_that_has_never_replied_is_not_called_ICMP_unsupported_Invariant_39()
    {
        // Invariant 39: ABSENCE_OF_REPLY_NEVER_PROVES_TARGET_CAPABILITY
        var target = new TargetIdentity("SilentTarget", "Silent Host", "198.51.100.1", TargetAddressFamily.IPv4);
        var evaluator = new TargetHealthEvaluator(target);
        var now = DateTimeOffset.UtcNow;

        evaluator.EvaluateWindow(CreateMockWindowStats("SilentTarget", "198.51.100.1", TargetAddressFamily.IPv4, 0, 20, now));

        Assert.Equal(TargetCapabilityState.ResponseNotYetObserved, evaluator.CurrentCapability);
    }

    [Fact]
    public void Healthy_history_is_not_deleted_when_target_degrades_Invariants_40_and_41()
    {
        // Invariant 40: TARGET_HEALTH_NEVER_REWRITES_PRIOR_EVIDENCE
        // Invariant 41: TARGET_HEALTH_CHANGE_NEVER_RETROACTIVELY_REWEIGHTS_HISTORY
        var target = new TargetIdentity("TargetD", "Target D", "8.8.4.4", TargetAddressFamily.IPv4);
        var evaluator = new TargetHealthEvaluator(target);
        var now = DateTimeOffset.UtcNow;

        // Window 1-3: Healthy
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetD", "8.8.4.4", TargetAddressFamily.IPv4, 20, 0, now));
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetD", "8.8.4.4", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(1)));
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetD", "8.8.4.4", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(2)));

        // Window 4-5: Bad
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetD", "8.8.4.4", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(3)));
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetD", "8.8.4.4", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(4)));

        // Total 5 snapshots recorded in append-only history
        Assert.Equal(5, evaluator.History.Count);

        // Historical snapshots 1, 2, 3 MUST retain their original Weight = 1.0 and State = Healthy
        Assert.Equal(TargetHealthState.Healthy, evaluator.History[0].State);
        Assert.Equal(1.0, evaluator.History[0].Weight);
        Assert.Equal(TargetHealthState.Healthy, evaluator.History[1].State);
        Assert.Equal(1.0, evaluator.History[1].Weight);
        Assert.Equal(TargetHealthState.Healthy, evaluator.History[2].State);
        Assert.Equal(1.0, evaluator.History[2].Weight);

        // Snapshot 5 has degraded weight
        Assert.Equal(TargetHealthState.Degraded, evaluator.History[4].State);
        Assert.Equal(0.5, evaluator.History[4].Weight);
    }

    [Fact]
    public void Recovery_requires_hysteresis()
    {
        var target = new TargetIdentity("TargetE", "Target E", "1.0.0.1", TargetAddressFamily.IPv4);
        var policy = new TargetHealthPolicy
        {
            FailureWindowsToDegrade = 1,
            FailureWindowsToUnresponsive = 2,
            RecoveryWindowsRequired = 3,
        };
        var evaluator = new TargetHealthEvaluator(target, policy);
        var now = DateTimeOffset.UtcNow;

        // Degrade and suspend
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetE", "1.0.0.1", TargetAddressFamily.IPv4, 0, 20, now));
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetE", "1.0.0.1", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(1)));
        Assert.Equal(TargetHealthState.Unresponsive, evaluator.CurrentState);

        // Recovery window 1: Still recovering
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetE", "1.0.0.1", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(2)));
        Assert.Equal(TargetHealthState.Recovering, evaluator.CurrentState);
        Assert.Equal(EvidenceContribution.Reduced, evaluator.CurrentContribution);

        // Recovery window 2: Still recovering
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetE", "1.0.0.1", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(3)));
        Assert.Equal(TargetHealthState.Recovering, evaluator.CurrentState);

        // Recovery window 3: Fully recovered
        evaluator.EvaluateWindow(CreateMockWindowStats("TargetE", "1.0.0.1", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(4)));
        Assert.Equal(TargetHealthState.Healthy, evaluator.CurrentState);
        Assert.Equal(EvidenceContribution.Full, evaluator.CurrentContribution);
        Assert.Equal(1.0, evaluator.CurrentWeight);
    }

    [Fact]
    public void Service_restart_rebuilds_same_health_state_Invariant_44()
    {
        // Invariant 44: TARGET_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE
        var target = new TargetIdentity("TargetF", "Target F", "9.9.9.9", TargetAddressFamily.IPv4);
        var policy = TargetHealthPolicy.Default;
        var now = DateTimeOffset.UtcNow;

        var windows = new List<TargetProbeStatistics>
        {
            CreateMockWindowStats("TargetF", "9.9.9.9", TargetAddressFamily.IPv4, 20, 0, now),
            CreateMockWindowStats("TargetF", "9.9.9.9", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(1)),
            CreateMockWindowStats("TargetF", "9.9.9.9", TargetAddressFamily.IPv4, 10, 10, now.AddMinutes(2)),
            CreateMockWindowStats("TargetF", "9.9.9.9", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(3)),
        };

        // Rebuild run 1
        var history1 = TargetHealthEvaluator.RebuildHealthHistory(target, windows, policy);

        // Rebuild run 2 (simulating restart and rebuild from disk log)
        var history2 = TargetHealthEvaluator.RebuildHealthHistory(target, windows, policy);

        Assert.Equal(history1.Count, history2.Count);
        for (var i = 0; i < history1.Count; i++)
        {
            Assert.Equal(history1[i].State, history2[i].State);
            Assert.Equal(history1[i].Capability, history2[i].Capability);
            Assert.Equal(history1[i].Contribution, history2[i].Contribution);
            Assert.Equal(history1[i].Weight, history2[i].Weight);
            Assert.Equal(history1[i].InterpretationRefId, history2[i].InterpretationRefId);
        }
    }

    [Fact]
    public void IPv4_health_never_changes_IPv6_health_Invariant_45()
    {
        // Invariant 45: TARGET_HEALTH_IS_SCOPED_TO_ENDPOINT_AND_ADDRESS_FAMILY
        var targetV4 = new TargetIdentity("Cloudflare", "Cloudflare DNS", "1.1.1.1", TargetAddressFamily.IPv4);
        var targetV6 = new TargetIdentity("Cloudflare", "Cloudflare DNS", "2606:4700:4700::1111", TargetAddressFamily.IPv6);

        var evaluatorV4 = new TargetHealthEvaluator(targetV4);
        var evaluatorV6 = new TargetHealthEvaluator(targetV6);
        var now = DateTimeOffset.UtcNow;

        // V4 fails completely for 3 windows
        for (var i = 0; i < 3; i++)
        {
            evaluatorV4.EvaluateWindow(CreateMockWindowStats("Cloudflare", "1.1.1.1", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(i)));
        }

        // V6 succeeds completely for 3 windows
        for (var i = 0; i < 3; i++)
        {
            evaluatorV6.EvaluateWindow(CreateMockWindowStats("Cloudflare", "2606:4700:4700::1111", TargetAddressFamily.IPv6, 20, 0, now.AddMinutes(i)));
        }

        Assert.Equal(TargetHealthState.Unresponsive, evaluatorV4.CurrentState);
        Assert.Equal(TargetHealthState.Healthy, evaluatorV6.CurrentState);
        Assert.Equal(0.0, evaluatorV4.CurrentWeight);
        Assert.Equal(1.0, evaluatorV6.CurrentWeight);
    }

    [Fact]
    public void Key_acceptance_scenario_isolated_failure_vs_shared_failure()
    {
        // Scenario from user:
        // A: 20/20 20/20 20/20
        // B: 20/20 20/20 20/20
        // C: 20/20  3/20  0/20
        // -> C gradually loses weight, A and B remain normal, C is NOT deleted, reason is preserved.
        var targetA = new TargetIdentity("A", "Target A", "1.1.1.1", TargetAddressFamily.IPv4);
        var targetB = new TargetIdentity("B", "Target B", "8.8.8.8", TargetAddressFamily.IPv4);
        var targetC = new TargetIdentity("C", "Target C", "9.9.9.9", TargetAddressFamily.IPv4);

        var evalA = new TargetHealthEvaluator(targetA);
        var evalB = new TargetHealthEvaluator(targetB);
        var evalC = new TargetHealthEvaluator(targetC);
        var now = DateTimeOffset.UtcNow;

        // Window 1: A=20/20, B=20/20, C=20/20
        var peerCtx1 = new PeerContext(3, 3, 0);
        evalA.EvaluateWindow(CreateMockWindowStats("A", "1.1.1.1", TargetAddressFamily.IPv4, 20, 0, now), peerCtx1);
        evalB.EvaluateWindow(CreateMockWindowStats("B", "8.8.8.8", TargetAddressFamily.IPv4, 20, 0, now), peerCtx1);
        evalC.EvaluateWindow(CreateMockWindowStats("C", "9.9.9.9", TargetAddressFamily.IPv4, 20, 0, now), peerCtx1);

        Assert.Equal(TargetHealthState.Healthy, evalA.CurrentState);
        Assert.Equal(TargetHealthState.Healthy, evalB.CurrentState);
        Assert.Equal(TargetHealthState.Healthy, evalC.CurrentState);

        // Window 2: A=20/20, B=20/20, C=3/20 (85% loss for C)
        var peerCtx2 = new PeerContext(3, 2, 1); // 2 responding, 1 failing -> isolated to C
        evalA.EvaluateWindow(CreateMockWindowStats("A", "1.1.1.1", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(1)), peerCtx2);
        evalB.EvaluateWindow(CreateMockWindowStats("B", "8.8.8.8", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(1)), peerCtx2);
        evalC.EvaluateWindow(CreateMockWindowStats("C", "9.9.9.9", TargetAddressFamily.IPv4, 3, 17, now.AddMinutes(1)), peerCtx2);

        Assert.Equal(TargetHealthState.Healthy, evalA.CurrentState);
        Assert.Equal(TargetHealthState.Healthy, evalB.CurrentState);

        // Window 3: A=20/20, B=20/20, C=0/20 (100% loss for C)
        var peerCtx3 = new PeerContext(3, 2, 1);
        evalA.EvaluateWindow(CreateMockWindowStats("A", "1.1.1.1", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(2)), peerCtx3);
        evalB.EvaluateWindow(CreateMockWindowStats("B", "8.8.8.8", TargetAddressFamily.IPv4, 20, 0, now.AddMinutes(2)), peerCtx3);
        evalC.EvaluateWindow(CreateMockWindowStats("C", "9.9.9.9", TargetAddressFamily.IPv4, 0, 20, now.AddMinutes(2)), peerCtx3);

        // A and B remain healthy
        Assert.Equal(TargetHealthState.Healthy, evalA.CurrentState);
        Assert.Equal(1.0, evalA.CurrentWeight);
        Assert.Equal(TargetHealthState.Healthy, evalB.CurrentState);
        Assert.Equal(1.0, evalB.CurrentWeight);

        // C has degraded
        Assert.Equal(TargetHealthState.Degraded, evalC.CurrentState);
        Assert.Equal(0.5, evalC.CurrentWeight);
        Assert.Equal(3, evalC.History.Count); // Not deleted!
    }
}
