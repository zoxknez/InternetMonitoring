using IEM.Core.Reports;

namespace IEM.Core.Presentation;

public enum ServiceConnectionStatus
{
    Connected,
    Connecting,
    Disconnected,
    ServiceUnavailable,
}

public enum SessionRuntimeState
{
    Idle,
    Starting,
    Monitoring,
    Sealing,
    Sealed,
    Faulted,
}

/// <summary>
/// Immutable, atomic snapshot of all presentation-ready evidence, analysis, and runtime states.
/// Invariants:
/// 151. UI_NEVER_CREATES_OR_REINTERPRETS_EVIDENCE_SEMANTICS
/// 152. LIVE_UI_CONSUMES_IMMUTABLE_VERSIONED_PRESENTATION_SNAPSHOTS
/// 153. UI_VIEW_NEVER_MIXES_SEMANTIC_STATE_FROM_DIFFERENT_ANALYSIS_REVISIONS
/// </summary>
public sealed record PresentationSnapshot(
    string SnapshotId,
    string SessionId,
    long AnalysisRevision,
    DateTimeOffset CapturedAtUtc,
    SessionRuntimeState RuntimeState,
    ServiceConnectionStatus ServiceStatus,
    EvidenceAnalysisSnapshot? Analysis,
    ReportDocumentModel? CanonicalReport,
    IReadOnlyList<string> SourceRefs);
