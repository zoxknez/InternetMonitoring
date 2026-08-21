namespace IEM.Presentation.States;

using IEM.Presentation.Models;
using IEM.Presentation.Semantics;

/// <summary>
/// Platform-neutral, immutable presentation state for distinct Integrity, Trust, and Quality breakdowns.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
/// 163. OVERALL_UI_QUALITY_NEVER_HIDES_CLAIM_SPECIFIC_QUALITY
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public sealed record EvidencePresentationState(
    BadgeKind OverallQualityBand,
    string OverallQualityText,
    BadgeKind IntegrityState,
    string IntegrityLabel,
    BadgeKind TrustState,
    string TrustLabel,
    string PackageVerificationSummary,
    SemanticTone Tone,
    IReadOnlyList<ClaimPresentationItem> Claims)
{
    public static EvidencePresentationState Initial { get; } = new(
        OverallQualityBand: BadgeKind.Unknown,
        OverallQualityText: "Nepoznato",
        IntegrityState: BadgeKind.Unknown,
        IntegrityLabel: "Nepoznato",
        TrustState: BadgeKind.Unknown,
        TrustLabel: "Nepoznato",
        PackageVerificationSummary: "Nema aktivnog dokaznog paketa.",
        Tone: SemanticTone.Unknown,
        Claims: Array.Empty<ClaimPresentationItem>());
}
