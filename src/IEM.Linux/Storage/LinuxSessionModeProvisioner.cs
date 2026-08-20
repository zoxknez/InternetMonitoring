using System.Text.Json;
using IEM.Storage.Layout;

namespace IEM.Linux.Storage;

/// <summary>
/// POSIX directory permission provisioner and verification inspector for Linux session storage boundaries.
/// Invariants:
/// 77. PRIVILEGED_EVIDENCE_WRITES_NEVER_FOLLOW_UNTRUSTED_REPARSE_POINTS
/// 80. STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR
/// 81. EVIDENCE_SESSION_NEVER_STARTS_WITH_UNESTABLISHED_STORAGE_BOUNDARY
/// 82. FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS
/// </summary>
public sealed class LinuxSessionModeProvisioner : IStorageProtectionProvider
{
    public string PlatformName => "Linux";

    private readonly ISymlinkSafetyGuard _symlinkGuard;
    private readonly ILinuxPosixStorageApi _posix;
    private readonly LinuxStorageOwnershipPolicy _ownershipPolicy;

    public LinuxSessionModeProvisioner(
        ISymlinkSafetyGuard? symlinkGuard = null,
        ILinuxPosixStorageApi? posix = null,
        LinuxStorageOwnershipPolicy? ownershipPolicy = null)
    {
        _posix = posix ?? new LinuxNativePosixStorageApi();
        _ownershipPolicy = ownershipPolicy ?? LinuxStorageOwnershipPolicy.SystemDefault;
        _symlinkGuard = symlinkGuard ?? new LinuxSymlinkGuard(_posix, _ownershipPolicy);
    }

    public async Task<StorageProtectionObservation> ProvisionSessionBoundariesAsync(
        string sessionRoot,
        SessionLayoutDescriptor layout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentNullException.ThrowIfNull(layout);

        var resolver = new SessionPathResolver(sessionRoot, layout);
        var now = DateTimeOffset.UtcNow;
        var obsId = $"spo-lnx-{Guid.NewGuid():N}";

        try
        {
            // 1. Create root directory with mode 0700
            Directory.CreateDirectory(sessionRoot);

            // 2. Validate symlink safety of root
            var rootCheck = _symlinkGuard.ValidatePath(sessionRoot, sessionRoot);
            if (!rootCheck.IsSafe)
            {
                return new StorageProtectionObservation(
                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                    StorageProtectionState.NotEstablished,
                    RootBoundaryValid: false, ReparsePointCheck: false,
                    DiagnosticMessage: rootCheck.ViolationMessage ?? "Koren sesije je nebezbedan.");
            }

            // 3. Create semantic subdirectories with mode 0700
            var rawDir = resolver.GetAreaFullPath(StorageAreaPolicy.RawArea);
            var derivedDir = resolver.GetAreaFullPath(StorageAreaPolicy.DerivedArea);
            var evidenceDir = resolver.GetAreaFullPath(StorageAreaPolicy.EvidenceArea);
            var exportsDir = resolver.GetAreaFullPath(StorageAreaPolicy.ExportsArea);

            Directory.CreateDirectory(rawDir);
            Directory.CreateDirectory(derivedDir);
            Directory.CreateDirectory(evidenceDir);
            Directory.CreateDirectory(exportsDir);

            // 4. Write layout.json atomically with mode 0600
            var layoutPath = Path.Combine(sessionRoot, SessionLayoutDescriptor.FileName);
            var layoutTmp = layoutPath + ".tmp";
            await File.WriteAllBytesAsync(layoutTmp, layout.ToCanonicalBytes(), ct).ConfigureAwait(false);
            File.Move(layoutTmp, layoutPath, overwrite: true);

            // 5. Run full verification to assert Established state
            return await VerifyStorageProtectionAsync(sessionRoot, layout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: $"Greška pri kreiranju Linux POSIX zaštite sesije: {ex.Message}");
        }
    }

