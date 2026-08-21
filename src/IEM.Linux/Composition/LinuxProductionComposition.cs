using IEM.Core.Probes;
using IEM.Evidence.Crypto;
using IEM.Linux.Crypto;
using IEM.Linux.Storage;
using IEM.Storage.Layout;

namespace IEM.Linux.Composition;

/// <summary>
/// Immutable production composition container for the Linux evidence engine.
/// Represents a fully configured, sealed dependency graph for either SystemInstallation or PortableUser mode.
/// Invariant 8E-B: Frozen execution graph with no public setters and shared single POSIX/security graph.
/// </summary>
public sealed class LinuxProductionComposition
{
    public LinuxExecutionMode Mode { get; }
    public string StateRoot { get; }
    public IPlatformStorageLayout StorageLayout { get; }
    public LinuxSigningIdentityScope SigningScope { get; }
    public ILinuxPosixStorageApi PosixApi { get; }
    public LinuxStorageOwnershipPolicy OwnershipPolicy { get; }
    public ISymlinkSafetyGuard SymlinkGuard { get; }
    public IStorageProtectionProvider StorageProtectionProvider { get; }
    public IEvidenceKeyProvider EvidenceKeyProvider { get; }
    public IPlatformProbeFactory ProbeFactory { get; }

    internal LinuxProductionComposition(
        LinuxExecutionMode mode,
        string stateRoot,
        IPlatformStorageLayout storageLayout,
        LinuxSigningIdentityScope signingScope,
        ILinuxPosixStorageApi posixApi,
        LinuxStorageOwnershipPolicy ownershipPolicy,
        ISymlinkSafetyGuard symlinkGuard,
        IStorageProtectionProvider storageProtectionProvider,
        IEvidenceKeyProvider evidenceKeyProvider,
        IPlatformProbeFactory probeFactory)
    {
        Mode = mode;
        StateRoot = stateRoot ?? throw new ArgumentNullException(nameof(stateRoot));
        StorageLayout = storageLayout ?? throw new ArgumentNullException(nameof(storageLayout));
        SigningScope = signingScope;
        PosixApi = posixApi ?? throw new ArgumentNullException(nameof(posixApi));
        OwnershipPolicy = ownershipPolicy ?? throw new ArgumentNullException(nameof(ownershipPolicy));
        SymlinkGuard = symlinkGuard ?? throw new ArgumentNullException(nameof(symlinkGuard));
        StorageProtectionProvider = storageProtectionProvider ?? throw new ArgumentNullException(nameof(storageProtectionProvider));
        EvidenceKeyProvider = evidenceKeyProvider ?? throw new ArgumentNullException(nameof(evidenceKeyProvider));
        ProbeFactory = probeFactory ?? throw new ArgumentNullException(nameof(probeFactory));
    }
}
