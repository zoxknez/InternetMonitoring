namespace IEM.Linux.Storage;

public enum AtomicPublishStatus
{
    Success,
    Collision
}

public sealed record AtomicPublishResult(
    AtomicPublishStatus Status,
    int FinalFd = -1,
    PosixStat Stat = default) : IDisposable
{
    public bool IsSuccess => Status == AtomicPublishStatus.Success;
    public bool IsCollision => Status == AtomicPublishStatus.Collision;

    public void Dispose()
    {
        if (FinalFd >= 0)
        {
            // Note: caller may take ownership of FinalFd or dispose result to close it
        }
    }
}

/// <summary>
/// Domain-agnostic, crash-safe atomic file publisher using POSIX O_CREAT|O_EXCL, Fchmod,
/// Fstat verification, Fsync, RENAME_NOREPLACE, and directory durability.
/// </summary>
public static class LinuxAtomicFilePublisher
{
    public static AtomicPublishResult PublishAtomically(
        ILinuxPosixStorageApi posix,
        int parentFd,
        string targetFileName,
        ReadOnlySpan<byte> content,
        int targetMode,
        LinuxStorageOwnershipPolicy ownership,
        string tempPrefix = "pub")
    {
        ArgumentNullException.ThrowIfNull(posix);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFileName);

        var resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH |
                      LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS |
                      LinuxPosixStorageConstants.RESOLVE_NO_XDEV |
                      LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS;

        // 1. Authoritative existence check
        int statRes = posix.FstatAt(parentFd, targetFileName, out var existingStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW);
        if (statRes == 0)
        {
            return new AtomicPublishResult(AtomicPublishStatus.Collision, -1, existingStat);
        }

        int errno = posix.GetLastErrno();
        if (errno != LinuxPosixStorageConstants.ENOENT)
        {
            throw new InvalidOperationException(
                $"Authoritative lookup of '{targetFileName}' failed with errno {errno} (not ENOENT). Publication aborted.");
        }

        // 2. Create unique temporary file
        var tempName = $".tmp.{tempPrefix}.{Guid.NewGuid():N}";
        var tempHow = new OpenHow
        {
            Flags = (ulong)(LinuxPosixStorageConstants.O_CREAT | LinuxPosixStorageConstants.O_EXCL | LinuxPosixStorageConstants.O_RDWR | LinuxPosixStorageConstants.O_CLOEXEC),
            Mode = (ulong)targetMode,
            Resolve = resolve
        };

        int tempFd = posix.OpenAt2(parentFd, tempName, ref tempHow);
        if (tempFd < 0)
        {
            int openErrno = posix.GetLastErrno();
            throw new InvalidOperationException($"Failed to create temporary file '{tempName}' via openat2 (errno {openErrno}).");
        }

        try
        {
            // 3. Write all content
            if (!WriteAll(posix, tempFd, content))
            {
                throw new InvalidOperationException($"Failed to write complete content ({content.Length} bytes) to temporary file '{tempName}'.");
            }

            // 4. Defeat umask drift via explicit fchmod
            if (posix.Fchmod(tempFd, targetMode) != 0)
            {
                throw new InvalidOperationException($"Failed to set permissions 0{Convert.ToString(targetMode & 0xFFF, 8)} on temporary file '{tempName}'.");
            }

            // 5. Pre-publish validation on same FD
            if (posix.Fstat(tempFd, out var tempStat) != 0 || !tempStat.IsRegularFile)
            {
                throw new InvalidOperationException($"Temporary file '{tempName}' is not a valid regular file.");
            }

            if ((tempStat.PermissionBits & 0xFFF) != (targetMode & 0xFFF))
            {
                throw new InvalidOperationException(
                    $"Temporary file '{tempName}' permissions 0{Convert.ToString(tempStat.PermissionBits, 8)} do not match required 0{Convert.ToString(targetMode & 0xFFF, 8)}.");
            }

            if (!CheckOwnership(tempStat, ownership, out var tempOwnerErr))
            {
                throw new InvalidOperationException($"Temporary file '{tempName}' {tempOwnerErr}");
            }

            if (tempStat.Size != content.Length)
            {
                throw new InvalidOperationException(
                    $"Temporary file '{tempName}' size {tempStat.Size} does not match expected length {content.Length}.");
            }

            // 6. Flush file data to disk
            if (posix.Fsync(tempFd) != 0)
            {
                throw new InvalidOperationException($"fsync failed on temporary file '{tempName}'.");
            }
        }
        catch
        {
            posix.Close(tempFd);
            posix.UnlinkAt(parentFd, tempName, 0);
            throw;
        }

