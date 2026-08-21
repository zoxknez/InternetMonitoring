using System.Text.RegularExpressions;

namespace IEM.Linux.Storage;

public enum AtomicPublishStatus
{
    Success,
    Collision
}

public sealed class AtomicPublishResult : IDisposable
{
    private readonly ILinuxPosixStorageApi? _posix;
    private int _finalFd;

    public AtomicPublishStatus Status { get; }
    public PosixStat Stat { get; }

    public int FinalFd => _finalFd;
    public bool IsSuccess => Status == AtomicPublishStatus.Success;
    public bool IsCollision => Status == AtomicPublishStatus.Collision;

    public AtomicPublishResult(
        AtomicPublishStatus status,
        int finalFd = -1,
        PosixStat stat = default,
        ILinuxPosixStorageApi? posix = null)
    {
        Status = status;
        _finalFd = finalFd;
        Stat = stat;
        _posix = posix;
    }

    /// <summary>
    /// Transfers ownership of the open file descriptor to the caller.
    /// After this call, Dispose() will no longer close the descriptor.
    /// </summary>
    public int TakeFinalFd()
    {
        int fd = _finalFd;
        _finalFd = -1;
        return fd;
    }

    public void Dispose()
    {
        if (_finalFd >= 0)
        {
            _posix?.Close(_finalFd);
            _finalFd = -1;
        }
    }
}

public sealed class LinuxNamespaceLockScope : IDisposable
{
    private readonly ILinuxPosixStorageApi _posix;
    private int _lockFd;

    public int LockFd => _lockFd;

    public LinuxNamespaceLockScope(ILinuxPosixStorageApi posix, int lockFd)
    {
        _posix = posix;
        _lockFd = lockFd;
    }

    public void Dispose()
    {
        if (_lockFd >= 0)
        {
            _posix.Flock(_lockFd, LinuxPosixStorageConstants.LOCK_UN);
            _posix.Close(_lockFd);
            _lockFd = -1;
        }
    }
}

/// <summary>
/// Domain-agnostic, crash-safe atomic file publisher using POSIX O_CREAT|O_EXCL, Fchmod,
/// Fstat verification, Fsync, RENAME_NOREPLACE, flock concurrency guard, and directory durability.
/// Phase 3.1-8D-R3: Namespace-level transaction serialization via .iem-storage.lock.
/// </summary>
public static partial class LinuxAtomicFilePublisher
{
    [GeneratedRegex(@"^\.tmp\.([a-zA-Z0-9_-]+)\.([0-9a-f]{32})$")]
    private static partial Regex TempFileNameRegex();

    public static LinuxNamespaceLockScope? AcquireNamespaceLock(
        ILinuxPosixStorageApi posix,
        int parentFd,
        LinuxStorageOwnershipPolicy ownership,
        string lockFileName = LinuxStoragePaths.NamespaceLockFileName,
        bool failClosedOnContention = true)
    {
        ArgumentNullException.ThrowIfNull(posix);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFileName);

        var resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH |
                      LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS |
                      LinuxPosixStorageConstants.RESOLVE_NO_XDEV |
                      LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS;

        bool createdByThisCall = false;
        int statRes = posix.FstatAt(parentFd, lockFileName, out var existingStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW);
        int lockFd;

        var openExistingHow = new OpenHow
        {
            Flags = (ulong)(LinuxPosixStorageConstants.O_RDWR | LinuxPosixStorageConstants.O_CLOEXEC),
            Mode = 0,
            Resolve = resolve
        };

