namespace IEM.Presentation.Contracts;

using IEM.Core.Presentation;
using IEM.Presentation.States;

/// <summary>
/// Platform-neutral contract for projecting domain snapshots into pure semantic presentation states.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 152. LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS
/// 153. UI_VIEW_NEVER_MIXES_SEMANTIC_STATE_FROM_DIFFERENT_ANALYSIS_REVISIONS
/// 162. UI_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY
/// 163. OVERALL_UI_QUALITY_NEVER_HIDES_CLAIM_SPECIFIC_QUALITY
/// 166. REPORT_PREVIEW_IS_A_READ_ONLY_PROJECTION_OF_THE_CANONICAL_REPORT_DOCUMENT_MODEL
/// 167. NON_EXECUTED_OR_REFUSED_SPEED_MEASUREMENT_IS_NEVER_RENDERED_AS_ZERO_THROUGHPUT
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public interface ISnapshotSemanticProjector
{
    MonitorPresentationState ProjectMonitor(PresentationSnapshot snapshot);

    EvidencePresentationState ProjectEvidence(PresentationSnapshot snapshot);

    SpeedPresentationState ProjectSpeed(SpeedProjectionInput input);

    CasePresentationState ProjectCase(PresentationSnapshot snapshot, CaseWorkspaceState workspace);
}
