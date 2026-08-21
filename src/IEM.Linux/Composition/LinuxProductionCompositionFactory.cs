using IEM.Core.Probes;
using IEM.Linux.Crypto;
using IEM.Linux.Network;
using IEM.Linux.Storage;

namespace IEM.Linux.Composition;

/// <summary>
/// Authoritative factory for building sealed, dependency-injected Linux production composition graphs.
/// Invariants 8E-C, 8E-D, 8E-E:
/// - Single shared ILinuxPosixStorageApi per composition graph.
/// - Single frozen StateRoot passed to StorageLayout, LinuxSessionModeProvisioner, and LinuxEvidenceKeyProvider.
/// - Explicit ownership policy and symlink safety enforcement across all adapters.
/// - Fail-closed on root execution (EUID == 0) or expected UID/GID mismatch in System mode.
/// </summary>
public static class LinuxProductionCompositionFactory
{
    /// <summary>
    /// Constructs a production composition graph for SystemInstallation mode.
    /// </summary>
    public static LinuxProductionComposition CreateSystem(
        string? stateRoot = null,
        uint? expectedUid = null,
        uint? expectedGid = null,
        ILinuxPosixStorageApi? posix = null,
        IPlatformProbeFactory? probeFactory = null)
    {
        var frozenStateRoot = stateRoot ?? LinuxStoragePaths.DefaultSystemStateRoot;
        var sharedPosix = posix ?? new LinuxNativePosixStorageApi();
        var sharedProbeFactory = probeFactory ?? LinuxProbeFactory.Instance;

        var euid = sharedPosix.GetEuid();
        var egid = sharedPosix.GetEgid();

        if (euid == 0)
        {
            throw new InvalidOperationException("Linux system service must not establish evidence storage as root (EUID == 0).");
        }

        if (expectedUid.HasValue ^ expectedGid.HasValue)
        {
            throw new InvalidOperationException("Both expectedUid and expectedGid must be provided together, or both omitted.");
        }

        if (expectedUid.HasValue && euid != expectedUid.Value)
        {
            throw new InvalidOperationException($"System service execution EUID mismatch: expected {expectedUid.Value}, found {euid}.");
        }

        if (expectedGid.HasValue && egid != expectedGid.Value)
        {
            throw new InvalidOperationException($"System service execution EGID mismatch: expected {expectedGid.Value}, found {egid}.");
        }

        var sharedOwnership = LinuxStorageOwnershipPolicy.CreateSystem(euid, egid);
        var sharedLayout = new LinuxStorageLayout(frozenStateRoot);
        var sharedGuard = new LinuxSymlinkGuard(sharedPosix, sharedOwnership);
        var sharedProtection = new LinuxSessionModeProvisioner(frozenStateRoot, sharedGuard, sharedPosix, sharedOwnership);
        var sharedKeyProvider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, frozenStateRoot, sharedPosix, sharedOwnership);

        return new LinuxProductionComposition(
            LinuxExecutionMode.SystemInstallation,
            frozenStateRoot,
            sharedLayout,
            LinuxSigningIdentityScope.SystemInstallation,
            sharedPosix,
            sharedOwnership,
            sharedGuard,
            sharedProtection,
            sharedKeyProvider,
            sharedProbeFactory);
    }

    /// <summary>
    /// Constructs a production composition graph for PortableUser mode.
    /// </summary>
    public static LinuxProductionComposition CreatePortable(
        string? portableStateRoot = null,
        ILinuxPosixStorageApi? posix = null,
        IPlatformProbeFactory? probeFactory = null)
    {
        var frozenStateRoot = portableStateRoot ?? LinuxStoragePaths.TryResolvePortableStateRoot();
        if (string.IsNullOrWhiteSpace(frozenStateRoot))
        {
            throw new InvalidOperationException(
                "Failed to resolve canonical portable state root directory ($XDG_STATE_HOME and $HOME are unavailable).");
        }

        var sharedPosix = posix ?? new LinuxNativePosixStorageApi();
        var sharedProbeFactory = probeFactory ?? LinuxProbeFactory.Instance;

        var euid = sharedPosix.GetEuid();
        var egid = sharedPosix.GetEgid();

        var sharedOwnership = LinuxStorageOwnershipPolicy.CreatePortable(euid, egid);
        var sharedLayout = new LinuxPortableStorageLayout(frozenStateRoot);
        var sharedGuard = new LinuxSymlinkGuard(sharedPosix, sharedOwnership);
        var sharedProtection = new LinuxSessionModeProvisioner(frozenStateRoot, sharedGuard, sharedPosix, sharedOwnership);
        var sharedKeyProvider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.PortableUser, frozenStateRoot, sharedPosix, sharedOwnership);

        return new LinuxProductionComposition(
            LinuxExecutionMode.PortableUser,
            frozenStateRoot,
            sharedLayout,
            LinuxSigningIdentityScope.PortableUser,
            sharedPosix,
            sharedOwnership,
            sharedGuard,
            sharedProtection,
            sharedKeyProvider,
            sharedProbeFactory);
    }
}