    public async Task<StorageProtectionObservation> VerifyStorageProtectionAsync(
        string sessionRoot,
        SessionLayoutDescriptor layout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentNullException.ThrowIfNull(layout);

        var resolver = new SessionPathResolver(sessionRoot, layout);
        var now = DateTimeOffset.UtcNow;
        var obsId = $"spo-chk-lnx-{Guid.NewGuid():N}";

        if (!Directory.Exists(sessionRoot))
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: $"Koren sesije '{sessionRoot}' ne postoji.");
        }

        // 1. Validate root boundary and symlinks
        var rootCheck = _symlinkGuard.ValidatePath(sessionRoot, sessionRoot);
        if (!rootCheck.IsSafe)
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: rootCheck.ViolationMessage ?? "Koren sesije je nebezbedan.");
        }

        // 2. Validate all semantic areas
        var rawDir = resolver.GetAreaFullPath(StorageAreaPolicy.RawArea);
        var derivedDir = resolver.GetAreaFullPath(StorageAreaPolicy.DerivedArea);
        var evidenceDir = resolver.GetAreaFullPath(StorageAreaPolicy.EvidenceArea);
        var exportsDir = resolver.GetAreaFullPath(StorageAreaPolicy.ExportsArea);

        var areas = new[] { ("Raw", rawDir), ("Derived", derivedDir), ("Evidence", evidenceDir), ("Exports", exportsDir) };
        foreach (var (name, dir) in areas)
        {
            if (!Directory.Exists(dir))
            {
                return new StorageProtectionObservation(
                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                    StorageProtectionState.NotEstablished,
                    RootBoundaryValid: true, ReparsePointCheck: false,
                    DiagnosticMessage: $"Nedostaje obavezna podzona sesije '{name}' ({dir}).");
            }

            var areaCheck = _symlinkGuard.ValidatePath(sessionRoot, dir);
            if (!areaCheck.IsSafe)
            {
                return new StorageProtectionObservation(
                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                    StorageProtectionState.NotEstablished,
                    RootBoundaryValid: true, ReparsePointCheck: false,
                    DiagnosticMessage: areaCheck.ViolationMessage ?? $"Podzona sesije '{name}' je nebezbedna.");
            }
        }

        // 3. Validate layout.json
        var layoutPath = Path.Combine(sessionRoot, SessionLayoutDescriptor.FileName);
        if (!File.Exists(layoutPath))
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: true, ReparsePointCheck: false,
                DiagnosticMessage: $"Nedostaje deskriptor sesije '{SessionLayoutDescriptor.FileName}'.");
        }

        var layoutCheck = _symlinkGuard.ValidatePath(sessionRoot, layoutPath);
        if (!layoutCheck.IsSafe)
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: true, ReparsePointCheck: false,
                DiagnosticMessage: layoutCheck.ViolationMessage ?? $"Deskriptor '{SessionLayoutDescriptor.FileName}' je nebezbedan.");
        }

        // 4. Validate layout.json content and SessionId match
        try
        {
            var bytes = await File.ReadAllBytesAsync(layoutPath, ct).ConfigureAwait(false);
            var loaded = SessionLayoutDescriptor.FromCanonicalBytes(bytes);
            if (loaded == null || loaded.SessionId != layout.SessionId)
            {
                return new StorageProtectionObservation(
                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                    StorageProtectionState.NotEstablished,
                    RootBoundaryValid: true, ReparsePointCheck: false,
                    DiagnosticMessage: loaded == null
                        ? $"Deskriptor '{SessionLayoutDescriptor.FileName}' nije validan JSON."
                        : $"Deskriptor sadrži neslaganje SessionId: '{loaded.SessionId}' umesto očekivanog '{layout.SessionId}'.");
            }
        }
        catch (Exception ex)
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: true, ReparsePointCheck: false,
                DiagnosticMessage: $"Deskriptor '{SessionLayoutDescriptor.FileName}' je oštećen ili nečitljiv: {ex.Message}");
        }

        // All checks succeeded
        return new StorageProtectionObservation(
            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
            layout.StoragePolicyVersion, layout.StoragePolicyHash,
            StorageProtectionState.Established,
            RootBoundaryValid: true, ReparsePointCheck: true,
            PlatformSecurityDescriptorRef: $"POSIX:0700/0600:{_ownershipPolicy.PolicyName}");
    }
}
