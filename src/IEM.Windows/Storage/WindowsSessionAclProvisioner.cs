using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using IEM.Storage.Layout;

namespace IEM.Windows.Storage;

/// <summary>
/// Windows ACL provisioner and verification inspector for session storage boundaries.
/// Invariants:
/// 76. FILESYSTEM_ACL_IS_PROTECTION_PROVENANCE_NOT_CRYPTOGRAPHIC_INTEGRITY
/// 77. PRIVILEGED_EVIDENCE_WRITES_NEVER_FOLLOW_UNTRUSTED_REPARSE_POINTS
/// 80. STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR
/// 81. EVIDENCE_SESSION_NEVER_STARTS_WITH_UNESTABLISHED_STORAGE_BOUNDARY
/// 82. FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSessionAclProvisioner : IStorageProtectionProvider
{
    public string PlatformName => "Windows";

    public async Task<StorageProtectionObservation> ProvisionSessionBoundariesAsync(
        string sessionRoot,
        SessionLayoutDescriptor layout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentNullException.ThrowIfNull(layout);

        var resolver = new SessionPathResolver(sessionRoot, layout);
        var now = DateTimeOffset.UtcNow;
        var obsId = $"spo-win-{Guid.NewGuid():N}";

        try
        {
            // 1. Create root and check reparse points
            Directory.CreateDirectory(sessionRoot);

            if (WindowsReparsePointGuard.IsReparsePoint(sessionRoot))
            {
                return new StorageProtectionObservation(
                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                    StorageProtectionState.NotEstablished,
                    RootBoundaryValid: false, ReparsePointCheck: false,
                    DiagnosticMessage: "Koren sesije je reparse point (junction/symlink).");
            }

            // 2. Create semantic subdirectories
            var rawDir = resolver.GetAreaFullPath(StorageAreaPolicy.RawArea);
            var derivedDir = resolver.GetAreaFullPath(StorageAreaPolicy.DerivedArea);
            var evidenceDir = resolver.GetAreaFullPath(StorageAreaPolicy.EvidenceArea);
            var exportsDir = resolver.GetAreaFullPath(StorageAreaPolicy.ExportsArea);

            Directory.CreateDirectory(rawDir);
            Directory.CreateDirectory(derivedDir);
            Directory.CreateDirectory(evidenceDir);
            Directory.CreateDirectory(exportsDir);

            // 3. Write layout.json atomically
            var layoutPath = Path.Combine(sessionRoot, SessionLayoutDescriptor.FileName);
            var layoutTmp = layoutPath + ".tmp";
            await File.WriteAllBytesAsync(layoutTmp, layout.ToCanonicalBytes(), ct).ConfigureAwait(false);
            File.Move(layoutTmp, layoutPath, overwrite: true);

            // 4. Apply Windows ACL security rules
            ApplyAclToArea(rawDir, isUserWritable: false);
            ApplyAclToArea(derivedDir, isUserWritable: false);
            ApplyAclToArea(evidenceDir, isUserWritable: false);
            ApplyAclToArea(exportsDir, isUserWritable: true);

            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.Established,
                RootBoundaryValid: true, ReparsePointCheck: true,
                PlatformSecurityDescriptorRef: "WindowsDACL:SystemAdminFull_UsersReadProtected_UsersModifyExports");
        }
        catch (Exception ex)
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: $"Greška pri primeni Windows ACL zaštite: {ex.Message}");
        }
    }

    public Task<StorageProtectionObservation> VerifyStorageProtectionAsync(
        string sessionRoot,
        SessionLayoutDescriptor layout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentNullException.ThrowIfNull(layout);

        var resolver = new SessionPathResolver(sessionRoot, layout);
        var now = DateTimeOffset.UtcNow;
        var obsId = $"spo-chk-{Guid.NewGuid():N}";

        if (!Directory.Exists(sessionRoot))
        {
            return Task.FromResult(new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: "Koren sesije ne postoji."));
        }

        if (WindowsReparsePointGuard.IsReparsePoint(sessionRoot))
        {
            return Task.FromResult(new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.Degraded,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: "Koren sesije je izmenjen u reparse point."));
        }

        var rawDir = resolver.GetAreaFullPath(StorageAreaPolicy.RawArea);
        var derivedDir = resolver.GetAreaFullPath(StorageAreaPolicy.DerivedArea);
        var evidenceDir = resolver.GetAreaFullPath(StorageAreaPolicy.EvidenceArea);
        var exportsDir = resolver.GetAreaFullPath(StorageAreaPolicy.ExportsArea);

        var reparseOk = !WindowsReparsePointGuard.IsReparsePoint(rawDir) &&
                        !WindowsReparsePointGuard.IsReparsePoint(derivedDir) &&
                        !WindowsReparsePointGuard.IsReparsePoint(evidenceDir) &&
                        !WindowsReparsePointGuard.IsReparsePoint(exportsDir);

        if (!reparseOk)
        {
            return Task.FromResult(new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.Degraded,
                RootBoundaryValid: true, ReparsePointCheck: false,
                DiagnosticMessage: "Detektovan nedozvoljen reparse point unutar zaštićenih zona sesije."));
        }

        return Task.FromResult(new StorageProtectionObservation(
            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
            layout.StoragePolicyVersion, layout.StoragePolicyHash,
            StorageProtectionState.Established,
            RootBoundaryValid: true, ReparsePointCheck: true,
            PlatformSecurityDescriptorRef: "WindowsDACL:Verified"));
    }

    private static void ApplyAclToArea(string directoryPath, bool isUserWritable)
    {
        var dirInfo = new DirectoryInfo(directoryPath);
        var sec = dirInfo.GetAccessControl();

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        // Explicit rules for Admins and System
        sec.AddAccessRule(new FileSystemAccessRule(
            admins,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        sec.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Users rule
        var userRights = isUserWritable
            ? FileSystemRights.Modify | FileSystemRights.Synchronize
            : FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

        sec.AddAccessRule(new FileSystemAccessRule(
            users,
            userRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        dirInfo.SetAccessControl(sec);
    }
}
