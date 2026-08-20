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
    public const int MaxLayoutDescriptorSizeBytes = 65536;

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

        var now = DateTimeOffset.UtcNow;
        var obsId = $"spo-lnx-{Guid.NewGuid():N}";

        try
        {
            var normStateRoot = NormalizePath(_stateRoot);
            var normSessionRoot = NormalizePath(sessionRoot);

            // 1. R2-C & R3-F: Open StateRoot FD - StateRoot in system mode is VERIFY-ONLY.
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
                if ((rootStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
                {
                    return new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"StateRoot '{normStateRoot}' ima neispravne permisije 0{Convert.ToString(rootStat.PermissionBits, 8)} (zahteva se 0700, tiha popravka je zabranjena).");
                }

                if (!CheckOwnership(rootStat, out var ownerError))
                {
                    return new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"StateRoot '{normStateRoot}' {ownerError}");
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

                // 2. R2-B, R3-E, R3-F: Step-by-step FD-relative traversal with NO-REPAIR of existing objects
                foreach (var seg in sessionSegments)
                {
                    bool existed = _posix.FstatAt(currentFd, seg, out var segStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0;
                    if (!existed)
                    {
                        // Object is newly created -> MkdirAt(0700)
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
                        // Invariant 80 & R3-F: Existing object MUST strictly have mode 0700 and correct owner BEFORE descending!
                        if ((fdStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
                        {
                            return new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: false, ReparsePointCheck: false,
                                DiagnosticMessage: $"Postojeći segment '{seg}' ima neispravne permisije 0{Convert.ToString(fdStat.PermissionBits, 8)} (zahteva se 0700, tiha popravka je zabranjena).");
                        }

                        if (!CheckOwnership(fdStat, out var segOwnerError))
                        {
                            return new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: false, ReparsePointCheck: false,
                                DiagnosticMessage: $"Postojeći segment '{seg}' {segOwnerError}");
                        }
                    }
                    else
                    {
                        if (_posix.Fchmod(nextFd, LinuxPosixStorageConstants.Mode0700) != 0)
                        {
                            return new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: false, ReparsePointCheck: false,
                                DiagnosticMessage: $"fchmod 0700 nije uspeo za novokreirani segment '{seg}'.");
                        }
                    }

                    currentFd = nextFd;
                }

                var sessionFd = currentFd;

                // 3. R2-B, R3-E, R3-F: Semantic subdirectories (Raw, Evidence, Derived, Exports) with NO-REPAIR of existing
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
                            // Invariant 80 & R3-F: Existing area directory MUST be strictly 0700 and correct owner (NO chmod!)
                            if ((fdStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
                            {
                                return new StorageProtectionObservation(
                                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                    StorageProtectionState.NotEstablished,
                                    RootBoundaryValid: false, ReparsePointCheck: false,
                                    DiagnosticMessage: $"Postojeća podzona '{area}' ima neispravne permisije 0{Convert.ToString(fdStat.PermissionBits, 8)} (zahteva se 0700, tiha popravka je zabranjena).");
                            }

                            if (!CheckOwnership(fdStat, out var areaOwnerError))
                            {
                                return new StorageProtectionObservation(
                                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                    StorageProtectionState.NotEstablished,
                                    RootBoundaryValid: false, ReparsePointCheck: false,
                                    DiagnosticMessage: $"Postojeća podzona '{area}' {areaOwnerError}");
                            }
                        }
                        else
                        {
                            if (_posix.Fchmod(areaFd, LinuxPosixStorageConstants.Mode0700) != 0)
                            {
                                return new StorageProtectionObservation(
                                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                    StorageProtectionState.NotEstablished,
                                    RootBoundaryValid: false, ReparsePointCheck: false,
                                    DiagnosticMessage: $"fchmod 0700 nije uspeo za novokreiranu podzonu '{area}'.");
                            }
                        }
                    }
                    finally
                    {
                        _posix.Close(areaFd);
                    }
                }

                // 4. R2-D, R3-D, R3-E: Write layout.json FD-relatively with O_CREAT | O_EXCL (refuse overwrite if exists!)
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
                    if (!WriteAll(layoutFd, layoutBytes))
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Upisivanje u '{SessionLayoutDescriptor.FileName}' nije uspelo kroz WriteAll.");
                    }

                    if (_posix.Fsync(layoutFd) != 0)
                    {
                        return new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"fsync nije uspeo za '{SessionLayoutDescriptor.FileName}'.");
                    }
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

    public Task<StorageProtectionObservation> VerifyStorageProtectionAsync(
        string sessionRoot,
        SessionLayoutDescriptor layout,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentNullException.ThrowIfNull(layout);

        var now = DateTimeOffset.UtcNow;
        var obsId = $"spo-chk-lnx-{Guid.NewGuid():N}";

        try
        {
            var normStateRoot = NormalizePath(_stateRoot);
            var normSessionRoot = NormalizePath(sessionRoot);

            // 1. R3-B: Open trusted StateRoot FD
            var rootFd = _posix.Open(normStateRoot, LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_NOFOLLOW | LinuxPosixStorageConstants.O_CLOEXEC, 0);
            if (rootFd < 0)
            {
                return Task.FromResult(new StorageProtectionObservation(
                    obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                    layout.StoragePolicyVersion, layout.StoragePolicyHash,
                    StorageProtectionState.NotEstablished,
                    RootBoundaryValid: false, ReparsePointCheck: false,
                    DiagnosticMessage: $"Autoritativni StateRoot '{normStateRoot}' ne postoji ili se ne može otvoriti. ZERO pathname fallback."));
            }

            var openFds = new List<int> { rootFd };

            try
            {
                if (_posix.Fstat(rootFd, out var rootStat) != 0 || !rootStat.IsDirectory)
                {
                    return Task.FromResult(new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"Autoritativni StateRoot '{normStateRoot}' nije validan direktorijum."));
                }

                if ((rootStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
                {
                    return Task.FromResult(new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"StateRoot '{normStateRoot}' ima neispravne permisije 0{Convert.ToString(rootStat.PermissionBits, 8)}."));
                }

                if (!CheckOwnership(rootStat, out var rootOwnerErr))
                {
                    return Task.FromResult(new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"StateRoot '{normStateRoot}' {rootOwnerErr}"));
                }

                // Verify sessionRoot is within StateRoot
                if (!normSessionRoot.StartsWith(normStateRoot + "/", StringComparison.Ordinal) && normSessionRoot != normStateRoot)
                {
                    return Task.FromResult(new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: false, ReparsePointCheck: false,
                        DiagnosticMessage: $"Koren sesije '{sessionRoot}' se nalazi van StateRoot-a '{normStateRoot}'."));
                }

                var relSession = normSessionRoot.Substring(normStateRoot.Length).TrimStart('/');
                var sessionSegments = relSession.Split('/', StringSplitOptions.RemoveEmptyEntries);

                var currentFd = rootFd;

                // Step-by-step traversal keeping kernel-validated descendant FDs alive
                foreach (var seg in sessionSegments)
                {
                    var segHow = new OpenHow
                    {
                        Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_CLOEXEC,
                        Mode = 0,
                        Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                    };

                    int nextFd = _posix.OpenAt2(currentFd, seg, ref segHow);
                    if (nextFd < 0)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Nije moguće bezbedno otvoriti segment '{seg}' kroz openat2. ZERO pathname fallback."));
                    }

                    openFds.Add(nextFd);

                    if (_posix.Fstat(nextFd, out var fdStat) != 0 || !fdStat.IsDirectory)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Segment '{seg}' nije validan direktorijum."));
                    }

                    if ((fdStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Segment '{seg}' ima neispravne permisije 0{Convert.ToString(fdStat.PermissionBits, 8)}."));
                    }

                    if (!CheckOwnership(fdStat, out var segOwnerErr))
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: false, ReparsePointCheck: false,
                            DiagnosticMessage: $"Segment '{seg}' {segOwnerErr}"));
                    }

                    currentFd = nextFd;
                }

                var sessionFd = currentFd;

                // 2. Validate all semantic areas from kernel-validated sessionFd
                var areaNames = new[] { "Raw", "Evidence", "Derived", "Exports" };
                foreach (var area in areaNames)
                {
                    var areaHow = new OpenHow
                    {
                        Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_CLOEXEC,
                        Mode = 0,
                        Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                    };

                    int areaFd = _posix.OpenAt2(sessionFd, area, ref areaHow);
                    if (areaFd < 0)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Podzona '{area}' ne postoji ili se ne može bezbedno otvoriti. ZERO pathname fallback."));
                    }

                    try
                    {
                        if (_posix.Fstat(areaFd, out var areaStat) != 0 || !areaStat.IsDirectory)
                        {
                            return Task.FromResult(new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: true, ReparsePointCheck: false,
                                DiagnosticMessage: $"Podzona '{area}' nije validan direktorijum."));
                        }

                        if ((areaStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0700)
                        {
                            return Task.FromResult(new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: true, ReparsePointCheck: false,
                                DiagnosticMessage: $"Podzona '{area}' ima neispravne permisije 0{Convert.ToString(areaStat.PermissionBits, 8)}."));
                        }

                        if (!CheckOwnership(areaStat, out var areaOwnerErr))
                        {
                            return Task.FromResult(new StorageProtectionObservation(
                                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                                StorageProtectionState.NotEstablished,
                                RootBoundaryValid: true, ReparsePointCheck: false,
                                DiagnosticMessage: $"Podzona '{area}' {areaOwnerErr}"));
                        }
                    }
                    finally
                    {
                        _posix.Close(areaFd);
                    }
                }

                // 3. R3-B & R3-C: Open and read layout.json from the SAME validated sessionFd
                var layoutHow = new OpenHow
                {
                    Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_CLOEXEC,
                    Mode = 0,
                    Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                };

                int layoutFd = _posix.OpenAt2(sessionFd, SessionLayoutDescriptor.FileName, ref layoutHow);
                if (layoutFd < 0)
                {
                    return Task.FromResult(new StorageProtectionObservation(
                        obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                        layout.StoragePolicyVersion, layout.StoragePolicyHash,
                        StorageProtectionState.NotEstablished,
                        RootBoundaryValid: true, ReparsePointCheck: false,
                        DiagnosticMessage: $"Nije moguće bezbedno otvoriti '{SessionLayoutDescriptor.FileName}' kroz openat2. ZERO pathname fallback."));
                }

                try
                {
                    if (_posix.Fstat(layoutFd, out var fdStat) != 0 || !fdStat.IsRegularFile)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Deskriptor '{SessionLayoutDescriptor.FileName}' nije validan regularni fajl."));
                    }

                    if ((fdStat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0600)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Deskriptor '{SessionLayoutDescriptor.FileName}' ima neispravne permisije 0{Convert.ToString(fdStat.PermissionBits, 8)} (zahteva se 0600)."));
                    }

                    if (!CheckOwnership(fdStat, out var layoutOwnerErr))
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Deskriptor '{SessionLayoutDescriptor.FileName}' {layoutOwnerErr}"));
                    }

                    if (fdStat.Size <= 0 || fdStat.Size > MaxLayoutDescriptorSizeBytes)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Deskriptor '{SessionLayoutDescriptor.FileName}' ima nevalidnu veličinu ({fdStat.Size} bajtova)."));
                    }

                    var buffer = new byte[(int)fdStat.Size];
                    if (!ReadExactly(layoutFd, buffer))
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: $"Neuspešno ili nepotpuno čitanje (short read/EOF/greška) deskriptora '{SessionLayoutDescriptor.FileName}'."));
                    }

                    var loaded = SessionLayoutDescriptor.FromCanonicalBytes(buffer);
                    if (loaded == null || loaded.SessionId != layout.SessionId)
                    {
                        return Task.FromResult(new StorageProtectionObservation(
                            obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                            layout.StoragePolicyVersion, layout.StoragePolicyHash,
                            StorageProtectionState.NotEstablished,
                            RootBoundaryValid: true, ReparsePointCheck: false,
                            DiagnosticMessage: loaded == null
                                ? $"Deskriptor '{SessionLayoutDescriptor.FileName}' nije validan JSON."
                                : $"Deskriptor sadrži neslaganje SessionId: '{loaded.SessionId}' umesto očekivanog '{layout.SessionId}'."));
                    }
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

            // All checks succeeded on validated FDs
            return Task.FromResult(new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.Established,
                RootBoundaryValid: true, ReparsePointCheck: true,
                PlatformSecurityDescriptorRef: $"POSIX:0700/0600:{_ownershipPolicy.PolicyName}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new StorageProtectionObservation(
                obsId, layout.SessionId, now, PlatformName, layout.LayoutVersion,
                layout.StoragePolicyVersion, layout.StoragePolicyHash,
                StorageProtectionState.NotEstablished,
                RootBoundaryValid: false, ReparsePointCheck: false,
                DiagnosticMessage: $"Greška pri verifikaciji Linux POSIX zaštite sesije: {ex.Message}"));
        }
    }

    private bool WriteAll(int fd, ReadOnlySpan<byte> data)
    {
        int total = 0;
        while (total < data.Length)
        {
            int n = _posix.Write(fd, data.Slice(total));
            if (n <= 0)
            {
                return false;
            }
            total += n;
        }
        return true;
    }

    private bool ReadExactly(int fd, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _posix.Read(fd, buffer.Slice(total));
            if (n <= 0)
            {
                return false;
            }
            total += n;
        }
        return true;
    }

    private bool CheckOwnership(PosixStat stat, out string? errorMessage)
    {
        if (_ownershipPolicy.EnforceExactOwnership)
        {
            if (_ownershipPolicy.ExpectedUid.HasValue && stat.Uid != _ownershipPolicy.ExpectedUid.Value)
            {
                errorMessage = $"ima neispravnog vlasnika UID {stat.Uid} (očekivano {_ownershipPolicy.ExpectedUid.Value}).";
                return false;
            }
            if (_ownershipPolicy.ExpectedGid.HasValue && stat.Gid != _ownershipPolicy.ExpectedGid.Value)
            {
                errorMessage = $"ima neispravnu grupu GID {stat.Gid} (očekivano {_ownershipPolicy.ExpectedGid.Value}).";
                return false;
            }
        }
        errorMessage = null;
        return true;
    }

    private static string NormalizePath(string path)
    {
        var norm = path.Replace('\\', '/').TrimEnd('/');
        if (norm.Length >= 2 && norm[1] == ':' && char.IsLetter(norm[0]))
        {
            norm = norm.Substring(2);
        }
        return norm;
    }
}
