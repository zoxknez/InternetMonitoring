using IEM.Core.Probes;

namespace IEM.Core.Gateway;

public enum GatewayCapabilityKind
{
    /// <summary>ICMP Echo Reply from gateway endpoint.</summary>
    IcmpEcho,

    /// <summary>
    /// Link-layer neighbor entry resolution (ARP for IPv4, NDP for IPv6).
    /// Invariant 50: NEIGHBOR_RESOLUTION_NEVER_PROVES_GATEWAY_FORWARDING.
    /// </summary>
    NeighborResolution,

    /// <summary>
    /// OS default route table presence for this gateway.
    /// Invariant 51: ROUTE_PRESENCE_NEVER_PROVES_GATEWAY_REACHABILITY.
    /// </summary>
    RoutePresence,

    /// <summary>Local management endpoint reply (e.g. HTTP router UI or UPnP) when explicitly configured.</summary>
    ManagementResponse,
}

public enum ObservationMethod
{
    IcmpPing,
    ArpLookup,
    NdpLookup,
    DefaultRouteCheck,
    HttpManagementProbe,
}

public enum ObservationOutcome
{
    Success,
    Timeout,
    DestinationUnreachable,
    LocalExecutionFailure,
    NotConfigured,
}

/// <summary>
/// Raw observation of a specific gateway capability attempt (FACT).
/// </summary>
public sealed record GatewayCapabilityObservation(
    string ObservationId,
    string GatewayId,
    GatewayCapabilityKind CapabilityKind,
    ObservationMethod Method,
    DateTimeOffset AttemptedAtUtc,
    ObservationOutcome Outcome,
    string InterfaceId,
    TargetAddressFamily AddressFamily,
    IReadOnlyList<string> SourceEvidenceRefs,
    string? DiagnosticMessage = null)
{
    public bool IsPositiveEvidence => Outcome == ObservationOutcome.Success;
}

/// <summary>
/// State of capability evidence learned from observations.
/// Invariants:
/// 46. ABSENCE_OF_GATEWAY_RESPONSE_NEVER_PROVES_UNSUPPORTED_CAPABILITY
/// 47. OBSERVED_GATEWAY_CAPABILITY_IS_ESTABLISHED_ONLY_BY_POSITIVE_EVIDENCE
/// </summary>
public enum CapabilityEvidenceState
{
    /// <summary>No conclusive evidence gathered yet.</summary>
    Unknown,

    /// <summary>Observed to be supported and working recently via positive evidence.</summary>
    ObservedSupported,

    /// <summary>Observed to be supported in the past, but currently failing or silent.</summary>
    PreviouslyObserved,

    /// <summary>
    /// Probes executed, but no reply observed yet.
    /// Never marked as 'Unsupported' (Invariant 46).
    /// </summary>
    ResponseNotYetObserved,

    /// <summary>Capability probe is optional/opt-in and not configured.</summary>
    NotAssessed,
}

/// <summary>
/// State of a single capability for a gateway identity.
/// </summary>
public sealed record GatewayCapabilityState(
    GatewayCapabilityKind Kind,
    CapabilityEvidenceState EvidenceState,
    DateTimeOffset? FirstObservedUtc,
    DateTimeOffset? LastObservedUtc,
    int SuccessfulObservationCount,
    int EligibleAttemptCount);

/// <summary>
/// Complete learned capability profile for a gateway identity (INFERENCE).
/// Invariants:
/// 48. GATEWAY_CAPABILITY_HISTORY_IS_APPEND_ONLY
/// 49. CURRENT_GATEWAY_BEHAVIOR_NEVER_REWRITES_PRIOR_CAPABILITY_EVIDENCE
/// </summary>
public sealed record GatewayCapabilityProfile(
    GatewayIdentity Gateway,
    IReadOnlyList<GatewayCapabilityState> Capabilities,
    DateTimeOffset LearnedFromUtc,
    DateTimeOffset LearnedThroughUtc,
    IReadOnlyList<string> SourceEvidenceRefs,
    string InterpretationRefId,
    string PolicyRefId,
    int ProfileVersion);

/// <summary>
/// High-level behavioral assessment of the gateway at an evaluation moment.
/// </summary>
public enum GatewayBehaviorState
{
    Unknown,
    NormallyResponding,
    ResponseDegraded,
    PreviouslyObservedCapabilityMissing,
    Recovering,
}

/// <summary>
/// Immutable snapshot of gateway assessment (ASSESSMENT).
/// Invariant 54: GATEWAY_CAPABILITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE.
/// </summary>
public sealed record GatewayAssessmentSnapshot(
    string SnapshotId,
    GatewayIdentity Gateway,
    GatewayBehaviorState BehaviorState,
    IReadOnlyList<GatewayCapabilityState> CapabilityStates,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> SourceEvidenceRefs,
    string InterpretationRefId,
    DateTimeOffset EvaluatedAtUtc);
