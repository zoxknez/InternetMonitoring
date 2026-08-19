using IEM.Core.Probes;

namespace IEM.Core.Health;

/// <summary>
/// Stable identity of a target measurement endpoint.
/// Invariants:
/// 37. PROBE_RESULT_PRESERVES_TARGET_AND_ADDRESS_FAMILY
/// 45. TARGET_HEALTH_IS_SCOPED_TO_ENDPOINT_AND_ADDRESS_FAMILY
/// </summary>
public sealed record TargetIdentity(
    string TargetId,
    string LogicalName,
    string EndpointAddress,
    TargetAddressFamily AddressFamily,
    TargetProbeType ProbeType = TargetProbeType.IcmpEcho)
{
    public string UniqueKey => $"{TargetId}:{AddressFamily}:{EndpointAddress}";
}

/// <summary>
/// Capability status of a target.
/// Invariant 39: ABSENCE_OF_REPLY_NEVER_PROVES_TARGET_CAPABILITY.
/// </summary>
public enum TargetCapabilityState
{
    /// <summary>No evidence has been gathered yet.</summary>
    Unknown,

    /// <summary>At least one successful probe reply has been observed historically.</summary>
    ResponseObserved,

    /// <summary>
    /// Probes were executed, but no response has been observed so far.
    /// Does NOT assert that target does not support ICMP (may be path filtering or local route).
    /// </summary>
    ResponseNotYetObserved,
}

/// <summary>
/// Inferred health state of the target based on recent evaluation windows.
/// </summary>
public enum TargetHealthState
{
    /// <summary>Initial state before sufficient evaluation windows are processed.</summary>
    Unknown,

    /// <summary>Target is responding reliably within expected error thresholds.</summary>
    Healthy,

    /// <summary>Target is exhibiting elevated loss ratio across multiple windows.</summary>
    Degraded,

    /// <summary>Target is completely silent across sustained evaluation windows while peers respond.</summary>
    Unresponsive,

    /// <summary>Previously degraded or unresponsive target is demonstrating sustained recovery.</summary>
    Recovering,
}

/// <summary>
/// Weighting contribution of the target in aggregate classification verdicts.
/// Invariant 41: TARGET_HEALTH_CHANGE_NEVER_RETROACTIVELY_REWEIGHTS_HISTORY.
/// Invariant 42: TARGET_EXCLUSION_IS_ALWAYS_VISIBLE_AND_REASONED.
/// </summary>
public enum EvidenceContribution
{
    /// <summary>Full weight (1.0) in network state classification.</summary>
    Full,

    /// <summary>Reduced weight (0.5) due to degraded response history.</summary>
    Reduced,

    /// <summary>
    /// Suspended weight (0.0). The target is not used to declare an outage,
    /// but remains visible in the evidence with its explicit reason code.
    /// </summary>
    Suspended,
}

/// <summary>
/// Contextual health state of peer targets evaluated within the same time window.
/// Invariant 43: SHARED_FAILURE_NEVER_BECOMES_TARGET_FAILURE_BY_DEFAULT.
/// </summary>
public sealed record PeerContext(
    int TotalPeers,
    int RespondingPeers,
    int FailingPeers)
{
    /// <summary>
    /// True if multiple or majority of peers are failing simultaneously,
    /// indicating a shared network/local failure rather than an isolated target issue.
    /// </summary>
    public bool IsSharedNetworkFailure => TotalPeers > 1 && FailingPeers >= (TotalPeers + 1) / 2;
}

/// <summary>
/// Immutable snapshot of target health assessment at a specific point in time.
/// Invariants:
/// 40. TARGET_HEALTH_NEVER_REWRITES_PRIOR_EVIDENCE
/// 41. TARGET_HEALTH_CHANGE_NEVER_RETROACTIVELY_REWEIGHTS_HISTORY
/// 42. TARGET_EXCLUSION_IS_ALWAYS_VISIBLE_AND_REASONED
/// 44. TARGET_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE
/// </summary>
public sealed record TargetHealthSnapshot(
    string SnapshotId,
    TargetIdentity Target,
    TargetHealthState State,
    TargetCapabilityState Capability,
    EvidenceContribution Contribution,
    double Weight,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> SourceEvidenceRefs,
    string InterpretationRefId,
    DateTimeOffset EvaluatedAtUtc,
    PeerContext? PeerContext = null)
{
    public bool IsActiveInEvidence => Contribution != EvidenceContribution.Suspended;
}
