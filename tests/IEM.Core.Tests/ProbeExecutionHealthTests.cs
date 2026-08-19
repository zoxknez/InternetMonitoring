using IEM.Core.ProbeHealth;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-9: Probe Execution Health & Epistemological Failure Classification.
/// Invariants 55-66.
/// </summary>
public sealed class ProbeExecutionHealthTests
{
    private static ProbeIdentity CreateIcmpProbe(string id = "icmp-v4", TargetAddressFamily family = TargetAddressFamily.IPv4) =>
        new(id, TargetProbeType.IcmpEcho, "SystemIcmp", "3.0.0", family);

    private static ProbeIdentity CreateDnsProbe(string id = "dns-v4", TargetAddressFamily family = TargetAddressFamily.IPv4) =>
        new(id, TargetProbeType.DnsQuery, "DnsClient", "3.0.0", family);

    [Fact]
    public void Local_socket_failure_is_not_network_failure_Invariant_55()
    {
        // Invariant 55: LOCAL_EXECUTION_FAILURE_IS_NEVER_REPORTED_AS_NETWORK_FAILURE
        var probe = CreateIcmpProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-1", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(5),
            ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed,
            "WinSock", 10055, "WSAENOBUFS", DiagnosticMessage: "No buffer space available");

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.FailedLocalSystem, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Ineligible, classification.Eligibility);
        Assert.Contains("LocalSystemOperationFailed", classification.ClassificationReasonCode);
    }

    [Fact]
    public void Resolver_API_failure_is_not_DNS_failure()
    {
        // Scenario A: DNS query prepared -> OS resolver API invocation fails locally
        var probe = CreateDnsProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-dns-local", probe, "google.com", TargetAddressFamily.IPv4, now, now.AddMilliseconds(2),
            ExecutionStage.NameResolution, ProbeRawOutcome.NativeOperationFailed,
            "DnsResolverApi", -1, "WSASYSCALLFAILURE", DiagnosticMessage: "Local resolver subsystem failed");

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.FailedLocalSystem, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Ineligible, classification.Eligibility);
        Assert.NotEqual(FailureDomain.FailedNetwork, classification.Domain);
        Assert.NotEqual(FailureDomain.FailedRemote, classification.Domain);
    }

    [Fact]
    public void Internal_exception_never_counts_as_network_loss_Invariant_59()
    {
        // Invariant 59: INTERNAL_PROBE_ERROR_NEVER_CONTRIBUTES_NETWORK_FAILURE_EVIDENCE
        var probe = CreateIcmpProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-bug", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(1),
            ExecutionStage.Preparation, ProbeRawOutcome.NativeOperationFailed,
            "InternalException", 0, "NullReferenceException", DiagnosticMessage: "Null ref in probe builder");

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.InternalError, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Ineligible, classification.Eligibility);
    }

    [Fact]
    public void Timeout_does_not_identify_failure_cause_Invariant_57()
    {
        // Invariant 57: TIMEOUT_DESCRIBES_OBSERVED_NON_COMPLETION_NOT_FAILURE_CAUSE
        var probe = CreateIcmpProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-timeout", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(2000),
            ExecutionStage.Receive, ProbeRawOutcome.NoResponseBeforeDeadline,
            TimeoutConfiguredMs: 2000);

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.Timeout, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Eligible, classification.Eligibility);
        Assert.Equal("NoResponseBeforeDeadline", classification.ClassificationReasonCode);
    }

    [Fact]
    public void Ambiguous_failure_remains_Unknown_Invariant_56()
    {
        // Invariant 56: AMBIGUOUS_PROBE_FAILURE_REMAINS_UNKNOWN
        var probe = CreateIcmpProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-ambig", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(100),
            ExecutionStage.ProtocolValidation, ProbeRawOutcome.Cancelled,
            DiagnosticMessage: "Process shut down mid-probe");

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.Unknown, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Limited, classification.Eligibility);
    }

    [Fact]
    public void HTTP_503_can_be_classified_as_remote_protocol_failure_Invariant_60()
    {
        // Invariant 60: REMOTE_FAILURE_REQUIRES_POSITIVE_REMOTE_OR_PROTOCOL_FAILURE_EVIDENCE
        var probe = new ProbeIdentity("http-probe", TargetProbeType.TcpSyn, "HttpProbe", "3.0.0", TargetAddressFamily.IPv4);
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-http-503", probe, "https://api.target.com", TargetAddressFamily.IPv4, now, now.AddMilliseconds(120),
            ExecutionStage.ProtocolValidation, ProbeRawOutcome.ProtocolNegativeResponseReceived,
            "HttpProtocol", 503, "HTTP_503_SERVICE_UNAVAILABLE", DiagnosticMessage: "Server returned 503");

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.FailedRemote, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Eligible, classification.Eligibility);
        Assert.Contains("RemoteProtocolFailureObserved", classification.ClassificationReasonCode);
    }

    [Fact]
    public void DNS_SERVFAIL_requires_actual_protocol_response_Scenario_B()
    {
        // Scenario B: DNS query successfully sent -> valid DNS response -> SERVFAIL
        var probe = CreateDnsProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-dns-servfail", probe, "example.com", TargetAddressFamily.IPv4, now, now.AddMilliseconds(45),
            ExecutionStage.Receive, ProbeRawOutcome.ProtocolNegativeResponseReceived,
            "DnsRCode", 2, "SERVFAIL", DiagnosticMessage: "DNS Server Failure Response");

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.FailedRemote, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Eligible, classification.Eligibility);
    }

    [Fact]
    public void Native_Windows_error_is_preserved_as_provenance_Invariant_58()
    {
        // Invariant 58: NATIVE_ERROR_CODE_IS_EVIDENCE_INPUT_NOT_FINAL_SEMANTIC_CLASSIFICATION
        var probe = CreateIcmpProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-win-prov", probe, "10.0.0.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(10),
            ExecutionStage.RouteResolution, ProbeRawOutcome.NativeOperationFailed,
            "WinSock", 10051, "WSAENETUNREACH");

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.FailedNetwork, classification.Domain);
        Assert.Contains(classification.SourceEvidenceRefs, r => r.Contains("WinSock:WSAENETUNREACH"));
    }

    [Fact]
    public void Successful_attempt_is_eligible_Invariant_62()
    {
        // Invariant 62: PROBE_EXECUTION_ELIGIBILITY_IS_EXPLICIT_NOT_IMPLICIT
        var probe = CreateIcmpProbe();
        var now = DateTimeOffset.UtcNow;
        var attempt = new ProbeExecutionAttempt(
            "att-ok", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(15),
            ExecutionStage.Completion, ProbeRawOutcome.Success);

        var classification = ProbeClassifier.ClassifyAttempt(attempt);

        Assert.Equal(FailureDomain.None, classification.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Eligible, classification.Eligibility);
    }

    [Fact]
    public void One_local_failure_does_not_degrade_probe_health_Invariant_63()
    {
        // Invariant 63: SINGLE_PROBE_EXECUTION_FAILURE_NEVER_ESTABLISHES_PROBE_UNHEALTHINESS
        var probe = CreateIcmpProbe();
        var policy = new ProbeFailurePolicy { LocalFailuresToDegrade = 2 };
        var evaluator = new ProbeHealthEvaluator(probe, policy);
        var now = DateTimeOffset.UtcNow;

        var attempts = new List<ProbeExecutionAttempt>
        {
            new("att-1", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(1), ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed, "WinSock", 10055, "WSAENOBUFS"),
        };

        var snapshot = evaluator.EvaluateWindow(attempts, now, now.AddMinutes(1));

        Assert.Equal(ProbeHealthState.Unknown, snapshot.HealthState); // Not Degraded after single failure
        Assert.Equal(1, snapshot.LocalFailureCount);
    }

    [Fact]
    public void Repeated_local_failures_can_degrade_and_make_unusable_probe_health()
    {
        var probe = CreateIcmpProbe();
        var policy = new ProbeFailurePolicy { LocalFailuresToDegrade = 2, LocalFailuresToUnusable = 4 };
        var evaluator = new ProbeHealthEvaluator(probe, policy);
        var now = DateTimeOffset.UtcNow;

        // Window 1: 2 local failures -> Degraded
        var w1 = new List<ProbeExecutionAttempt>
        {
            new("att-1", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(1), ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed, "WinSock", 10055, "WSAENOBUFS"),
            new("att-2", probe, "1.1.1.1", TargetAddressFamily.IPv4, now.AddSeconds(1), now.AddSeconds(1), ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed, "WinSock", 10055, "WSAENOBUFS"),
        };
        var s1 = evaluator.EvaluateWindow(w1, now, now.AddMinutes(1));
        Assert.Equal(ProbeHealthState.Degraded, s1.HealthState);

        // Window 2: 2 more local failures -> Unusable
        var w2 = new List<ProbeExecutionAttempt>
        {
            new("att-3", probe, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(1), now.AddMinutes(1), ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed, "WinSock", 10055, "WSAENOBUFS"),
            new("att-4", probe, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(1).AddSeconds(1), now.AddMinutes(1).AddSeconds(1), ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed, "WinSock", 10055, "WSAENOBUFS"),
        };
        var s2 = evaluator.EvaluateWindow(w2, now.AddMinutes(1), now.AddMinutes(2));
        Assert.Equal(ProbeHealthState.Unusable, s2.HealthState);
    }

    [Fact]
    public void Healthy_probe_requires_hysteresis_after_degradation()
    {
        var probe = CreateIcmpProbe();
        var policy = new ProbeFailurePolicy { LocalFailuresToDegrade = 1, RecoveryAttemptsRequired = 2 };
        var evaluator = new ProbeHealthEvaluator(probe, policy);
        var now = DateTimeOffset.UtcNow;

        // Degrade
        evaluator.EvaluateWindow(new List<ProbeExecutionAttempt>
        {
            new("att-fail", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(1), ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed, "WinSock", 10055, "WSAENOBUFS"),
        }, now, now.AddMinutes(1));
        Assert.Equal(ProbeHealthState.Degraded, evaluator.CurrentState);

        // Recovery window 1: Recovering
        evaluator.EvaluateWindow(new List<ProbeExecutionAttempt>
        {
            new("att-ok-1", probe, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(1), now.AddMinutes(1).AddMilliseconds(15), ExecutionStage.Completion, ProbeRawOutcome.Success),
        }, now.AddMinutes(1), now.AddMinutes(2));
        Assert.Equal(ProbeHealthState.Recovering, evaluator.CurrentState);

        // Recovery window 2: Healthy
        evaluator.EvaluateWindow(new List<ProbeExecutionAttempt>
        {
            new("att-ok-2", probe, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(2), now.AddMinutes(2).AddMilliseconds(15), ExecutionStage.Completion, ProbeRawOutcome.Success),
        }, now.AddMinutes(2), now.AddMinutes(3));
        Assert.Equal(ProbeHealthState.Healthy, evaluator.CurrentState);
    }

    [Fact]
    public void DNS_probe_health_does_not_change_ICMP_probe_health_Invariant_64()
    {
        // Invariant 64: PROBE_HEALTH_IS_SCOPED_TO_PROBE_IMPLEMENTATION_AND_RELEVANT_CONTEXT
        var probeIcmp = CreateIcmpProbe();
        var probeDns = CreateDnsProbe();

        var evalIcmp = new ProbeHealthEvaluator(probeIcmp);
        var evalDns = new ProbeHealthEvaluator(probeDns, new ProbeFailurePolicy { LocalFailuresToDegrade = 1 });
        var now = DateTimeOffset.UtcNow;

        // DNS has local failures
        evalDns.EvaluateWindow(new List<ProbeExecutionAttempt>
        {
            new("att-dns-err", probeDns, "8.8.8.8", TargetAddressFamily.IPv4, now, now, ExecutionStage.NameResolution, ProbeRawOutcome.NativeOperationFailed, "DnsApi", -1, "FAIL"),
        }, now, now.AddMinutes(1));

        // ICMP has clean execution
        evalIcmp.EvaluateWindow(new List<ProbeExecutionAttempt>
        {
            new("att-icmp-ok", probeIcmp, "8.8.8.8", TargetAddressFamily.IPv4, now, now.AddMilliseconds(10), ExecutionStage.Completion, ProbeRawOutcome.Success),
        }, now, now.AddMinutes(1));

        Assert.Equal(ProbeHealthState.Degraded, evalDns.CurrentState);
        Assert.Equal(ProbeHealthState.Healthy, evalIcmp.CurrentState);
    }

    [Fact]
    public void Historical_probe_attempts_are_never_reclassified_in_place_Invariants_65_and_66()
    {
        // Invariant 65: PROBE_HEALTH_NEVER_REWRITES_EXECUTION_EVIDENCE
        // Invariant 66: PROBE_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE
        var probe = CreateIcmpProbe();
        var policy = ProbeFailurePolicy.Default;
        var now = DateTimeOffset.UtcNow;

        var attempts = new List<ProbeExecutionAttempt>
        {
            new("a1", probe, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(15), ExecutionStage.Completion, ProbeRawOutcome.Success),
            new("a2", probe, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(1), now.AddMinutes(1).AddMilliseconds(15), ExecutionStage.Completion, ProbeRawOutcome.Success),
            new("a3", probe, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(2), now.AddMinutes(2).AddMilliseconds(15), ExecutionStage.Completion, ProbeRawOutcome.Success),
        };

        var history1 = ProbeHealthEvaluator.RebuildHistory(probe, attempts, TimeSpan.FromMinutes(1), policy);
        var history2 = ProbeHealthEvaluator.RebuildHistory(probe, attempts, TimeSpan.FromMinutes(1), policy);

        Assert.Equal(history1.Count, history2.Count);
        for (var i = 0; i < history1.Count; i++)
        {
            Assert.Equal(history1[i].HealthState, history2[i].HealthState);
            Assert.Equal(history1[i].InterpretationRefId, history2[i].InterpretationRefId);
            Assert.Equal(history1[i].ExecutedAttemptCount, history2[i].ExecutedAttemptCount);
        }
    }

    [Fact]
    public void End_to_end_acceptance_scenario_T0_to_T6()
    {
        // Scenario from user:
        // T0: ICMP send OK, reply OK -> Success, Eligible
        // T1: ICMP socket creation fails locally -> FailedLocalSystem, Ineligible for network failure, NOT target loss
        // T2: ICMP send succeeds, no reply before timeout -> Timeout, Eligible evidence of no reply, no asserted cause
        // T3: DNS resolver invocation itself fails -> FailedLocalSystem, NOT "DNS down"
        // T4: DNS query sent, SERVFAIL response received -> FailedRemote / protocol failure observed
        // T5: Probe implementation throws unexpected exception -> InternalError, no network evidence contribution
        // T6: Implementation recovers -> ProbeHealth Recovering -> Healthy after configured clean windows.
        // Historical T0-T5 records remain unchanged.

        var probeIcmp = CreateIcmpProbe("icmp-main");
        var probeDns = CreateDnsProbe("dns-main");
        var now = DateTimeOffset.UtcNow;

        // T0: ICMP success
        var t0 = new ProbeExecutionAttempt("t0", probeIcmp, "1.1.1.1", TargetAddressFamily.IPv4, now, now.AddMilliseconds(15), ExecutionStage.Completion, ProbeRawOutcome.Success);
        var c0 = ProbeClassifier.ClassifyAttempt(t0);
        Assert.Equal(FailureDomain.None, c0.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Eligible, c0.Eligibility);

        // T1: ICMP local socket failure
        var t1 = new ProbeExecutionAttempt("t1", probeIcmp, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(1), now.AddMinutes(1), ExecutionStage.SocketCreation, ProbeRawOutcome.NativeOperationFailed, "WinSock", 10055, "WSAENOBUFS");
        var c1 = ProbeClassifier.ClassifyAttempt(t1);
        Assert.Equal(FailureDomain.FailedLocalSystem, c1.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Ineligible, c1.Eligibility);

        // T2: ICMP timeout
        var t2 = new ProbeExecutionAttempt("t2", probeIcmp, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(2), now.AddMinutes(2).AddSeconds(2), ExecutionStage.Receive, ProbeRawOutcome.NoResponseBeforeDeadline, TimeoutConfiguredMs: 2000);
        var c2 = ProbeClassifier.ClassifyAttempt(t2);
        Assert.Equal(FailureDomain.Timeout, c2.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Eligible, c2.Eligibility);

        // T3: DNS resolver invocation fails locally
        var t3 = new ProbeExecutionAttempt("t3", probeDns, "example.com", TargetAddressFamily.IPv4, now.AddMinutes(3), now.AddMinutes(3), ExecutionStage.NameResolution, ProbeRawOutcome.NativeOperationFailed, "ResolverApi", -1, "RESOLVER_API_CRASH");
        var c3 = ProbeClassifier.ClassifyAttempt(t3);
        Assert.Equal(FailureDomain.FailedLocalSystem, c3.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Ineligible, c3.Eligibility);

        // T4: DNS query sent, SERVFAIL received
        var t4 = new ProbeExecutionAttempt("t4", probeDns, "example.com", TargetAddressFamily.IPv4, now.AddMinutes(4), now.AddMinutes(4).AddMilliseconds(30), ExecutionStage.Receive, ProbeRawOutcome.ProtocolNegativeResponseReceived, "DnsRCode", 2, "SERVFAIL");
        var c4 = ProbeClassifier.ClassifyAttempt(t4);
        Assert.Equal(FailureDomain.FailedRemote, c4.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Eligible, c4.Eligibility);

        // T5: Unexpected internal exception
        var t5 = new ProbeExecutionAttempt("t5", probeIcmp, "1.1.1.1", TargetAddressFamily.IPv4, now.AddMinutes(5), now.AddMinutes(5), ExecutionStage.Preparation, ProbeRawOutcome.NativeOperationFailed, "InternalException", 0, "NullReferenceException", DiagnosticMessage: "Crash in serializer");
        var c5 = ProbeClassifier.ClassifyAttempt(t5);
        Assert.Equal(FailureDomain.InternalError, c5.Domain);
        Assert.Equal(ProbeEvidenceEligibility.Ineligible, c5.Eligibility);

        // T6: Health evaluator with recovery
        var policy = new ProbeFailurePolicy { LocalFailuresToDegrade = 1, RecoveryAttemptsRequired = 2 };
        var healthEval = new ProbeHealthEvaluator(probeIcmp, policy);

        // Window with T1 + T5 (local and internal failures)
        var snapBad = healthEval.EvaluateWindow(new List<ProbeExecutionAttempt> { t1, t5 }, now, now.AddMinutes(5));
        Assert.Equal(ProbeHealthState.Degraded, snapBad.HealthState);

        // Window 1 of recovery
        var snapRec = healthEval.EvaluateWindow(new List<ProbeExecutionAttempt> { t0 }, now.AddMinutes(6), now.AddMinutes(7));
        Assert.Equal(ProbeHealthState.Recovering, snapRec.HealthState);

        // Window 2 of recovery -> Healthy
        var snapGood = healthEval.EvaluateWindow(new List<ProbeExecutionAttempt> { t0 }, now.AddMinutes(7), now.AddMinutes(8));
        Assert.Equal(ProbeHealthState.Healthy, snapGood.HealthState);

        // Historical T0-T5 classifications remain immutable
        Assert.Equal(FailureDomain.None, c0.Domain);
        Assert.Equal(FailureDomain.FailedLocalSystem, c1.Domain);
        Assert.Equal(FailureDomain.Timeout, c2.Domain);
        Assert.Equal(FailureDomain.FailedLocalSystem, c3.Domain);
        Assert.Equal(FailureDomain.FailedRemote, c4.Domain);
        Assert.Equal(FailureDomain.InternalError, c5.Domain);
    }
}