        if (statRes == 0)
        {
            // Existing lock file: verify-only (NO REPAIR)
            lockFd = posix.OpenAt2(parentFd, lockFileName, ref openExistingHow);
            if (lockFd < 0)
            {
                throw new InvalidOperationException($"Failed to open existing namespace lock file '{lockFileName}' securely via openat2.");
            }
        }
        else
        {
            int errno = posix.GetLastErrno();
            if (errno != LinuxPosixStorageConstants.ENOENT)
            {
                throw new InvalidOperationException(
                    $"Authoritative lookup of namespace lock '{lockFileName}' failed with errno {errno} (not ENOENT).");
            }

            // Authoritative ENOENT: create new lock file
            var createHow = new OpenHow
            {
                Flags = (ulong)(LinuxPosixStorageConstants.O_CREAT | LinuxPosixStorageConstants.O_EXCL | LinuxPosixStorageConstants.O_RDWR | LinuxPosixStorageConstants.O_CLOEXEC),
                Mode = (ulong)LinuxPosixStorageConstants.Mode0600,
                Resolve = resolve
            };

            lockFd = posix.OpenAt2(parentFd, lockFileName, ref createHow);
            if (lockFd < 0)
            {
                int openErrno = posix.GetLastErrno();
                if (openErrno == LinuxPosixStorageConstants.EEXIST)
                {
                    // Race winner created lock file: fall back to VERIFY-ONLY (NO REPAIR)
                    lockFd = posix.OpenAt2(parentFd, lockFileName, ref openExistingHow);
                    if (lockFd < 0)
                    {
                        throw new InvalidOperationException($"Failed to open namespace lock file '{lockFileName}' after race collision.");
                    }
                    createdByThisCall = false;
                }
                else
                {
                    throw new InvalidOperationException($"Failed to create namespace lock file '{lockFileName}' via openat2 (errno {openErrno}).");
                }
            }
            else
            {
                createdByThisCall = true;
            }
        }

