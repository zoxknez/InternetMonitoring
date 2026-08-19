using IEM.Core.Quality;

namespace IEM.Core.Reports;

/// <summary>
/// Read-only snapshot of derived and evaluated analysis results fed into report generation.
/// Invariant 133: REPORT_MODEL_CONSUMES_ESTABLISHED_ANALYSIS_AND_NEVER_REINTERPRETS_RAW_EVIDENCE.
/// Invariant 150: REPORT_GENERATION_CAPTURES_AN_EXPLICIT_ANALYSIS_SNAPSHOT_AND_NEVER_IMPLICITLY_TRACKS_FUTURE_STATE.
/// </summary>
public sealed record EvidenceAnalysisSnapshot(
    string SessionRef,
    string AnalysisVersion,
    string InterpretationRefId,
    DateTimeOffset SessionStartUtc,
    DateTimeOffset SessionEndUtc,
    TimeSpan TotalDuration,
    TimeSpan ActiveMonitoringDuration,
    TimeSpan HostSuspensionDuration,
    IReadOnlyList<string> TargetsEvaluated,
    int TotalProbeAttempts,
    int OutagesObservedCount,
    string TargetHealthSummary,
    string ProbeHealthSummary,
    string ClockContinuitySummary,
    IReadOnlyList<EvidenceQualityAssessment> QualityAssessments,
    string PackageIntegrityState,
    string PackageTrustState,
    IReadOnlyList<ReportClaim> Claims,
    IReadOnlyList<string> SourceEvidenceRefs,
    DateTimeOffset GeneratedAtUtc);
