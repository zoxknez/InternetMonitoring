namespace IEM.Storage.Layout;

/// <summary>
/// Platform-neutral contract for provisioning and verifying session filesystem protection boundaries.
/// Invariants:
/// 81. EVIDENCE_SESSION_NEVER_STARTS_WITH_UNESTABLISHED_STORAGE_BOUNDARY
/// 82. FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS
/// </summary>
public interface IStorageProtectionProvider
{
    string PlatformName { get; }

    Task<StorageProtectionObservation> ProvisionSessionBoundariesAsync(
        string sessionRoot,
        SessionLayoutDescriptor layout,
        CancellationToken ct = default);

    Task<StorageProtectionObservation> VerifyStorageProtectionAsync(
        string sessionRoot,
        SessionLayoutDescriptor layout,
        CancellationToken ct = default);
}
