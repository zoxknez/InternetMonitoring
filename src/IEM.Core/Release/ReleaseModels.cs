namespace IEM.Core.Release;

/// <summary>
/// Canonical release identity shared across all distributed artifacts.
/// Invariants:
/// 191. RELEASE_ARTIFACT_IDENTITY_IS_EXPLICIT_AND_VERSION_BOUND
/// 192. ALL_ARTIFACTS_OF_ONE_RELEASE_SHARE_ONE_CANONICAL_RELEASE_IDENTITY
/// 193. RELEASE_IDENTITY_NEVER_CHANGES_AFTER_ARTIFACT_SIGNING
/// 207. SERVICE_AND_APPLICATION_RELEASE_VERSIONS_NEVER_SILENTLY_DIVERGE
/// </summary>
public sealed record ReleaseIdentity(
    string ProductVersion,
    string InformationalVersion,
    string GitCommit,
    string BuildId,
    string BuildConfiguration,
    DateTimeOffset BuildTimestampUtc,
    string ReleaseChannel,
    string Architecture,
    int ReleaseManifestVersion = 1);

/// <summary>
/// Authenticode signature metadata and validation state for an executable artifact.
/// Invariants:
/// 194. UNSIGNED_REQUIRED_EXECUTABLE_IS_NEVER_RELEASED
/// 195. AUTHENTICODE_SIGNATURE_IS_VERIFIED_BEFORE_RELEASE_ACCEPTANCE
/// 197. TIMESTAMP_FAILURE_NEVER_SILENTLY_DEGRADES_TO_UNTIMESTAMPED_RELEASE
/// </summary>
public sealed record AuthenticodeSignatureState(
    string ArtifactPath,
    bool IsSigned,
    string? Publisher,
    string? SubjectThumbprint,
    bool HasValidTimestamp,
    DateTimeOffset? TimestampUtc,
    string DigestAlgorithm = "SHA256",
    bool ChainValidated = true);

/// <summary>
/// Individual software component in the Software Bill of Materials (SBOM).
/// </summary>
public sealed record SbomComponent(
    string Name,
    string Version,
    string PackageType,
    string? Supplier,
    string? License,
    string Sha256Hash);

/// <summary>
/// Software Bill of Materials (SBOM) model conforming to supply chain transparency standards.
/// Invariants:
/// 200. SBOM_IS_GENERATED_FROM_THE_RELEASE_BEING_DISTRIBUTED
/// 201. SBOM_FAILURE_NEVER_PRODUCES_A_FALSE_COMPLETE_SBOM
/// </summary>
public sealed record SoftwareBillOfMaterials(
    string SbomFormat,
    string DocumentNamespace,
    ReleaseIdentity Release,
    IReadOnlyList<SbomComponent> Components,
    string SbomSha256);

/// <summary>
/// Canonical release manifest binding all distributed executables, signatures, and SBOM.
/// Invariants:
/// 199. RELEASE_MANIFEST_HASHES_EXACT_DISTRIBUTED_ARTIFACTS
/// 202. RELEASE_MANIFEST_AND_EVIDENCE_MANIFEST_ARE_SEPARATE_TRUST_DOMAINS
/// 210. DISTRIBUTED_ARTIFACTS_ARE_BIT_IDENTICAL_TO_THE_VERIFIED_RELEASE_SET
/// </summary>
public sealed record ReleaseManifest(
    ReleaseIdentity Identity,
    IReadOnlyDictionary<string, string> ArtifactSha256Hashes,
    IReadOnlyDictionary<string, AuthenticodeSignatureState> Signatures,
    string SbomSha256,
    DateTimeOffset GeneratedAtUtc);

/// <summary>
/// Evaluation result of the release gate pipeline.
/// Invariant 209: FAILED_RELEASE_GATE_NEVER_PUBLISHES_A_RELEASE_AS_ACCEPTED.
/// </summary>
public sealed record ReleaseGateResult(
    bool IsAccepted,
    IReadOnlyList<string> PassedSteps,
    IReadOnlyList<string> Violations);
