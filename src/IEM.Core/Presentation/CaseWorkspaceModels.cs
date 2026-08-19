namespace IEM.Core.Presentation;

/// <summary>
/// User-authored note or claim in the case workspace.
/// Invariants:
/// 164. USER_CASE_METADATA_AND_ANNOTATIONS_NEVER_MUTATE_SOURCE_EVIDENCE
/// 165. USER_AUTHORED_CASE_STATEMENT_IS_NEVER_PROMOTED_TO_EVIDENCE_CLAIM
/// </summary>
public sealed record UserStatement(
    string StatementId,
    string Text,
    DateTimeOffset CreatedAtUtc,
    string? Author = null);

/// <summary>
/// Case workspace metadata and user annotations completely isolated from source evidence packages.
/// Invariant 154: UI_STATE_IS_NEVER_EVIDENCE_STATE.
/// </summary>
public sealed record CaseWorkspaceState(
    string CaseId,
    string SourceSessionRef,
    string? OperatorName,
    string? ContractNumber,
    string? UserContact,
    IReadOnlyList<UserStatement> UserStatements,
    string SelectedProfileId);
