namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral visual badge taxonomy for epistemic status, integrity, trust, and quality bands.
/// Invariant 170: VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE.
/// </summary>
public enum BadgeKind
{
    // Epistemic classes
    Fact,
    Inference,
    Assessment,

    // Package Integrity states
    Verified,
    Incomplete,
    Invalid,

    // Package Trust states
    Established,
    NotEstablished,
    NotApplicable,

    // Evidence Quality bands
    Strong,
    Moderate,
    Limited,
    Insufficient,

    // Unset or non-evaluable
    Unknown
}
