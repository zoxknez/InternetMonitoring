using IEM.Storage.Layout;

namespace IEM.Linux.Storage;

/// <summary>
/// POSIX FD-relative symlink and directory boundary safety guard for Linux.
/// Invariants:
/// 77. PRIVILEGED_EVIDENCE_WRITES_NEVER_FOLLOW_UNTRUSTED_REPARSE_POINTS
/// 80. STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR
/// 81. EVIDENCE_SESSION_NEVER_STARTS_WITH_UNESTABLISHED_STORAGE_BOUNDARY
/// 82. FILESYSTEM_SECURITY_MECHANISM_IS_PLATFORM_PROVENANCE_NOT_EVIDENCE_SEMANTICS
/// </summary>
public sealed class LinuxSymlinkGuard : ISymlinkSafetyGuard
{
    private readonly ILinuxPosixStorageApi _posix;
    private readonly LinuxStorageOwnershipPolicy _ownershipPolicy;

    public LinuxSymlinkGuard(
        ILinuxPosixStorageApi? posix = null,
        LinuxStorageOwnershipPolicy? ownershipPolicy = null)
    {
        _posix = posix ?? new LinuxNativePosixStorageApi();
        _ownershipPolicy = ownershipPolicy ?? LinuxStorageOwnershipPolicy.SystemDefault;
    }

