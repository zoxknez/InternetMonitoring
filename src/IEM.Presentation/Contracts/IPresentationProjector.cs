namespace IEM.Presentation.Contracts;

using IEM.Core.Presentation;
using IEM.Core.Reports;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;
using IEM.Presentation.States;

/// <summary>
/// Platform-neutral contract for projecting immutable domain snapshots into presentation states.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 152. LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS
/// 153. UI_VIEW_NEVER_MIXES_SEMANTIC_STATE_FROM_DIFFERENT_ANALYSIS_REVISIONS
/// 170. VISUAL_STYLE_NEVER_CHANGES_OR_COLLAPSES_SEMANTIC_STATE
/// </summary>
public interface IPresentationProjector
{
    MonitorPresentationState ProjectMonitor(PresentationSnapshot snapshot);

    EvidencePresentationState ProjectEvidence(PresentationSnapshot snapshot);

    CasePresentationState ProjectCase(
        PresentationSnapshot snapshot,
        ReportCompositionProfile profile,
        IReadOnlyList<UserStatementPresentationItem> userStatements,
        string operatorName,
        string contractNumber,
        string userContact);

    SpeedPresentationState ProjectSpeed(PresentationSnapshot snapshot);

    ShellPresentationState ProjectShell(
        PresentationSnapshot snapshot,
        ShellTab activeTab,
        DurationChoice selectedDuration,
        bool isRunning,
        string? fault);
}
