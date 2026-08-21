namespace IEM.Presentation.States;

using System.Collections.Immutable;
using IEM.Core.Reports;
using IEM.Presentation.Models;

/// <summary>
/// Platform-neutral, immutable presentation state for case metadata, user annotations, and report previews.
/// Invariants:
/// 164. USER_CASE_METADATA_AND_ANNOTATIONS_NEVER_MUTATE_SOURCE_EVIDENCE
/// 165. USER_AUTHORED_CASE_STATEMENT_IS_NEVER_PROMOTED_TO_EVIDENCE_CLAIM
/// 166. REPORT_PREVIEW_IS_A_READ_ONLY_PROJECTION_OF_THE_CANONICAL_REPORT_DOCUMENT_MODEL
/// </summary>
public sealed record CasePresentationState(
    string OperatorName,
    string ContractNumber,
    string UserContact,
    ReportCompositionProfile SelectedProfile,
    string PreviewText,
    ImmutableArray<UserStatementPresentationItem> UserStatements)
{
    public static CasePresentationState Initial { get; } = new(
        OperatorName: string.Empty,
        ContractNumber: string.Empty,
        UserContact: string.Empty,
        SelectedProfile: ReportCompositionProfile.Complaint,
        PreviewText: "Dokument još uvek nije dostupan (čekanje na podatke sesije).",
        UserStatements: ImmutableArray<UserStatementPresentationItem>.Empty);
}
