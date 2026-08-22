namespace IEM.Presentation.Contracts;

using IEM.Core.Presentation;

/// <summary>
/// Exhaustive, explicit projection input for deterministic top-level Shell presentation state synthesis.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 152. LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS
/// 153. UI_VIEW_NEVER_MIXES_SEMANTIC_STATE_FROM_DIFFERENT_ANALYSIS_REVISIONS
/// </summary>
public sealed record ShellProjectionInput(
    PresentationSnapshot Snapshot,
    ShellInteractionState Interaction,
    HostPresentationFacts HostFacts,
    HistoryPresentationState History,
    UpdatePresentationState Update,
    CaseWorkspaceState CaseWorkspace,
    SpeedExecutionFacts SpeedFacts);
