namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral representation of package integrity status.
/// Invariants:
/// 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public enum IntegrityPresentationState
{
    Unknown,
    Verified,
    Incomplete,
    Invalid
}
