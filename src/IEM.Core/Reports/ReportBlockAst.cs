using IEM.Core.Quality;

namespace IEM.Core.Reports;

/// <summary>
/// Abstract base record for all semantic blocks in the Report AST.
/// Invariant 135: CANONICAL_REPORT_MODEL_CONTAINS_SEMANTIC_BLOCKS_NOT_RENDERER_MARKUP.
/// </summary>
public abstract record ReportBlock
{
    public ContentSensitivity Sensitivity { get; init; } = ContentSensitivity.Public;
}

public sealed record HeadingBlock(int Level, string Text) : ReportBlock;

public sealed record ParagraphBlock(string Text, IReadOnlyList<string>? HighlightedTerms = null) : ReportBlock;

public sealed record ClaimBlock(ReportClaim Claim) : ReportBlock;

public sealed record MetricBlock(string Label, ReportValue Value, string? MeaningRef = null) : ReportBlock;

public sealed record ReportTableCell(ReportValue Value, ContentSensitivity Sensitivity = ContentSensitivity.Public);

public sealed record TableBlock(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<ReportTableCell>> Rows) : ReportBlock;

public enum TimelineEntryCategory
{
    ActiveMonitoring,
    InterruptionObserved,
    HostSuspended,
    ClockAdjustment,
    BootBoundary,
    ProbeDegraded,
}

public sealed record ReportTimelineEntry(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    TimelineEntryCategory Category,
    string Description,
    string? EpistemicClass = null,
    string? QualityRef = null);

public sealed record TimelineBlock(IReadOnlyList<ReportTimelineEntry> Entries) : ReportBlock;

public enum NoticeKind
{
    Info,
    Warning,
    LegalNotice,
}

public sealed record NoticeBlock(NoticeKind Kind, string Message) : ReportBlock;

public sealed record QualityBadgeBlock(
    EvidenceQualityBand Band,
    string Purpose,
    string SummaryReason) : ReportBlock;

public sealed record IntegrityNoticeBlock(
    string IntegrityState,
    string TrustState,
    string? KeyId,
    string? TimestampAuthority) : ReportBlock;

public sealed record PageBreakBlock() : ReportBlock;
