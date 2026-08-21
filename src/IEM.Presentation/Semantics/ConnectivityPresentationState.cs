namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral representation of network connectivity observation state.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 159. UNKNOWN_UI_VALUE_NEVER_BECOMES_ZERO_SUCCESS_FAILURE_OR_UNSUPPORTED
/// </summary>
public enum ConnectivityPresentationState
{
    Unknown,
    Online,
    Degraded,
    Outage
}
