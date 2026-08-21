namespace IEM.Presentation.Contracts;

using IEM.Presentation.States;

/// <summary>
/// Platform-neutral composition contract for deterministic presentation state projection.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 152. LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS
/// 153. UI_VIEW_NEVER_MIXES_SEMANTIC_STATE_FROM_DIFFERENT_ANALYSIS_REVISIONS
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public interface IPresentationProjector : ISnapshotSemanticProjector
{
    ShellPresentationState ProjectShell(ShellProjectionInput input);
}