        posix.Close(tempFd);

        // 7. Atomic publication via RENAME_NOREPLACE
        int ren = posix.RenameAt2(parentFd, tempName, parentFd, targetFileName, LinuxPosixStorageConstants.RENAME_NOREPLACE);
        if (ren != 0)
        {
            int renErrno = posix.GetLastErrno();
            posix.UnlinkAt(parentFd, tempName, 0);

            // If and ONLY if EEXIST, treat as legitimate race collision with winner
            if (renErrno == LinuxPosixStorageConstants.EEXIST)
            {
                if (posix.FstatAt(parentFd, targetFileName, out var winnerStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW) == 0)
                {
                    return new AtomicPublishResult(AtomicPublishStatus.Collision, -1, winnerStat);
                }
            }

            throw new InvalidOperationException($"renameat2 failed to publish '{targetFileName}' (errno {renErrno}).");
        }

        // 8. Parent directory durability fsync
        if (posix.Fsync(parentFd) != 0)
        {
            throw new InvalidOperationException(
                $"File '{targetFileName}' was published but parent directory durability could not be established.");
        }

        // 9. Re-open final file via openat2 for same-FD validation
        var finalHow = new OpenHow
        {
            Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_CLOEXEC,
            Mode = 0,
            Resolve = resolve
        };

        int finalFd = posix.OpenAt2(parentFd, targetFileName, ref finalHow);
        if (finalFd < 0)
        {
            throw new InvalidOperationException($"Failed to reopen published file '{targetFileName}' via openat2.");
        }

        if (posix.Fstat(finalFd, out var finalStat) != 0 || !finalStat.IsRegularFile)
        {
            posix.Close(finalFd);
            throw new InvalidOperationException($"Published file '{targetFileName}' is not a valid regular file.");
        }

        return new AtomicPublishResult(AtomicPublishStatus.Success, finalFd, finalStat);
    }

    public static bool CleanupStaleTempFile(
        ILinuxPosixStorageApi posix,
        int parentFd,
        string tempFileName,
        LinuxStorageOwnershipPolicy ownership)
    {
        ArgumentNullException.ThrowIfNull(posix);
        if (string.IsNullOrWhiteSpace(tempFileName) || !tempFileName.StartsWith(".tmp.") || tempFileName.Contains('/'))
        {
            return false;
        }

        var resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH |
                      LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS |
                      LinuxPosixStorageConstants.RESOLVE_NO_XDEV |
                      LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS;

        var openHow = new OpenHow
        {
            Flags = LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_CLOEXEC,
            Mode = 0,
            Resolve = resolve
        };

        int tempFd = posix.OpenAt2(parentFd, tempFileName, ref openHow);
        if (tempFd < 0)
        {
            return false; // Cannot open safely or is symlink
        }

        try
        {
            if (posix.Fstat(tempFd, out var stat) != 0 || !stat.IsRegularFile)
            {
                return false;
            }

            if (!CheckOwnership(stat, ownership, out _))
            {
                return false; // Foreign owned: NEVER delete
            }

            if ((stat.PermissionBits & ~0x1FF) != 0) // No setuid/setgid/sticky bits
            {
                return false;
            }
        }
        finally
        {
            posix.Close(tempFd);
        }

        // Safe app-owned temp file confirmed: unlink
        if (posix.UnlinkAt(parentFd, tempFileName, 0) == 0)
        {
            posix.Fsync(parentFd);
            return true;
        }

        return false;
    }

    private static bool WriteAll(ILinuxPosixStorageApi posix, int fd, ReadOnlySpan<byte> data)
    {
        int total = 0;
        while (total < data.Length)
        {
            int n = posix.Write(fd, data.Slice(total));
            if (n <= 0)
            {
                return false;
            }
            total += n;
        }
        return true;
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
