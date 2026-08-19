namespace IEM.Core.Ipc;

/// <summary>
/// Authoritative audit record of an evidence-affecting control command (FACT).
/// Invariant 93: EVIDENCE_AFFECTING_CONTROL_ACTIONS_ARE_AUDITABLE.
/// </summary>
public sealed record ControlCommandObserved(
    string CommandEventId,
    string RequestId,
    string CommandName,
    string? SessionId,
    string PeerIdentityRef,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string AuthorizationDecisionRef,
    string Outcome,
    string? FailureCode = null);
