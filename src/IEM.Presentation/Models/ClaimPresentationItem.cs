namespace IEM.Presentation.Models;

using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral presentation item for an individual evidence claim.
/// Invariants:
/// 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
/// 163. OVERALL_UI_QUALITY_NEVER_HIDES_CLAIM_SPECIFIC_QUALITY
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public sealed record ClaimPresentationItem(
    string ClaimId,
    string StatementKey,
    BadgeKind EpistemicBadge,
    string EpistemicLabel,
    string ValueText,
    string SupportState,
    BadgeKind QualityBadge,
    string? QualityAssessmentRef);
