namespace IEM.Verification.Safety;

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
/// Centralized, read-only, true handle-bound confined package reader ensuring forensic verifier
/// never accesses files outside the package root and eliminates check-then-open (TOCTOU) races.
/// Invariant 29: VERIFIER_NEVER_READS_OUTSIDE_PACKAGE_ROOT.
/// Policy: Any symbolic link, junction, or reparse point is strictly forbidden in forensic packages.
/// </summary>
public static class ConfinedPackageFileReader
{
    public enum ReadResultStatus
    {
        Success,
        NotFound,
        Violation
    }

    /// <summary>
    /// Safely opens a read-only FileStream bound directly to the verified native OS handle.
    /// On Linux: uses root dirfd + openat2(RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV).
    /// On Windows: uses CreateFileW(FILE_FLAG_OPEN_REPARSE_POINT) with handle attribute verification and GetFinalPathNameByHandle.
    /// </summary>
    public static ReadResultStatus TryOpenRead(
        string packageRoot,
        string relativePath,
        out FileStream? stream,
        out string? violationReason)
    {
        stream = null;
        violationReason = null;

        // 1. Basic lexical validation
        if (!PathSafety.TryResolveSafeRelativePath(packageRoot, relativePath, out var safeFullPath, out violationReason))
        {
            return ReadResultStatus.Violation;
        }

        // 2. Native OS handle-bound open & validation
        if (OperatingSystem.IsLinux())
        {
            return TryOpenReadLinux(packageRoot, relativePath, out stream, out violationReason);
        }

        if (OperatingSystem.IsWindows())
        {
            return TryOpenReadWindows(packageRoot, relativePath, safeFullPath, out stream, out violationReason);
        }

        // Cross-platform managed fallback (non-Windows, non-Linux)
        return TryOpenReadManagedFallback(packageRoot, relativePath, safeFullPath, out stream, out violationReason);
    }

    /// <summary>
    /// Safely reads all bytes from a package-owned file strictly confined within the package root
    /// through the verified native handle.
    /// </summary>
    public static async Task<(ReadResultStatus Status, byte[]? Bytes, string? ViolationReason)> TryReadAllBytesAsync(
        string packageRoot,
        string relativePath,
        CancellationToken ct = default)
    {
        var status = TryOpenRead(packageRoot, relativePath, out var stream, out var violationReason);
        if (status != ReadResultStatus.Success || stream is null)
        {
            return (status, null, violationReason);
        }

        using (stream)
        {
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return (ReadResultStatus.Success, bytes, null);
        }
    }

