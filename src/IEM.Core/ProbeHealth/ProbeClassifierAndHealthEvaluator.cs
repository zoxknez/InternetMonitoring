namespace IEM.Core.ProbeHealth;

/// <summary>
/// Classifies raw probe execution attempts into failure domains and manages probe engine health.
/// Invariants 55-66.
/// </summary>
public static class ProbeClassifier
{
    public static ProbeFailureClassification ClassifyAttempt(
        ProbeExecutionAttempt attempt,
        ProbeFailurePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        policy ??= ProbeFailurePolicy.Default;

        var sourceEvidenceRefs = new List<string>
        {
            $"attempt:{attempt.AttemptId}",
            $"stage={attempt.Stage};outcome={attempt.RawOutcome}",
        };

        if (attempt.NativeErrorCode.HasValue || attempt.NativeErrorName is not null)
        {
            sourceEvidenceRefs.Add($"native={attempt.NativeErrorDomain}:{attempt.NativeErrorName ?? attempt.NativeErrorCode?.ToString()}");
        }

        var interpretationRefId = $"classifier:v{policy.PolicyVersion}:{policy.PolicyHash}";
        var policyRefId = policy.PolicyHash;

        // 1. Success
        if (attempt.RawOutcome == ProbeRawOutcome.Success)
        {
            return new ProbeFailureClassification(
                AttemptId: attempt.AttemptId,
                Domain: FailureDomain.None,
                Eligibility: ProbeEvidenceEligibility.Eligible,
                ClassificationReasonCode: "ExecutionSuccessful",
                SourceEvidenceRefs: sourceEvidenceRefs,
                InterpretationRefId: interpretationRefId,
                PolicyRefId: policyRefId,
                ConfidenceBasis: "PositiveSuccessfulObservation");
        }

        // 2. Timeout (Invariant 57: TIMEOUT_DESCRIBES_OBSERVED_NON_COMPLETION_NOT_FAILURE_CAUSE)
        if (attempt.RawOutcome == ProbeRawOutcome.NoResponseBeforeDeadline)
        {
            return new ProbeFailureClassification(
                AttemptId: attempt.AttemptId,
                Domain: FailureDomain.Timeout,
                Eligibility: ProbeEvidenceEligibility.Eligible,
                ClassificationReasonCode: "NoResponseBeforeDeadline",
                SourceEvidenceRefs: sourceEvidenceRefs,
                InterpretationRefId: interpretationRefId,
                PolicyRefId: policyRefId,
                ConfidenceBasis: "ObservedNonCompletion");
        }

        // 3. Positive Remote or Protocol Failure (Invariant 60: REMOTE_FAILURE_REQUIRES_POSITIVE_REMOTE_OR_PROTOCOL_FAILURE_EVIDENCE)
        if (attempt.RawOutcome == ProbeRawOutcome.ProtocolNegativeResponseReceived)
        {
            return new ProbeFailureClassification(
                AttemptId: attempt.AttemptId,
                Domain: FailureDomain.FailedRemote,
                Eligibility: ProbeEvidenceEligibility.Eligible,
                ClassificationReasonCode: $"RemoteProtocolFailureObserved ({attempt.NativeErrorName ?? attempt.NativeErrorCode?.ToString() ?? "NegativeResponse"})",
                SourceEvidenceRefs: sourceEvidenceRefs,
                InterpretationRefId: interpretationRefId,
                PolicyRefId: policyRefId,
                ConfidenceBasis: "ExplicitRemoteProtocolResponse");
        }

        // 4. Internal IEM Error (Invariant 59: INTERNAL_PROBE_ERROR_NEVER_CONTRIBUTES_NETWORK_FAILURE_EVIDENCE)
        if (string.Equals(attempt.NativeErrorDomain, "InternalException", StringComparison.OrdinalIgnoreCase))
        {
            return new ProbeFailureClassification(
                AttemptId: attempt.AttemptId,
                Domain: FailureDomain.InternalError,
                Eligibility: ProbeEvidenceEligibility.Ineligible,
                ClassificationReasonCode: $"InternalIemFault ({attempt.DiagnosticMessage ?? "UnhandledException"})",
                SourceEvidenceRefs: sourceEvidenceRefs,
                InterpretationRefId: interpretationRefId,
                PolicyRefId: policyRefId,
                ConfidenceBasis: "InternalEngineDiagnostic");
        }

        // 5. Local OS / System Operation Failure (Invariant 55: LOCAL_EXECUTION_FAILURE_IS_NEVER_REPORTED_AS_NETWORK_FAILURE)
        if (attempt.RawOutcome == ProbeRawOutcome.NativeOperationFailed)
        {
            if (attempt.Stage is ExecutionStage.Preparation or ExecutionStage.NameResolution or ExecutionStage.SocketCreation or ExecutionStage.Bind)
            {
                return new ProbeFailureClassification(
                    AttemptId: attempt.AttemptId,
                    Domain: FailureDomain.FailedLocalSystem,
                    Eligibility: ProbeEvidenceEligibility.Ineligible,
                    ClassificationReasonCode: $"LocalSystemOperationFailed ({attempt.Stage}: {attempt.NativeErrorName ?? attempt.DiagnosticMessage ?? "NativeFailure"})",
                    SourceEvidenceRefs: sourceEvidenceRefs,
                    InterpretationRefId: interpretationRefId,
                    PolicyRefId: policyRefId,
                    ConfidenceBasis: "LocalSystemFailureDiagnostic");
            }

            // Route resolution / Network path condition
            if (attempt.Stage is ExecutionStage.RouteResolution or ExecutionStage.Connect or ExecutionStage.Send)
            {
                var errorUpper = (attempt.NativeErrorName ?? string.Empty).ToUpperInvariant();
                if (errorUpper.Contains("UNREACH") || errorUpper.Contains("NOROUTE") || errorUpper.Contains("NETWORK_DOWN"))
                {
                    // Invariant 61: NETWORK_FAILURE_CLASSIFICATION_NEVER_IDENTIFIES_UNPROVEN_ROOT_CAUSE
                    return new ProbeFailureClassification(
                        AttemptId: attempt.AttemptId,
                        Domain: FailureDomain.FailedNetwork,
                        Eligibility: ProbeEvidenceEligibility.Eligible,
                        ClassificationReasonCode: $"NetworkPathConditionPreventedOperation ({attempt.NativeErrorName ?? "Unreachable"})",
                        SourceEvidenceRefs: sourceEvidenceRefs,
                        InterpretationRefId: interpretationRefId,
                        PolicyRefId: policyRefId,
                        ConfidenceBasis: "NetworkPathDiagnostic");
                }

                return new ProbeFailureClassification(
                    AttemptId: attempt.AttemptId,
                    Domain: FailureDomain.FailedLocalSystem,
                    Eligibility: ProbeEvidenceEligibility.Ineligible,
                    ClassificationReasonCode: $"LocalExecutionFailure ({attempt.Stage}: {attempt.NativeErrorName ?? "NativeError"})",
                    SourceEvidenceRefs: sourceEvidenceRefs,
                    InterpretationRefId: interpretationRefId,
                    PolicyRefId: policyRefId,
                    ConfidenceBasis: "LocalOperationFailureDiagnostic");
            }
        }

        // 6. Ambiguous Failure (Invariant 56: AMBIGUOUS_PROBE_FAILURE_REMAINS_UNKNOWN)
        return new ProbeFailureClassification(
            AttemptId: attempt.AttemptId,
            Domain: FailureDomain.Unknown,
            Eligibility: ProbeEvidenceEligibility.Limited,
            ClassificationReasonCode: "AmbiguousProbeOutcome",
            SourceEvidenceRefs: sourceEvidenceRefs,
            InterpretationRefId: interpretationRefId,
            PolicyRefId: policyRefId,
            ConfidenceBasis: "InconclusiveEvidence");
    }
}