        try
        {
            if (createdByThisCall)
            {
                // Newly created by THIS transaction: defeat umask drift
                if (posix.Fchmod(lockFd, LinuxPosixStorageConstants.Mode0600) != 0)
                {
                    throw new InvalidOperationException($"Failed to set permissions 0600 on namespace lock '{lockFileName}'.");
                }

                if (posix.Fstat(lockFd, out var stat) != 0 || !stat.IsRegularFile)
                {
                    throw new InvalidOperationException($"Newly created namespace lock '{lockFileName}' is not a valid regular file.");
                }

                if ((stat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0600)
                {
                    throw new InvalidOperationException(
                        $"Newly created namespace lock '{lockFileName}' has invalid mode 0{Convert.ToString(stat.PermissionBits, 8)}.");
                }

                if (!CheckOwnership(stat, ownership, out var ownerErr))
                {
                    throw new InvalidOperationException($"Newly created namespace lock '{lockFileName}' {ownerErr}");
                }

                if (posix.Fsync(lockFd) != 0 || posix.Fsync(parentFd) != 0)
                {
                    throw new InvalidOperationException($"fsync failed during namespace lock '{lockFileName}' creation.");
                }
            }
            else
            {
                // Existing lock file or created by race winner: VERIFY ONLY (ZERO REPAIR)
                if (posix.Fstat(lockFd, out var stat) != 0 || !stat.IsRegularFile)
                {
                    throw new InvalidOperationException($"Namespace lock '{lockFileName}' is not a regular file.");
                }

                if ((stat.PermissionBits & 0xFFF) != LinuxPosixStorageConstants.Mode0600)
                {
                    throw new InvalidOperationException(
                        $"Namespace lock '{lockFileName}' permissions 0{Convert.ToString(stat.PermissionBits, 8)} are invalid (must be exact 0600).");
                }

                if (!CheckOwnership(stat, ownership, out var ownerErr))
                {
                    throw new InvalidOperationException($"Namespace lock '{lockFileName}' {ownerErr}");
                }
            }
        }
        catch
        {
            posix.Close(lockFd);
            throw;
        }

        // Acquire exclusive non-blocking advisory lock
        if (posix.Flock(lockFd, LinuxPosixStorageConstants.LOCK_EX | LinuxPosixStorageConstants.LOCK_NB) != 0)
        {
            int flockErrno = posix.GetLastErrno();
            posix.Close(lockFd);
            if (!failClosedOnContention && (flockErrno == LinuxPosixStorageConstants.EWOULDBLOCK || flockErrno == LinuxPosixStorageConstants.EAGAIN))
            {
                return null;
            }
            throw new InvalidOperationException(
                $"Failed to acquire exclusive namespace lock for '{lockFileName}' (errno {flockErrno}).");
        }

        return new LinuxNamespaceLockScope(posix, lockFd);
    }

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
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPrefix);

        // R3-B: Acquire namespace-level transaction lock BEFORE target lookup and temp creation
        using var nsLock = AcquireNamespaceLock(posix, parentFd, ownership, LinuxStoragePaths.NamespaceLockFileName, failClosedOnContention: true);

        var resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH |
                      LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS |
                      LinuxPosixStorageConstants.RESOLVE_NO_XDEV |
                      LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS;

        // 1. Authoritative existence check
        int statRes = posix.FstatAt(parentFd, targetFileName, out var existingStat, LinuxPosixStorageConstants.AT_SYMLINK_NOFOLLOW);
        if (statRes == 0)
        {
            return new AtomicPublishResult(AtomicPublishStatus.Collision, -1, existingStat, posix);
        }

        int errno = posix.GetLastErrno();
        if (errno != LinuxPosixStorageConstants.ENOENT)
        {
            throw new InvalidOperationException(
                $"Authoritative lookup of '{targetFileName}' failed with errno {errno} (not ENOENT). Publication aborted.");
        }

        // 2. Create unique temporary file with strict grammar: .tmp.<prefix>.<32-hex-guid>
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
            // R2-A: Acquire exclusive advisory lock on tempFd (defense-in-depth)
            if (posix.Flock(tempFd, LinuxPosixStorageConstants.LOCK_EX | LinuxPosixStorageConstants.LOCK_NB) != 0)
            {
                int flockErrno = posix.GetLastErrno();
                throw new InvalidOperationException($"Failed to acquire exclusive publication lock for '{tempName}' (errno {flockErrno}).");
            }

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

            // R2-B & R3-D: Atomic publication via RENAME_NOREPLACE with locks held
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
                        return new AtomicPublishResult(AtomicPublishStatus.Collision, -1, winnerStat, posix);
                    }
                }

                throw new InvalidOperationException($"renameat2 failed to publish '{targetFileName}' (errno {renErrno}).");
            }

            // R2-B & R3-D: Parent directory durability fsync while locks are held
            if (posix.Fsync(parentFd) != 0)
            {
                throw new InvalidOperationException(
                    $"File '{targetFileName}' was published but parent directory durability could not be established.");
            }
        }
        catch
        {
            posix.UnlinkAt(parentFd, tempName, 0);
            throw;
        }
        finally
        {
            posix.Close(tempFd);
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

        return new AtomicPublishResult(AtomicPublishStatus.Success, finalFd, finalStat, posix);
    }

    public static bool CleanupStaleTempFile(
        ILinuxPosixStorageApi posix,
        int parentFd,
        string tempFileName,
        string expectedPrefix,
        LinuxStorageOwnershipPolicy ownership,
        int expectedMode = LinuxPosixStorageConstants.Mode0600)
    {
        ArgumentNullException.ThrowIfNull(posix);
        if (string.IsNullOrWhiteSpace(tempFileName) || string.IsNullOrWhiteSpace(expectedPrefix))
        {
            return false;
        }

        // R1-C: Strict temp grammar check (.tmp.<expectedPrefix>.<32hex>)
        var match = TempFileNameRegex().Match(tempFileName);
        if (!match.Success || !string.Equals(match.Groups[1].Value, expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // R3-C: Acquire namespace lock first. If held by active publisher -> NEVER scavenge!
        using var nsLock = AcquireNamespaceLock(posix, parentFd, ownership, LinuxStoragePaths.NamespaceLockFileName, failClosedOnContention: false);
        if (nsLock == null)
        {
            return false; // Active publisher holds namespace lock
        }

        var resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH |
                      LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS |
                      LinuxPosixStorageConstants.RESOLVE_NO_XDEV |
                      LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS;

        var openHow = new OpenHow
        {
            Flags = LinuxPosixStorageConstants.O_RDWR | LinuxPosixStorageConstants.O_CLOEXEC,
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
            // R1-F: Staleness / concurrency check via exclusive non-blocking lock on tempFd
            if (posix.Flock(tempFd, LinuxPosixStorageConstants.LOCK_EX | LinuxPosixStorageConstants.LOCK_NB) != 0)
            {
                // Lock held by active concurrent publisher -> NEVER delete
                return false;
            }

            if (posix.Fstat(tempFd, out var stat) != 0 || !stat.IsRegularFile)
            {
                return false;
            }

            // R1-D: Exact ownership and exact mode check
            if (!CheckOwnership(stat, ownership, out _))
            {
                return false; // Foreign owned: NEVER delete
            }

            if ((stat.PermissionBits & 0xFFF) != (expectedMode & 0xFFF))
            {
                return false; // Mode drift: NEVER delete blindly
            }

            // R2-C & R3-D: Unlink while locks are STILL held
            if (posix.UnlinkAt(parentFd, tempFileName, 0) != 0)
            {
                return false;
            }

            if (posix.Fsync(parentFd) != 0)
            {
                throw new InvalidOperationException(
                    $"Temporary file '{tempFileName}' was unlinked but cleanup durability could not be established.");
            }

            return true;
        }
        finally
        {
            posix.Close(tempFd);
        }
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
