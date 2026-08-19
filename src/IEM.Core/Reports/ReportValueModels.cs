using System.Globalization;

namespace IEM.Core.Reports;

public enum EpistemicClass
{
    Fact,
    Inference,
    Assessment,
}

public enum ClaimSupportState
{
    Supported,
    Limited,
    Unknown,
    NotApplicable,
}

public enum ContentSensitivity
{
    Public,
    PotentiallyIdentifying,
    NetworkIdentifier,
    DeviceIdentifier,
    SensitiveOperational,
}

public enum ReportValueKind
{
    Text,
    Numeric,
    Integer,
    Duration,
    Timestamp,
    Unknown,
}

/// <summary>
/// Strongly-typed semantic value preserving original numerics/types across localization and formatting.
/// Invariants:
/// 137. LOCALIZATION_AND_FORMATTING_NEVER_CHANGE_REPORT_VALUE_SEMANTICS
/// 138. UNKNOWN_REPORT_VALUE_IS_NEVER_REPLACED_BY_ZERO_EMPTY_OR_INFERRED_TEXT
/// </summary>
public sealed record ReportValue
{
    public ReportValueKind Kind { get; init; }
    public string? TextValue { get; init; }
    public double? NumericValue { get; init; }
    public long? IntegerValue { get; init; }
    public TimeSpan? DurationValue { get; init; }
    public DateTimeOffset? TimestampValue { get; init; }
    public string? Unit { get; init; }

    public static ReportValue FromText(string text) =>
        new() { Kind = ReportValueKind.Text, TextValue = text };

    public static ReportValue FromNumeric(double value, string? unit = null) =>
        new() { Kind = ReportValueKind.Numeric, NumericValue = value, Unit = unit };

    public static ReportValue FromInteger(long value, string? unit = null) =>
        new() { Kind = ReportValueKind.Integer, IntegerValue = value, Unit = unit };

    public static ReportValue FromDuration(TimeSpan duration) =>
        new() { Kind = ReportValueKind.Duration, DurationValue = duration };

    public static ReportValue FromTimestamp(DateTimeOffset timestamp) =>
        new() { Kind = ReportValueKind.Timestamp, TimestampValue = timestamp };

    public static ReportValue Unknown(string? unit = null) =>
        new() { Kind = ReportValueKind.Unknown, Unit = unit };

    public string Format(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        return Kind switch
        {
            ReportValueKind.Text => TextValue ?? string.Empty,
            ReportValueKind.Numeric => Unit != null ? $"{NumericValue?.ToString("N2", culture)} {Unit}" : NumericValue?.ToString("N2", culture) ?? "Unknown",
            ReportValueKind.Integer => Unit != null ? $"{IntegerValue?.ToString("N0", culture)} {Unit}" : IntegerValue?.ToString("N0", culture) ?? "Unknown",
            ReportValueKind.Duration => DurationValue.HasValue ? FormatDuration(DurationValue.Value, culture) : "Unknown",
            ReportValueKind.Timestamp => TimestampValue?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", culture) ?? "Unknown",
            ReportValueKind.Unknown => "Unknown",
            _ => "Unknown",
        };
    }

    private static string FormatDuration(TimeSpan d, CultureInfo culture)
    {
        if (d.TotalHours >= 1)
        {
            return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";
        }
        if (d.TotalMinutes >= 1)
        {
            return $"{(int)d.TotalMinutes}m {d.Seconds}s";
        }
        return $"{d.TotalSeconds.ToString("F1", culture)}s";
    }
}

/// <summary>
/// Fully traceable evidentiary claim in the report document model.
/// Invariants:
/// 136. EVERY_EVIDENTIARY_REPORT_CLAIM_PRESERVES_ITS_EPISTEMIC_CLASS_AND_PROVENANCE
/// 144. NARRATIVE_TEMPLATE_NEVER_STRENGTHENS_THE_UNDERLYING_CLAIM
/// </summary>
public sealed record ReportClaim(
    string ClaimId,
    string ClaimKind,
    string StatementKey,
    ReportValue? StructuredValue,
    EpistemicClass EpistemicClass,
    ClaimSupportState SupportState,
    IReadOnlyList<string> SourceEvidenceRefs,
    IReadOnlyList<string> ReasonCodes,
    string? InterpretationRefId = null,
    string? QualityAssessmentRef = null,
    string? TimeContextRef = null,
    ContentSensitivity Sensitivity = ContentSensitivity.Public);