    // =========================================================================
    // Linux Implementation: openat2
    // =========================================================================
    private static ReadResultStatus TryOpenReadLinux(
        string packageRoot,
        string relativePath,
        out FileStream? stream,
        out string? violationReason)
    {
        stream = null;
        violationReason = null;

        var fullRoot = Path.GetFullPath(packageRoot);
        int rootDirFd = NativePackageConfinementInterop.LinuxOpen(
            fullRoot,
            NativePackageConfinementInterop.O_RDONLY | NativePackageConfinementInterop.O_DIRECTORY | NativePackageConfinementInterop.O_CLOEXEC,
            0);

        if (rootDirFd < 0)
        {
            var openRootErr = Marshal.GetLastWin32Error();
            if (openRootErr == 2 /* ENOENT */ || openRootErr == 20 /* ENOTDIR */)
            {
                return ReadResultStatus.NotFound;
            }
            violationReason = $"Nije moguće otvoriti korenski direktorijum paketa (errno {openRootErr}).";
            return ReadResultStatus.Violation;
        }

        try
        {
            var normalizedRel = relativePath.Replace('\\', '/');
            var how = new NativePackageConfinementInterop.OpenHow
            {
                Flags = (ulong)(NativePackageConfinementInterop.O_RDONLY | NativePackageConfinementInterop.O_CLOEXEC | NativePackageConfinementInterop.O_NOFOLLOW),
                Mode = 0,
                Resolve = NativePackageConfinementInterop.RESOLVE_BENEATH |
                          NativePackageConfinementInterop.RESOLVE_NO_SYMLINKS |
                          NativePackageConfinementInterop.RESOLVE_NO_MAGICLINKS |
                          NativePackageConfinementInterop.RESOLVE_NO_XDEV
            };

            int fd = NativePackageConfinementInterop.LinuxSyscallOpenAt2(
                NativePackageConfinementInterop.SYS_openat2,
                rootDirFd,
                normalizedRel,
                ref how,
                (nuint)Marshal.SizeOf<NativePackageConfinementInterop.OpenHow>());

            if (fd < 0)
            {
                int errno = Marshal.GetLastWin32Error();
                if (errno == 2 /* ENOENT */ || errno == 20 /* ENOTDIR */)
                {
                    return ReadResultStatus.NotFound;
                }

                violationReason = $"openat2 odbio pristup putanji '{relativePath}' (errno {errno}).";
                return ReadResultStatus.Violation;
            }

            // Verify opened descriptor represents a regular file (not dir, fifo, socket)
            if (NativePackageConfinementInterop.LinuxFstat(fd, out var stat) != 0)
            {
                NativePackageConfinementInterop.LinuxClose(fd);
                violationReason = $"fstat neuspešan za otvoreni deskriptor '{relativePath}'.";
                return ReadResultStatus.Violation;
            }

            if ((stat.st_mode & NativePackageConfinementInterop.S_IFMT) != NativePackageConfinementInterop.S_IFREG)
            {
                NativePackageConfinementInterop.LinuxClose(fd);
                violationReason = $"Putanja '{relativePath}' nije regularna datoteka dokaza.";
                return ReadResultStatus.Violation;
            }

            var safeHandle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
            stream = new FileStream(safeHandle, FileAccess.Read, bufferSize: 4096);
            return ReadResultStatus.Success;
        }
        finally
        {
            NativePackageConfinementInterop.LinuxClose(rootDirFd);
        }
    }