    public SymlinkSafetyResult ValidatePath(string trustedRoot, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(trustedRoot) || string.IsNullOrWhiteSpace(targetPath))
        {
            return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, "Putanja je prazna ili nevalidna.");
        }

        // Normalize slashes and strip Windows drive prefix if present during cross-platform testing
        var normRoot = trustedRoot.Replace('\\', '/').TrimEnd('/');
        var normTarget = targetPath.Replace('\\', '/').TrimEnd('/');

        if (normRoot.Length >= 2 && normRoot[1] == ':' && char.IsLetter(normRoot[0]))
        {
            normRoot = normRoot.Substring(2);
        }
        if (normTarget.Length >= 2 && normTarget[1] == ':' && char.IsLetter(normTarget[0]))
        {
            normTarget = normTarget.Substring(2);
        }

        if (normTarget.Contains("..") || normTarget.Contains('\0'))
        {
            return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, "Putanja sadrži nedozvoljene karaktere ili '..' traversal.");
        }

        // Strict prefix validation (prevent prefix collision such as /a/b vs /a/bad)
        if (normTarget != normRoot && !normTarget.StartsWith(normRoot + "/", StringComparison.Ordinal))
        {
            return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Ciljna putanja '{targetPath}' se nalazi van dozvoljenog korena '{trustedRoot}'.");
        }

        return ValidatePosixFdRelative(normRoot, normTarget);
    }

    private SymlinkSafetyResult ValidatePosixFdRelative(string normRoot, string normTarget)
    {
        // 1. Open and inspect trusted root
        var rootFd = _posix.Open(normRoot, LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_NOFOLLOW | LinuxPosixStorageConstants.O_CLOEXEC, 0);
        if (rootFd < 0)
        {
            // Inspect root stat to see if it was a symlink or missing
            if (_posix.FstatAt(LinuxPosixStorageConstants.AT_FDCWD, normRoot, out var rootStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0)
            {
                if (rootStat.IsSymlink)
                {
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Koren '{normRoot}' je symlink.");
                }
            }
            return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Nije moguće otvoriti koren '{normRoot}'.");
        }

        var openFds = new List<int> { rootFd };

        try
        {
            if (_posix.Fstat(rootFd, out var rootStat) != 0 || !rootStat.IsDirectory)
            {
                return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Koren '{normRoot}' nije validan direktorijum.");
            }

            if (!ValidateOwnershipAndMode(rootStat, isDirectory: true, out var rootDiag))
            {
                return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Koren '{normRoot}': {rootDiag}");
            }

            if (normRoot == normTarget)
            {
                return new SymlinkSafetyResult(true, StorageProtectionState.Established);
            }

            var relPath = normTarget.Substring(normRoot.Length).TrimStart('/');
            var segments = relPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            var currentFd = rootFd;

            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                bool isLast = (i == segments.Length - 1);

                // Stat segment relative to currentFd without following symlinks
                if (_posix.FstatAt(currentFd, segment, out var segStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) != 0)
                {
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Segment '{segment}' ne postoji ili nije čitljiv.");
                }

                if (segStat.IsSymlink)
                {
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Detektovan nedozvoljen symlink na segmentu '{segment}'.");
                }

                bool isDir = segStat.IsDirectory;
                if (!isLast && !isDir)
                {
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Međusegment '{segment}' nije direktorijum.");
                }

                // Strict openat2 validation on EVERY component (including final leaf component)
                // Enforces kernel RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_XDEV | RESOLVE_NO_MAGICLINKS
                var openHow = new OpenHow
                {
                    Flags = (ulong)(LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_CLOEXEC | (isDir ? LinuxPosixStorageConstants.O_DIRECTORY : 0)),
                    Mode = 0,
                    Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                };

                int nextFd = _posix.OpenAt2(currentFd, segment, ref openHow);
                if (nextFd < 0)
                {
                    // R1-E: Never fallback to OpenAt! Any failure (ENOSYS, EXDEV, ELOOP, etc.) must fail-closed.
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Nije moguće bezbedno otvoriti segment '{segment}' kroz openat2 (rezolucija odbijena ili nepodržana).");
                }

                openFds.Add(nextFd);

                // Stat opened FD to verify mode and ownership on the exact opened kernel descriptor
                if (_posix.Fstat(nextFd, out var fdStat) != 0)
                {
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"fstat nije uspeo na otvorenom deskriptoru za segment '{segment}'.");
                }

                if (!ValidateOwnershipAndMode(fdStat, isDir, out var segDiag))
                {
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Segment '{segment}': {segDiag}");
                }

                if (!isLast)
                {
                    currentFd = nextFd;
                }
            }

            return new SymlinkSafetyResult(true, StorageProtectionState.Established);
        }
        finally
        {
            foreach (var fd in openFds)
            {
                _posix.Close(fd);
            }
        }
    }

    private bool ValidateOwnershipAndMode(PosixStat stat, bool isDirectory, out string? diagnostic)
    {
        diagnostic = null;

        // 1. Exact UID/GID ownership validation
        if (_ownershipPolicy.EnforceExactOwnership)
        {
            if (_ownershipPolicy.ExpectedUid.HasValue && stat.Uid != _ownershipPolicy.ExpectedUid.Value)
            {
                diagnostic = $"Neispravan vlasnik UID {stat.Uid} (očekivano {_ownershipPolicy.ExpectedUid.Value}).";
                return false;
            }

            if (_ownershipPolicy.ExpectedGid.HasValue && stat.Gid != _ownershipPolicy.ExpectedGid.Value)
            {
                diagnostic = $"Neispravna grupa GID {stat.Gid} (očekivano {_ownershipPolicy.ExpectedGid.Value}).";
                return false;
            }
        }

        // 2. R1-C: Exact mode truth (directories == 0700, layout.json/files == 0600)
        var perm = stat.PermissionBits;
        if (isDirectory)
        {
            if (perm != LinuxPosixStorageConstants.Mode0700) // 0x1C0 (0700 octal)
            {
                diagnostic = $"Nedozvoljene permisije direktorijuma 0{Convert.ToString(perm, 8)} (zahteva se striktno 0700).";
                return false;
            }
        }
        else
        {
            if (perm != LinuxPosixStorageConstants.Mode0600) // 0x180 (0600 octal)
            {
                diagnostic = $"Nedozvoljene permisije fajla 0{Convert.ToString(perm, 8)} (zahteva se striktno 0600).";
                return false;
            }
        }

        return true;
    }
}
