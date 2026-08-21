namespace IEM.Presentation.Contracts;

using System.Collections.Immutable;
using IEM.Core.Reports;
using IEM.Presentation.Models;

/// <summary>
/// User case workspace input state provided explicitly without disk/journal file reads inside projectors.
/// Invariants:
/// 164. USER_CASE_METADATA_AND_ANNOTATIONS_NEVER_MUTATE_SOURCE_EVIDENCE
/// 165. USER_AUTHORED_CASE_STATEMENT_IS_NEVER_PROMOTED_TO_EVIDENCE_CLAIM
/// </summary>
public sealed record CaseWorkspaceState(
    string OperatorName,
    string ContractNumber,
    string UserContact,
    ReportCompositionProfile SelectedProfile,
    ImmutableArray<UserStatementPresentationItem> UserStatements,
    string? CaseJournalText)
{
    public static CaseWorkspaceState Empty { get; } = new(
        OperatorName: string.Empty,
        ContractNumber: string.Empty,
        UserContact: string.Empty,
        SelectedProfile: ReportCompositionProfile.Complaint,
        UserStatements: ImmutableArray<UserStatementPresentationItem>.Empty,
        CaseJournalText: null);
}