/// <summary>
/// Evaluates health of a specific probe execution pipeline across chronological attempts.
/// Invariants 63-66.
/// </summary>
public sealed class ProbeHealthEvaluator
{
    private readonly ProbeIdentity _probe;
    private readonly ProbeFailurePolicy _policy;
    private readonly List<ProbeHealthSnapshot> _history = new();

    private ProbeHealthState _state = ProbeHealthState.Unknown;
    private int _consecutiveLocalFailures;
    private int _consecutiveSuccesses;

    public ProbeHealthEvaluator(ProbeIdentity probe, ProbeFailurePolicy? policy = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _policy = policy ?? ProbeFailurePolicy.Default;
    }

    public ProbeIdentity Probe => _probe;
    public ProbeFailurePolicy Policy => _policy;
    public ProbeHealthState CurrentState => _state;
    public IReadOnlyList<ProbeHealthSnapshot> History => _history.AsReadOnly();

    public ProbeHealthSnapshot EvaluateWindow(
        IReadOnlyList<ProbeExecutionAttempt> attempts,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var reasons = new List<string>();
        var sourceEvidenceRefs = new List<string>
        {
            $"window:{windowStartUtc:O}..{windowEndUtc:O}",
            $"attempts_count={attempts.Count}",
        };

        var executedCount = attempts.Count;
        var eligibleCount = 0;
        var localFailureCount = 0;
        var internalErrorCount = 0;

        foreach (var attempt in attempts.OrderBy(a => a.StartedUtc))
        {
            var classification = ProbeClassifier.ClassifyAttempt(attempt, _policy);
            if (classification.Eligibility == ProbeEvidenceEligibility.Eligible)
            {
                eligibleCount++;
            }

            if (classification.Domain == FailureDomain.FailedLocalSystem)
            {
                localFailureCount++;
            }
            else if (classification.Domain == FailureDomain.InternalError)
            {
                internalErrorCount++;
            }
        }

        var badAttempts = localFailureCount + internalErrorCount;

        // Invariant 63: SINGLE_PROBE_EXECUTION_FAILURE_NEVER_ESTABLISHES_PROBE_UNHEALTHINESS
        if (badAttempts == 0 && executedCount > 0)
        {
            _consecutiveLocalFailures = 0;
            if (_state is ProbeHealthState.Degraded or ProbeHealthState.Unusable or ProbeHealthState.Recovering)
            {
                _consecutiveSuccesses++;
                if (_consecutiveSuccesses >= _policy.RecoveryAttemptsRequired)
                {
                    _state = ProbeHealthState.Healthy;
                    reasons.Add($"Mehanizam probe se potpuno oporavio ({_consecutiveSuccesses} uzastopnih uspešnih ciklusa).");
                }
                else
                {
                    _state = ProbeHealthState.Recovering;
                    reasons.Add($"Mehanizam probe u fazi oporavka ({_consecutiveSuccesses}/{_policy.RecoveryAttemptsRequired}).");
                }
            }
            else
            {
                _state = ProbeHealthState.Healthy;
                reasons.Add("Mehanizam probe izvršava zahteve bez lokalnih grešaka.");
            }
        }
        else if (badAttempts > 0)
        {
            _consecutiveSuccesses = 0;
            _consecutiveLocalFailures += badAttempts;

            if (_consecutiveLocalFailures >= _policy.LocalFailuresToUnusable)
            {
                _state = ProbeHealthState.Unusable;
                reasons.Add($"Mehanizam probe neupotrebljiv usled {_consecutiveLocalFailures} uzastopnih lokalnih/internih grešaka.");
            }
            else if (_consecutiveLocalFailures >= _policy.LocalFailuresToDegrade)
            {
                _state = ProbeHealthState.Degraded;
                reasons.Add($"Mehanizam probe degradiran ({_consecutiveLocalFailures} zabeleženih lokalnih otkaza).");
            }
            else
            {
                reasons.Add($"Zabeležen izolovan lokalni otkaz ({badAttempts}), proba ostaje u stanju {_state}.");
            }
        }

        var snapshotId = $"phs-{_probe.ProbeType}-{_history.Count + 1}";
        var interpretationRefId = $"phealth:v{_policy.PolicyVersion}:{_policy.PolicyHash}";

        var snapshot = new ProbeHealthSnapshot(
            SnapshotId: snapshotId,
            Probe: _probe,
            HealthState: _state,
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            ExecutedAttemptCount: executedCount,
            EligibleAttemptCount: eligibleCount,
            LocalFailureCount: localFailureCount,
            InternalErrorCount: internalErrorCount,
            ReasonCodes: reasons,
            SourceEvidenceRefs: sourceEvidenceRefs,
            InterpretationRefId: interpretationRefId,
            EvaluatedAtUtc: windowEndUtc);

        _history.Add(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Deterministically rebuilds probe health history from persisted attempts.
    /// Invariant 66: PROBE_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE.
    /// </summary>
    public static IReadOnlyList<ProbeHealthSnapshot> RebuildHistory(
        ProbeIdentity probe,
        IEnumerable<ProbeExecutionAttempt> attempts,
        TimeSpan windowSize,
        ProbeFailurePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(attempts);

        var evaluator = new ProbeHealthEvaluator(probe, policy);
        var ordered = attempts.OrderBy(a => a.StartedUtc).ToList();
        if (ordered.Count == 0)
        {
            return evaluator.History;
        }

        var windowStart = ordered.First().StartedUtc;
        var currentBatch = new List<ProbeExecutionAttempt>();

        foreach (var attempt in ordered)
        {
            if (attempt.StartedUtc - windowStart >= windowSize && currentBatch.Count > 0)
            {
                evaluator.EvaluateWindow(currentBatch, windowStart, attempt.StartedUtc);
                currentBatch.Clear();
                windowStart = attempt.StartedUtc;
            }
            currentBatch.Add(attempt);
        }

        if (currentBatch.Count > 0)
        {
            evaluator.EvaluateWindow(currentBatch, windowStart, currentBatch.Last().CompletedUtc);
        }

        return evaluator.History;
    }
}
