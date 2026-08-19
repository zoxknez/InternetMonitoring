namespace IEM.Storage.Layout;

public enum ArtifactRole
{
    RawEvidence,
    DerivedEvidence,
    IntegrityEnvelope,
    Export,
}

public enum ArtifactMutationPolicy
{
    AppendOnlyUntilSeal,
    CreateOnce,
    EnvelopePostSealWrite,
    UserMutableExcluded,
}

public enum StorageAccessLevel
{
    None,
    ReadOnly,
    AppendOnly,
    FullControl,
}

/// <summary>
/// Declarative policy for a semantic storage area within the session package.
/// Invariants:
/// 68. MANIFEST_SCOPE_IS_DEFINED_BY_ARTIFACT_ROLE
/// 69. USER_WRITABLE_CONTENT_NEVER_BECOMES_PROTECTED_EVIDENCE
/// </summary>
public sealed record StorageAreaPolicy(
    ArtifactRole Role,
    string RelativeRoot,
    ArtifactMutationPolicy MutationPolicy,
    bool ManifestParticipation,
    StorageAccessLevel ServiceAccessActive,
    StorageAccessLevel ServiceAccessSealed,
    StorageAccessLevel UserAccess)
{
    public static readonly StorageAreaPolicy RawArea = new(
        Role: ArtifactRole.RawEvidence,
        RelativeRoot: "Raw",
        MutationPolicy: ArtifactMutationPolicy.AppendOnlyUntilSeal,
        ManifestParticipation: true,
        ServiceAccessActive: StorageAccessLevel.AppendOnly,
        ServiceAccessSealed: StorageAccessLevel.ReadOnly,
        UserAccess: StorageAccessLevel.ReadOnly);

    public static readonly StorageAreaPolicy DerivedArea = new(
        Role: ArtifactRole.DerivedEvidence,
        RelativeRoot: "Derived",
        MutationPolicy: ArtifactMutationPolicy.AppendOnlyUntilSeal,
        ManifestParticipation: true,
        ServiceAccessActive: StorageAccessLevel.AppendOnly,
        ServiceAccessSealed: StorageAccessLevel.ReadOnly,
        UserAccess: StorageAccessLevel.ReadOnly);

    public static readonly StorageAreaPolicy EvidenceArea = new(
        Role: ArtifactRole.IntegrityEnvelope,
        RelativeRoot: "Evidence",
        MutationPolicy: ArtifactMutationPolicy.EnvelopePostSealWrite,
        ManifestParticipation: false, // Envelope itself is not in the manifest file list
        ServiceAccessActive: StorageAccessLevel.ReadOnly,
        ServiceAccessSealed: StorageAccessLevel.AppendOnly, // for post-seal timestamp writes
        UserAccess: StorageAccessLevel.ReadOnly);

    public static readonly StorageAreaPolicy ExportsArea = new(
        Role: ArtifactRole.Export,
        RelativeRoot: "Exports",
        MutationPolicy: ArtifactMutationPolicy.UserMutableExcluded,
        ManifestParticipation: false,
        ServiceAccessActive: StorageAccessLevel.FullControl,
        ServiceAccessSealed: StorageAccessLevel.FullControl,
        UserAccess: StorageAccessLevel.FullControl);

    public static readonly IReadOnlyList<StorageAreaPolicy> StandardAreas = new[]
    {
        RawArea,
        DerivedArea,
        EvidenceArea,
        ExportsArea,
    };
}
