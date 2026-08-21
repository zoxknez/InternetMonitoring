namespace IEM.Presentation.States;

using System.Collections.Immutable;
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
    QualityPresentationBand OverallQualityBand,
    string OverallQualityText,
    IntegrityPresentationState IntegrityState,
    string IntegrityLabel,
    TrustPresentationState TrustState,
    string TrustLabel,
    string PackageVerificationSummary,
    SemanticTone Tone,
    ImmutableArray<ClaimPresentationItem> Claims)
{
    public static EvidencePresentationState Initial { get; } = new(
        OverallQualityBand: QualityPresentationBand.Unknown,
        OverallQualityText: "Nepoznato",
        IntegrityState: IntegrityPresentationState.Unknown,
        IntegrityLabel: "Nepoznato",
        TrustState: TrustPresentationState.Unknown,
        TrustLabel: "Nepoznato",
        PackageVerificationSummary: "Nema aktivnog dokaznog paketa.",
        Tone: SemanticTone.Unknown,
        Claims: ImmutableArray<ClaimPresentationItem>.Empty);
}
