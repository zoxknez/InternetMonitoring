namespace IEM.Storage.Layout;

public enum StorageProtectionState
{
    Unknown,
    Established,
    Degraded,
    NotEstablished,
    Unsupported,
}

/// <summary>
/// Operational factual observation of the storage protection state for a session.
/// Invariants:
/// 76. FILESYSTEM_ACL_IS_PROTECTION_PROVENANCE_NOT_CRYPTOGRAPHIC_INTEGRITY
/// 80. STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR
/// 82. FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS
/// </summary>
public sealed record StorageProtectionObservation(
    string ObservationId,
    string SessionId,
    DateTimeOffset CapturedUtc,
    string Platform,
    int LayoutVersion,
    int StoragePolicyVersion,
    string StoragePolicyHash,
    StorageProtectionState ProtectionState,
    bool RootBoundaryValid,
    bool ReparsePointCheck,
    string? PlatformSecurityDescriptorRef = null,
    string? DiagnosticMessage = null);
