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

    private readonly string _stateRoot;
    private readonly ISymlinkSafetyGuard _symlinkGuard;
    private readonly ILinuxPosixStorageApi _posix;
    private readonly LinuxStorageOwnershipPolicy _ownershipPolicy;

    public LinuxSessionModeProvisioner(
        string? stateRoot = null,
        ISymlinkSafetyGuard? symlinkGuard = null,
        ILinuxPosixStorageApi? posix = null,
        LinuxStorageOwnershipPolicy? ownershipPolicy = null)
    {
        _stateRoot = stateRoot ?? LinuxStoragePaths.DefaultSystemStateRoot;
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
            var normStateRoot = _stateRoot.Replace('\\', '/').TrimEnd('/');
            var normSessionRoot = sessionRoot.Replace('\\', '/').TrimEnd('/');

            if (normStateRoot.Length >= 2 && normStateRoot[1] == ':' && char.IsLetter(normStateRoot[0]))
            {
                normStateRoot = normStateRoot.Substring(2);
            }
            if (normSessionRoot.Length >= 2 && normSessionRoot[1] == ':' && char.IsLetter(normSessionRoot[0]))
            {
                normSessionRoot = normSessionRoot.Substring(2);
            }

            // 1. R2-C: Open StateRoot FD - StateRoot in system mode is VERIFY-ONLY.
            // It MUST already exist, be a directory, have mode 0700 and correct ownership.
            // NO Directory.CreateDirectory(StateRoot) and NO Fchmod(StateRoot).
            var rootFd = _posix.Open(normStateRoot, LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_NOFOLLOW | LinuxPosixStorageConstants.O_CLOEXEC, 0);
            if (rootFd < 0)
            {
                return new StorageProtectionObservation(
                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                    StorageProtectionState.NotEstablished,
                    RootBoundaryValid: false, ReparsePointCheck: false,
                    DiagnosticMessage: $"Autoritativni StateRoot '{normStateRoot}' ne postoji ili se ne može otvoriti.");
            }

            var openFds = new List<int> { rootFd };

            try
            {
                if (_posix.Fstat(rootFd, out var rootStat) != 0 || !rootStat.IsDirectory)
                {
                    return new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"Autoritativni StateRoot '{normStateRoot}' nije validan direktorijum.");
                }

                // Verify exact mode 0700 and ownership on StateRoot (ZERO repair!)
                if ((rootStat.PermissionBits & 0x1FF) != LinuxPosixStorageConstants.Mode0700)
                {
                    return new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"StateRoot '{normStateRoot}' ima neispravne permisije 0{Convert.ToString(rootStat.PermissionBits, 8)} (zahteva se 0700, tiha popravka je zabranjena).");
                }

                if (_ownershipPolicy.EnforceExactOwnership)
                {
                    if (_ownershipPolicy.ExpectedUid.HasValue && rootStat.Uid != _ownershipPolicy.ExpectedUid.Value)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"StateRoot '{normStateRoot}' ima neispravnog vlasnika UID {rootStat.Uid} (očekivano {_ownershipPolicy.ExpectedUid.Value}).");
                    }
                    if (_ownershipPolicy.ExpectedGid.HasValue && rootStat.Gid != _ownershipPolicy.ExpectedGid.Value)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"StateRoot '{normStateRoot}' ima neispravnu grupu GID {rootStat.Gid} (očekivano {_ownershipPolicy.ExpectedGid.Value}).");
                    }
                }

                // Relative path from StateRoot to sessionRoot
                if (!normSessionRoot.StartsWith(normStateRoot + "/", StringComparison.Ordinal) && normSessionRoot != normStateRoot)
                {
                    return new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"Koren sesije '{sessionRoot}' se nalazi van StateRoot-a '{normStateRoot}'.");
                }

                var relSession = normSessionRoot.Substring(normStateRoot.Length).TrimStart('/');
                var sessionSegments = relSession.Split('/', StringSplitOptions.RemoveEmptyEntries);

                var currentFd = rootFd;

                // 2. R2-B: Step-by-step FD-relative traversal with NO-REPAIR of existing objects
                foreach (var seg in sessionSegments)
                {
                    bool existed = _posix.FstatAt(currentFd, seg, out var segStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0;
                    if (!existed)
                    {
                        // Object is newly created -> MkdirAt(0700) and Fchmod(0700) is allowed
                        if (_posix.MkdirAt(currentFd, seg, LinuxPosixStorageConstants.Mode0700) != 0)
                        {
                            return new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: false, ReparsePointCheck: false,
                                DiagnosticMessage: $"mkdirat nije uspeo za segment '{seg}'.");
                        }
                    }

                    var openHow = new OpenHow
                    {
                        Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_CLOEXEC,
                        Mode = 0,
                        Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                    };

                    int nextFd = _posix.OpenAt2(currentFd, seg, ref openHow);
                    if (nextFd < 0)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Nije moguće bezbedno otvoriti segment sesije '{seg}' kroz openat2.");
                    }

                    openFds.Add(nextFd);

                    if (_posix.Fstat(nextFd, out var fdStat) != 0 || !fdStat.IsDirectory)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Segment '{seg}' nije validan direktorijum.");
                    }

                    if (existed)
                    {
                        // Invariant 80: Existing object MUST strictly have mode 0700 and correct owner. NEVER chmod/chown!
                        if ((fdStat.PermissionBits & 0x1FF) != LinuxPosixStorageConstants.Mode0700)
                        {
                            return new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: false, ReparsePointCheck: false,
                                DiagnosticMessage: $"Postojeći segment '{seg}' ima neispravne permisije 0{Convert.ToString(fdStat.PermissionBits, 8)} (zahteva se 0700, tiha popravka je zabranjena).");
                        }
                    }
                    else
                    {
                        _posix.Fchmod(nextFd, LinuxPosixStorageConstants.Mode0700);
                    }

                    currentFd = nextFd;
                }

                var sessionFd = currentFd;

                // 3. R2-B: Semantic subdirectories (Raw, Evidence, Derived, Exports) with NO-REPAIR of existing
                var areaNames = new[] { "Raw", "Evidence", "Derived", "Exports" };
                foreach (var area in areaNames)
                {
                    bool areaExisted = _posix.FstatAt(sessionFd, area, out var areaStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0;
                    if (!areaExisted)
                    {
                        if (_posix.MkdirAt(sessionFd, area, LinuxPosixStorageConstants.Mode0700) != 0)
                        {
                            return new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: false, ReparsePointCheck: false,
                                DiagnosticMessage: $"mkdirat nije uspeo za podzonu '{area}'.");
                        }
                    }

                    var areaHow = new OpenHow
                    {
                        Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_CLOEXEC,
                        Mode = 0,
                        Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                    };

                    int areaFd = _posix.OpenAt2(sessionFd, area, ref areaHow);
                    if (areaFd < 0)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Nije moguće bezbedno otvoriti podzonu '{area}' kroz openat2.");
                    }

                    try
                    {
                        if (_posix.Fstat(areaFd, out var fdStat) != 0 || !fdStat.IsDirectory)
                        {
                            return new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: false, ReparsePointCheck: false,
                                DiagnosticMessage: $"Podzona '{area}' nije validan direktorijum.");
                        }

                        if (areaExisted)
                        {
                            // Invariant 80: Existing area directory MUST be strictly 0700 (NO chmod!)
                            if ((fdStat.PermissionBits & 0x1FF) != LinuxPosixStorageConstants.Mode0700)
                            {
                                return new StorageProtectionObservation(
                                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                    StorageProtectionState.NotEstablished,
                                    RootBoundaryValid: false, ReparsePointCheck: false,
                                    DiagnosticMessage: $"Postojeća podzona '{area}' ima neispravne permisije 0{Convert.ToString(fdStat.PermissionBits, 8)} (zahteva se 0700, tiha popravka je zabranjena).");
                            }
                        }
                        else
                        {
                            _posix.Fchmod(areaFd, LinuxPosixStorageConstants.Mode0700);
                        }
                    }
                    finally
                    {
                        _posix.Close(areaFd);
                    }
                }

                // 4. R2-D & R2-E: Write layout.json FD-relatively with O_CREAT | O_EXCL (refuse overwrite if exists!)
                var layoutBytes = layout.ToCanonicalBytes();
                var layoutHow = new OpenHow
                {
                    Flags = (ulong)(LinuxPosixStorageConstants.O_CREAT | LinuxPosixStorageConstants.O_EXCL | LinuxPosixStorageConstants.O_WRONLY | LinuxPosixStorageConstants.O_CLOEXEC),
                    Mode = (ulong)LinuxPosixStorageConstants.Mode0600,
                    Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                };

                int layoutFd = _posix.OpenAt2(sessionFd, SessionLayoutDescriptor.FileName, ref layoutHow);
                if (layoutFd < 0)
                {
                    // File already exists or openat2 refused creation -> Invariant 80: refuse silent overwrite!
                    return new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"Nije moguće kreirati '{SessionLayoutDescriptor.FileName}' kroz openat2 (fajl već postoji ili je kreiranje odbijeno).");
                }

                try
                {
                    int written = _posix.Write(layoutFd, layoutBytes);
                    if (written != layoutBytes.Length)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Upisivanje u '{SessionLayoutDescriptor.FileName}' nije uspelo ({written}/{layoutBytes.Length} bajtova).");
                    }

                    _posix.Fsync(layoutFd);
                }
                finally
                {
                    _posix.Close(layoutFd);
                }
            }
            finally
            {
                foreach (var fd in openFds)
                {
                    _posix.Close(fd);
                }
            }

            // 5. Run full verification from StateRoot to assert Established state
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

        var normStateRoot = _stateRoot.Replace('\\', '/').TrimEnd('/');
        var normSessionRoot = sessionRoot.Replace('\\', '/').TrimEnd('/');

        if (normStateRoot.Length >= 2 && normStateRoot[1] == ':' && char.IsLetter(normStateRoot[0]))
        {
            normStateRoot = normStateRoot.Substring(2);
        }
        if (normSessionRoot.Length >= 2 && normSessionRoot[1] == ':' && char.IsLetter(normSessionRoot[0]))
        {
            normSessionRoot = normSessionRoot.Substring(2);
        }

        // 1. Validate sessionRoot from trusted StateRoot
        var rootCheck = _symlinkGuard.ValidatePath(normStateRoot, normSessionRoot);
        if (!rootCheck.IsSafe)
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: rootCheck.ViolationMessage ?? "Koren sesije je nebezbedan.");
        }

        // 2. Validate all semantic areas from trusted StateRoot
        var rawDir = resolver.GetAreaFullPath(StorageAreaPolicy.RawArea);
        var derivedDir = resolver.GetAreaFullPath(StorageAreaPolicy.DerivedArea);
        var evidenceDir = resolver.GetAreaFullPath(StorageAreaPolicy.EvidenceArea);
        var exportsDir = resolver.GetAreaFullPath(StorageAreaPolicy.ExportsArea);

        var areas = new[] { ("Raw", rawDir), ("Derived", derivedDir), ("Evidence", evidenceDir), ("Exports", exportsDir) };
        foreach (var (name, dir) in areas)
        {
            var areaCheck = _symlinkGuard.ValidatePath(normStateRoot, dir);
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

        // 3. Validate layout.json from trusted StateRoot
        var layoutPath = Path.Combine(sessionRoot, SessionLayoutDescriptor.FileName);
        var layoutCheck = _symlinkGuard.ValidatePath(normStateRoot, layoutPath);
        if (!layoutCheck.IsSafe)
        {
            return new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: true, ReparsePointCheck: false,
                DiagnosticMessage: layoutCheck.ViolationMessage ?? $"Deskriptor '{SessionLayoutDescriptor.FileName}' je nebezbedan.");
        }

        // 4. R2-E: Open and read layout.json from the SAME FD (eliminated TOCTOU gap!)
        int sessionFd = _posix.Open(normSessionRoot, LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_NOFOLLOW | LinuxPosixStorageConstants.O_CLOEXEC, 0);
        if (sessionFd >= 0)
        {
            try
            {
                var layoutHow = new OpenHow
                {
                    Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_CLOEXEC,
                    Mode = 0,
                    Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                };

                int layoutFd = _posix.OpenAt2(sessionFd, SessionLayoutDescriptor.FileName, ref layoutHow);
                if (layoutFd < 0)
                {
                    return new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: true, ReparsePointCheck: false,
                        DiagnosticMessage: $"Nije moguće bezbedno otvoriti '{SessionLayoutDescriptor.FileName}' kroz openat2.");
                }

                try
                {
                    if (_posix.Fstat(layoutFd, out var fdStat) != 0 || !fdStat.IsRegularFile)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Deskriptor '{SessionLayoutDescriptor.FileName}' nije validan regularni fajl.");
                    }

                    if (fdStat.Size > 65536)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Deskriptor '{SessionLayoutDescriptor.FileName}' premašuje maksimalnu dozvoljenu veličinu.");
                    }

                    var buffer = new byte[(int)fdStat.Size];
                    int read = _posix.Read(layoutFd, buffer);
                    if (read == buffer.Length && buffer.Length > 0)
                    {
                        var loaded = SessionLayoutDescriptor.FromCanonicalBytes(buffer);
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
                }
                finally
                {
                    _posix.Close(layoutFd);
                }
            }
            finally
            {
                _posix.Close(sessionFd);
            }
        }
        else if (File.Exists(layoutPath))
        {
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
