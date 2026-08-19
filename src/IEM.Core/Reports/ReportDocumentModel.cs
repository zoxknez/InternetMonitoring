using System.Security.Cryptography;
using System.Text;
using IEM.Core.Quality;

namespace IEM.Core.Reports;

public enum DocumentPurpose
{
    EvidenceReport,
    TechnicalReport,
    Complaint,
    RegulatorySubmission,
    ExportSummary,
}

/// <summary>
/// Composition profile defining required sections and rules without altering underlying claim semantics.
/// Invariant 134: DOCUMENT_PURPOSE_MAY_CHANGE_COMPOSITION_BUT_NEVER_EVIDENCE_SEMANTICS.
/// </summary>
public sealed record ReportCompositionProfile
{
    public string ProfileId { get; init; } = "DefaultTechnical";
    public DocumentPurpose Purpose { get; init; } = DocumentPurpose.TechnicalReport;
    public IReadOnlyList<string> RequiredSectionKinds { get; init; } = Array.Empty<string>();
    public int ProfileVersion { get; init; } = 1;

    public string ProfileHash => ComputeProfileHash();

    private string ComputeProfileHash()
    {
        var desc = $"id={ProfileId};p={Purpose};v={ProfileVersion};req={string.Join(",", RequiredSectionKinds)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(desc)));
    }

    public static readonly ReportCompositionProfile Technical = new()
    {
        ProfileId = "TechnicalReport-v1",
        Purpose = DocumentPurpose.TechnicalReport,
        RequiredSectionKinds = new[] { "Summary", "Integrity", "Metrics", "Targets", "Timeline", "Claims" },
    };

    public static readonly ReportCompositionProfile Complaint = new()
    {
        ProfileId = "ComplaintProfile-SR-v1",
        Purpose = DocumentPurpose.Complaint,
        RequiredSectionKinds = new[] { "Summary", "DisruptionFacts", "EvidenceQuality", "SignOff" },
    };

    public static readonly ReportCompositionProfile Ratel = new()
    {
        ProfileId = "RatelProfile-SR-v1",
        Purpose = DocumentPurpose.RegulatorySubmission,
        RequiredSectionKinds = new[] { "RegulatoryHeader", "Summary", "MeasurementProtocol", "Disruptions", "IntegrityProof" },
    };
}

public sealed record ReportProvenance(
    string SourceSessionId,
    string SourceAnalysisRef,
    int DocumentModelSchemaVersion,
    string CompositionProfileRef,
    IReadOnlyList<string> InterpretationRefs,
    IReadOnlyList<string> QualityPolicyRefs);

public sealed record ReportSection(
    string SectionId,
    string Title,
    IReadOnlyList<ReportBlock> Blocks,
    ContentSensitivity Sensitivity = ContentSensitivity.Public);

/// <summary>
/// Canonical immutable representation of a generated report document.
/// Invariants 132-150.
/// </summary>
public sealed record ReportDocumentModel(
    int DocumentSchemaVersion,
    string DocumentId,
    string SessionRef,
    DocumentPurpose DocumentPurpose,
    string Language,
    DateTimeOffset GeneratedAtUtc,
    string Title,
    string? Subtitle,
    string Summary,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyList<string> EvidenceReferences,
    EvidenceQualityBand OverallQualityBand,
    string IntegrityState,
    string TrustState,
    IReadOnlyList<string> InterpretationRefs,
    IReadOnlyList<string> PolicyRefs,
    ReportProvenance Provenance);
