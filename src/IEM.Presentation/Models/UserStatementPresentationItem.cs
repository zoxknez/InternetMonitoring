namespace IEM.Presentation.Models;

/// <summary>
/// Platform-neutral presentation item for user-authored workspace annotations.
/// Invariants:
/// 164. USER_CASE_METADATA_AND_ANNOTATIONS_NEVER_MUTATE_SOURCE_EVIDENCE
/// 165. USER_AUTHORED_CASE_STATEMENT_IS_NEVER_PROMOTED_TO_EVIDENCE_CLAIM
/// </summary>
public sealed record UserStatementPresentationItem(
    string StatementId,
    string Text,
    DateTimeOffset CreatedAtUtc,
    string Author);
