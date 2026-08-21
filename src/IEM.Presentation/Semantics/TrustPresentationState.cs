namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral representation of evidence trust / legal authority status.
/// Invariants:
/// 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public enum TrustPresentationState
{
    Unknown,
    Established,
    NotEstablished,
    NotApplicable
}
