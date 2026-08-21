namespace IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral representation of measurement quality bands.
/// Invariants:
/// 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
/// 163. OVERALL_UI_QUALITY_NEVER_HIDES_CLAIM_SPECIFIC_QUALITY
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public enum QualityPresentationBand
{
    Unknown,
    Strong,
    Moderate,
    Limited,
    Insufficient
}