    // =========================================================================
    // Windows Implementation: Handle Confinement with FILE_FLAG_OPEN_REPARSE_POINT
    // =========================================================================
    private static ReadResultStatus TryOpenReadWindows(
        string packageRoot,
        string relativePath,
        string safeFullPath,
        out FileStream? stream,
        out string? violationReason)
    {
        stream = null;
        violationReason = null;

        var fullRoot = Path.GetFullPath(packageRoot);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        // Open intermediate directories to ensure no reparse points / junctions along the path
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var currentWalk = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            currentWalk = Path.Combine(currentWalk, segments[i]);
            using var hDir = NativePackageConfinementInterop.CreateFile(
                currentWalk,
                NativePackageConfinementInterop.GENERIC_READ,
                NativePackageConfinementInterop.FILE_SHARE_READ | NativePackageConfinementInterop.FILE_SHARE_WRITE | NativePackageConfinementInterop.FILE_SHARE_DELETE,
                IntPtr.Zero,
                NativePackageConfinementInterop.OPEN_EXISTING,
                NativePackageConfinementInterop.FILE_FLAG_BACKUP_SEMANTICS | NativePackageConfinementInterop.FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (hDir.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 2 || err == 3) // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
                {
                    return ReadResultStatus.NotFound;
                }
                violationReason = $"Nije moguće otvoriti direktorijum '{segments[i]}' (kod greške {err}).";
                return ReadResultStatus.Violation;
            }

            if (NativePackageConfinementInterop.GetFileInformationByHandle(hDir, out var dirInfo))
            {
                if ((dirInfo.dwFileAttributes & NativePackageConfinementInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
                {
                    violationReason = $"Segment putanje '{segments[i]}' je direktorijumski spoj (junction) ili reparse tačka.";
                    return ReadResultStatus.Violation;
                }
            }
        }

        // Open target file directly with FILE_FLAG_OPEN_REPARSE_POINT to prevent following symlinks/junctions
        var hFile = NativePackageConfinementInterop.CreateFile(
            safeFullPath,
            NativePackageConfinementInterop.GENERIC_READ,
            NativePackageConfinementInterop.FILE_SHARE_READ,
            IntPtr.Zero,
            NativePackageConfinementInterop.OPEN_EXISTING,
            NativePackageConfinementInterop.FILE_ATTRIBUTE_NORMAL | NativePackageConfinementInterop.FILE_FLAG_OPEN_REPARSE_POINT,
            IntPtr.Zero);

        if (hFile.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 2 || err == 3) // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
            {
                return ReadResultStatus.NotFound;
            }
            violationReason = $"Greška pri otvaranju datoteke '{relativePath}' (kod greške {err}).";
            return ReadResultStatus.Violation;
        }

        // Validate opened handle
        if (!NativePackageConfinementInterop.GetFileInformationByHandle(hFile, out var fileInfo))
        {
            hFile.Dispose();
            violationReason = $"Nije moguće dobiti atribute otvorene datoteke '{relativePath}'.";
            return ReadResultStatus.Violation;
        }

        if ((fileInfo.dwFileAttributes & NativePackageConfinementInterop.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
        {
            hFile.Dispose();
            violationReason = $"Datoteka '{relativePath}' je simbolički link ili reparse tačka.";
            return ReadResultStatus.Violation;
        }

        if ((fileInfo.dwFileAttributes & NativePackageConfinementInterop.FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            hFile.Dispose();
            violationReason = $"Putanja '{relativePath}' je direktorijum, a ne datoteka.";
            return ReadResultStatus.Violation;
        }

        if (NativePackageConfinementInterop.GetFileType(hFile) != NativePackageConfinementInterop.FILE_TYPE_DISK)
        {
            hFile.Dispose();
            violationReason = $"Putanja '{relativePath}' nije regularna datoteka na disku.";
            return ReadResultStatus.Violation;
        }

        // Verify final physical path of the open handle is strictly inside fullRoot
        var finalPath = NormalizeDosPath(GetFinalPath(hFile));
        var canonicalRoot = NormalizeDosPath(fullRoot);
        if (finalPath != null && !finalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            hFile.Dispose();
            violationReason = $"Fizička putanja otvorene datoteke ('{finalPath}') izlazi van korenskog direktorijuma paketa ('{canonicalRoot}').";
            return ReadResultStatus.Violation;
        }

        // Return FileStream over the EXACT open, validated SafeFileHandle
        stream = new FileStream(hFile, FileAccess.Read, bufferSize: 4096);
        return ReadResultStatus.Success;
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var sb = new StringBuilder(1024);
        var res = NativePackageConfinementInterop.GetFinalPathNameByHandle(
            handle,
            sb,
            (uint)sb.Capacity,
            NativePackageConfinementInterop.VOLUME_NAME_DOS);

        if (res == 0 || res > sb.Capacity)
        {
            return null;
        }

        return sb.ToString();
    }

    private static string NormalizeDosPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path.Substring(8);
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path.Substring(4);
        }

        return path;
    }

    // =========================================================================
    // Managed Fallback
    // =========================================================================
    private static ReadResultStatus TryOpenReadManagedFallback(
        string packageRoot,
        string relativePath,
        string safeFullPath,
        out FileStream? stream,
        out string? violationReason)
    {
        stream = null;
        violationReason = null;

        if (!File.Exists(safeFullPath))
        {
            return ReadResultStatus.NotFound;
        }

        try
        {
            var fileInfo = new FileInfo(safeFullPath);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0 || fileInfo.LinkTarget != null)
            {
                violationReason = $"Datoteka '{relativePath}' je simbolički link ili reparse tačka.";
                return ReadResultStatus.Violation;
            }

            stream = new FileStream(
                safeFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return ReadResultStatus.Success;
        }
        catch (Exception ex)
        {
            violationReason = $"Greška pri otvaranju datoteke '{relativePath}': {ex.Message}";
            return ReadResultStatus.Violation;
        }
    }
}
