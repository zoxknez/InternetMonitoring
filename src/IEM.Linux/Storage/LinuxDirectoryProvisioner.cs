namespace IEM.Linux.Storage;

/// <summary>
/// Crash-safe, TOCTOU-resistant directory provisioner for canonical Linux storage.
/// Invariants:
/// - Authoritative absence (ENOENT) required before creation
/// - Creation mode defeated umask via explicit fchmod(0700)
/// - Exact ownership (UID/GID) and mode verification
/// - Dual fsync (child newFd + parent parentFd) for durability on creation AND existing verification
/// - Verify-only for existing directories (ZERO chmod/chown repair)
/// </summary>
public static class LinuxDirectoryProvisioner
{
    public static int ProvisionOrVerifyDirectory(
        ILinuxPosixStorageApi posix,
        int parentFd,
        string dirName,
        LinuxStorageOwnershipPolicy ownership,
        int mode = LinuxPosixStorageConstants.Mode0700)
    {
        ArgumentNullException.ThrowIfNull(posix);
        ArgumentException.ThrowIfNullOrWhiteSpace(dirName);

        var resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH |
                      LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS |
                      LinuxPosixStorageConstants.RESOLVE_NO_XDEV |
                      LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS;

        var openHow = new OpenHow
        {
            Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_CLOEXEC,
            Mode = 0,
            Resolve = resolve
        };

        // 1. Authoritative existence check
        int statRes = posix.FstatAt(parentFd, dirName, out var existingStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW);
        if (statRes == 0)
        {
            // Existing directory: VERIFY-ONLY (NO REPAIR)
            int dirFd = posix.OpenAt2(parentFd, dirName, ref openHow);
            if (dirFd < 0)
            {
                throw new InvalidOperationException($"Failed to open existing directory '{dirName}' securely via openat2.");
            }

            try
            {
                if (posix.Fstat(dirFd, out var stat) != 0 || !stat.IsDirectory)
                {
                    throw new InvalidOperationException($"Existing path '{dirName}' is not a directory.");
                }

                if ((stat.PermissionBits & 0xFFF) != (mode & 0xFFF))
                {
                    throw new InvalidOperationException(
                        $"Directory '{dirName}' permissions 0{Convert.ToString(stat.PermissionBits, 8)} do not match required 0{Convert.ToString(mode & 0xFFF, 8)}.");
                }

                if (!CheckOwnership(stat, ownership, out var ownerErr))
                {
                    throw new InvalidOperationException($"Directory '{dirName}' {ownerErr}");
                }

                // Durability confirmation for existing directory (R1-A & 8D-F recovery)
                if (posix.Fsync(dirFd) != 0)
                {
                    throw new InvalidOperationException($"fsync failed on existing directory '{dirName}'.");
                }

                if (posix.Fsync(parentFd) != 0)
                {
                    throw new InvalidOperationException($"fsync failed on parent directory for existing entry '{dirName}'.");
                }

                return dirFd;
            }
            catch
            {
                posix.Close(dirFd);
                throw;
            }
        }

        // Check if absence is authoritative (ENOENT)
        int errno = posix.GetLastErrno();
        if (errno != LinuxPosixStorageConstants.ENOENT)
        {
            throw new InvalidOperationException(
                $"Authoritative lookup of directory '{dirName}' failed with errno {errno} (not ENOENT). Creation denied.");
        }

        // 2. Authoritative ENOENT: Create new directory
        if (posix.MkdirAt(parentFd, dirName, mode) != 0)
        {
            int mkdirErrno = posix.GetLastErrno();
            throw new InvalidOperationException($"mkdirat failed for '{dirName}' with errno {mkdirErrno}.");
        }

        int newFd = posix.OpenAt2(parentFd, dirName, ref openHow);
        if (newFd < 0)
        {
            throw new InvalidOperationException($"Failed to open newly created directory '{dirName}' via openat2.");
        }

        try
        {
            // Defeat umask drift on newly created directory
            if (posix.Fchmod(newFd, mode) != 0)
            {
                throw new InvalidOperationException($"Failed to set permissions 0{Convert.ToString(mode & 0xFFF, 8)} on new directory '{dirName}'.");
            }

            if (posix.Fstat(newFd, out var newStat) != 0 || !newStat.IsDirectory)
            {
                throw new InvalidOperationException($"Newly created path '{dirName}' is not a valid directory.");
            }

            if ((newStat.PermissionBits & 0xFFF) != (mode & 0xFFF))
            {
                throw new InvalidOperationException(
                    $"New directory '{dirName}' permissions 0{Convert.ToString(newStat.PermissionBits, 8)} are invalid.");
            }

            if (!CheckOwnership(newStat, ownership, out var newOwnerErr))
            {
                throw new InvalidOperationException($"New directory '{dirName}' {newOwnerErr}");
            }

            // Dual fsync for durability: child directory first, then parent directory entry
            if (posix.Fsync(newFd) != 0)
            {
                throw new InvalidOperationException($"fsync failed on new directory '{dirName}'.");
            }

            if (posix.Fsync(parentFd) != 0)
            {
                throw new InvalidOperationException($"fsync failed on parent directory for entry '{dirName}'.");
            }

            return newFd;
        }
        catch
        {
            posix.Close(newFd);
            throw;
        }
    }

    private static bool CheckOwnership(PosixStat stat, LinuxStorageOwnershipPolicy policy, out string? errorMessage)
    {
        if (policy.EnforceExactOwnership)
        {
            if (policy.ExpectedUid.HasValue && stat.Uid != policy.ExpectedUid.Value)
            {
                errorMessage = $"UID mismatch: found {stat.Uid}, expected {policy.ExpectedUid.Value}.";
                return false;
            }
            if (policy.ExpectedGid.HasValue && stat.Gid != policy.ExpectedGid.Value)
            {
                errorMessage = $"GID mismatch: found {stat.Gid}, expected {policy.ExpectedGid.Value}.";
                return false;
            }
        }
        errorMessage = null;
        return true;
    }
}
