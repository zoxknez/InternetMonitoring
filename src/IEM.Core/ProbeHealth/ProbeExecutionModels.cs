using IEM.Core.Probes;

namespace IEM.Core.ProbeHealth;

public enum ExecutionStage
{
    Preparation,
    NameResolution,
    SocketCreation,
    Bind,
    RouteResolution,
    Connect,
    Send,
    Receive,
    ProtocolValidation,
    Completion,
}

public enum ProbeRawOutcome
{
    Success,
    NoResponseBeforeDeadline,
    NativeOperationFailed,
    ProtocolResponseReceived,
    ProtocolNegativeResponseReceived,
    Cancelled,
}

/// <summary>
/// Operational factual record of a probe execution attempt (FACT).
/// Invariants:
/// 55. LOCAL_EXECUTION_FAILURE_IS_NEVER_REPORTED_AS_NETWORK_FAILURE
/// 58. NATIVE_ERROR_CODE_IS_EVIDENCE_INPUT_NOT_FINAL_SEMANTIC_CLASSIFICATION
/// </summary>
public sealed record ProbeExecutionAttempt(
    string AttemptId,
    ProbeIdentity Probe,
    string TargetRef,
    TargetAddressFamily AddressFamily,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    ExecutionStage Stage,
    ProbeRawOutcome RawOutcome,
    string? NativeErrorDomain = null,
    int? NativeErrorCode = null,
    string? NativeErrorName = null,
    int? TimeoutConfiguredMs = null,
    string? DiagnosticMessage = null,
    IReadOnlyList<string>? SourceContextRefs = null);

/// <summary>
/// Semantically classified failure domain of a probe attempt (INFERENCE).
/// Invariants:
/// 56. AMBIGUOUS_PROBE_FAILURE_REMAINS_UNKNOWN
/// 57. TIMEOUT_DESCRIBES_OBSERVED_NON_COMPLETION_NOT_FAILURE_CAUSE
/// 59. INTERNAL_PROBE_ERROR_NEVER_CONTRIBUTES_NETWORK_FAILURE_EVIDENCE
/// 60. REMOTE_FAILURE_REQUIRES_POSITIVE_REMOTE_OR_PROTOCOL_FAILURE_EVIDENCE
/// 61. NETWORK_FAILURE_CLASSIFICATION_NEVER_IDENTIFIES_UNPROVEN_ROOT_CAUSE
/// </summary>
public enum FailureDomain
{
    None,
    FailedNetwork,
    FailedRemote,
    FailedLocalSystem,
    InternalError,
    Timeout,
    Unknown,
}

/// <summary>
/// Explicit eligibility of the probe outcome to contribute to network evidence.
/// Invariant 62: PROBE_EXECUTION_ELIGIBILITY_IS_EXPLICIT_NOT_IMPLICIT.
/// </summary>
public enum ProbeEvidenceEligibility
{
    /// <summary>Fully eligible to contribute to network health and loss assessments.</summary>
    Eligible,

    /// <summary>Limited contribution (e.g. timeout without proved drop point, or explicit error).</summary>
    Limited,

    /// <summary>Ineligible for network assessment (local socket/system/internal errors).</summary>
    Ineligible,
}

/// <summary>
/// Inferred failure classification and eligibility (INFERENCE).
/// </summary>
public sealed record ProbeFailureClassification(
    string AttemptId,
    FailureDomain Domain,
    ProbeEvidenceEligibility Eligibility,
    string ClassificationReasonCode,
    IReadOnlyList<string> SourceEvidenceRefs,
    string InterpretationRefId,
    string PolicyRefId,
    string ConfidenceBasis);

/// <summary>
/// Inferred health state of the probe execution engine across a window.
/// Invariants:
/// 63. SINGLE_PROBE_EXECUTION_FAILURE_NEVER_ESTABLISHES_PROBE_UNHEALTHINESS
/// 65. PROBE_HEALTH_NEVER_REWRITES_EXECUTION_EVIDENCE
/// </summary>
public enum ProbeHealthState
{
    Unknown,
    Healthy,
    Degraded,
    Unusable,
    Recovering,
}

/// <summary>
/// Immutable snapshot of probe execution engine health (ASSESSMENT).
/// Invariant 66: PROBE_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE.
/// </summary>
public sealed record ProbeHealthSnapshot(
    string SnapshotId,
    ProbeIdentity Probe,
    ProbeHealthState HealthState,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    int ExecutedAttemptCount,
    int EligibleAttemptCount,
    int LocalFailureCount,
    int InternalErrorCount,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> SourceEvidenceRefs,
    string InterpretationRefId,
    DateTimeOffset EvaluatedAtUtc);
