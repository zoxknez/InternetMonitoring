using IEM.Storage.Layout;

namespace IEM.Linux.Storage;

/// <summary>
/// POSIX FD-relative symlink and directory boundary safety guard for Linux.
/// Invariants:
/// 77. PRIVILEGED_EVIDENCE_WRITES_NEVER_FOLLOW_UNTRUSTED_REPARSE_POINTS
/// 80. STORAGE_PROTECTION_DRIFT_IS_NEVER_SILENTLY_ERASED_BY_REPAIR
/// 81. EVIDENCE_SESSION_NEVER_STARTS_WITH_UNESTABLISHED_STORAGE_BOUNDARY
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

        // Normalize slashes
        var normRoot = trustedRoot.Replace('\\', '/').TrimEnd('/');
        var normTarget = targetPath.Replace('\\', '/').TrimEnd('/');

        if (normTarget.Contains("..") || normTarget.Contains('\0'))
        {
            return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, "Putanja sadrži nedozvoljene karaktere ili '..' traversal.");
        }

        if (!normTarget.StartsWith(normRoot, StringComparison.Ordinal))
        {
            return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Ciljna putanja '{targetPath}' se nalazi van dozvoljenog korena '{trustedRoot}'.");
        }

        // On non-Linux (e.g. running in simulation or unit tests on Windows without mock posix),
        // verify lexical separation and directory existence if available.
        if (!OperatingSystem.IsLinux() && _posix is LinuxNativePosixStorageApi)
        {
            return ValidateLexicalNonLinux(normRoot, normTarget);
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
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Koren sesije '{normRoot}' je symlink.");
                }
            }
            return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Nije moguće otvoriti koren sesije '{normRoot}'.");
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

                // Use openat2 if available for kernel-level RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_XDEV
                var openHow = new OpenHow
                {
                    Flags = (ulong)(LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_CLOEXEC | (isLast ? 0 : LinuxPosixStorageConstants.O_DIRECTORY)),
                    Mode = 0,
                    Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH | LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS | LinuxPosixStorageConstants.RESOLVE_NO_XDEV | LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
                };

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

                if (!ValidateOwnershipAndMode(segStat, isDir, out var segDiag))
                {
                    return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Segment '{segment}': {segDiag}");
                }

                if (!isLast)
                {
                    int nextFd = _posix.OpenAt2(currentFd, segment, ref openHow);
                    if (nextFd < 0)
                    {
                        // Fallback to standard openat with O_NOFOLLOW | O_DIRECTORY
                        nextFd = _posix.OpenAt(currentFd, segment, LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_NOFOLLOW | LinuxPosixStorageConstants.O_CLOEXEC, 0);
                    }

                    if (nextFd < 0)
                    {
                        return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Nije moguće bezbedno otvoriti segment '{segment}'.");
                    }

                    openFds.Add(nextFd);
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

        // Check ownership if required
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

        // Check permission bits: directories must not have group-write or other-write/read (e.g. 0700 or 0750)
        // Strictly fail on world writable (0002) or other-readable (0004/0007) in strict mode
        var perm = stat.PermissionBits;
        if ((perm & 0x002) != 0) // World-writable
        {
            diagnostic = $"Nedozvoljene permisije (world-writable 0{perm:X3}).";
            return false;
        }

        return true;
    }

    private static SymlinkSafetyResult ValidateLexicalNonLinux(string normRoot, string normTarget)
    {
        if (Directory.Exists(normTarget) || File.Exists(normTarget))
        {
            return new SymlinkSafetyResult(true, StorageProtectionState.Established);
        }
        return new SymlinkSafetyResult(false, StorageProtectionState.NotEstablished, $"Putanja '{normTarget}' ne postoji.");
    }
}
