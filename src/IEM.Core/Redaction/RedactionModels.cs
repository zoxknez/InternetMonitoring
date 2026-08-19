namespace IEM.Core.Redaction;

public enum RedactionAction
{
    Preserved,
    Masked,
    Removed,
    Generalized,
}

public enum RedactionFieldKind
{
    NetworkSsid,
    NetworkBssid,
    MacAddress,
    HostName,
    UserName,
    LocalPath,
    PrivateIpAddress,
    PublicIpAddress,
    Geolocation,
    UserContractMetadata,
    UserCustomNotes,
    OtherSensitive,
}

/// <summary>
/// Individual auditable redaction action performed on a field.
/// Invariant 182: REDACTION_METADATA_NEVER_REVEALS_THE_REDACTED_VALUE.
/// </summary>
public sealed record RedactionEntry(
    string TargetPath,
    string FieldPath,
    RedactionFieldKind FieldKind,
    RedactionAction Action,
    string Reason,
    string? FieldHashBefore = null);

/// <summary>
/// Auditable manifest of all redactions applied to derive a privacy-safe package.
/// Invariants:
/// 175. REDACTED_PACKAGE_IS_ALWAYS_EXPLICITLY_DERIVED
/// 176. REDACTED_PACKAGE_ALWAYS_BINDS_TO_THE_ORIGINAL_MANIFEST_HASH
/// 186. REDACTION_SCOPE_IS_EXPLICIT_AND_AUDITABLE
/// 189. REDACTION_CHAIN_NEVER_LOSES_PROVENANCE_TO_THE_CANONICAL_SOURCE
/// </summary>
public sealed record RedactionManifest(
    int SchemaVersion,
    string PackageId,
    string OriginalSessionId,
    string OriginalManifestSha256,
    string RedactionPolicyId,
    int RedactionPolicyVersion,
    string RedactionPolicyHash,
    DateTimeOffset RedactedAtUtc,
    IReadOnlyList<RedactionEntry> RedactedEntries,
    IReadOnlyDictionary<string, string> RedactedFileHashes);

public enum RedactedVerificationStatus
{
    ValidRedactedDerivative,
    OriginalManifestMismatch,
    RedactionPolicyMismatch,
    RedactedContentTampered,
    SignatureInvalid,
    UnsupportedRedactionPolicyVersion,
}

/// <summary>
/// Verification result for a redacted evidence package.
/// Invariant 188: REDACTED_DERIVATIVE_HAS_ITS_OWN_INTEGRITY_IDENTITY_AND_SIGNATURE.
/// </summary>
public sealed record RedactedVerificationResult(
    RedactedVerificationStatus Status,
    string OriginalManifestSha256,
    string DerivedManifestSha256,
    string PolicyHash,
    IReadOnlyList<string> Discrepancies);
