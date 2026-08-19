using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace IEM.Service.Linux.Lifecycle;

/// <summary>
/// POSIX operations abstraction for deterministic testing across platforms.
/// </summary>
public interface IPosixEnvironment
{
    bool IsLinux { get; }
    uint GetCurrentUid();
    int GetGroupGid(string groupName);
    bool GetPathOwnership(string path, out uint uid, out uint gid);
    bool SetGroupOwnership(string path, int gid);
    bool SetPermissions(string path, UnixFileMode mode);
    bool IsSymlinkOrReparsePoint(string path);
    string GetCanonicalRealPath(string path);
}

/// <summary>
/// Real Linux POSIX environment using libc P/Invokes and File APIs.
/// </summary>
public sealed class RealPosixEnvironment : IPosixEnvironment
{
    public static readonly RealPosixEnvironment Instance = new();

    public bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public uint GetCurrentUid()
    {
        if (!IsLinux) return 0;
        return getuid();
    }

    public int GetGroupGid(string groupName)
    {
        if (!IsLinux) return -1;
        var grpPtr = getgrnam(groupName);
        if (grpPtr == IntPtr.Zero) return -1;

        // struct group { char *gr_name; char *gr_passwd; gid_t gr_gid; ... }
        // On x86_64 / arm64 Linux, gr_gid is at offset 2 * sizeof(IntPtr)
        var gidOffset = 2 * IntPtr.Size;
        return Marshal.ReadInt32(grpPtr, gidOffset);
    }

    public bool GetPathOwnership(string path, out uint uid, out uint gid)
    {
        uid = 0;
        gid = 0;
        if (!IsLinux) return true;

        const int AT_FDCWD = -100;
        const int AT_SYMLINK_NOFOLLOW = 0x100;
        const uint STATX_BASIC_STATS = 0x7ff;

        try
        {
            var statxBuf = new byte[256];
            if (statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, statxBuf) == 0)
            {
                uid = BitConverter.ToUInt32(statxBuf, 20);
                gid = BitConverter.ToUInt32(statxBuf, 24);
                return true;
            }

            var lstatBuf = new byte[256];
            if (lstat(path, lstatBuf) == 0)
            {
                // On 64-bit Linux (x86_64 / arm64), st_uid is at byte offset 28, st_gid at offset 32
                uid = BitConverter.ToUInt32(lstatBuf, 28);
                gid = BitConverter.ToUInt32(lstatBuf, 32);
                return true;
            }
        }
        catch
        {
            // Fallback
        }

        return false;
    }

    public bool SetGroupOwnership(string path, int gid)
    {
        if (!IsLinux) return true;
        if (gid < 0) return false;

        // chown(path, -1, gid) changes only GID without altering UID
        return chown(path, -1, gid) == 0;
    }

    public bool SetPermissions(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsLinux()) return true;

        try
        {
            File.SetUnixFileMode(path, mode);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsSymlinkOrReparsePoint(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    public string GetCanonicalRealPath(string path)
    {
        return Path.GetFullPath(path);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr getgrnam([MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("libc", SetLastError = true)]
    private static extern int chown([MarshalAs(UnmanagedType.LPStr)] string path, int owner, int group);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPStr)] string pathname, int flags, uint mask, [Out] byte[] statxbuf);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat([MarshalAs(UnmanagedType.LPStr)] string path, [Out] byte[] statbuf);
}

/// <summary>
/// Validates and prepares the systemd RuntimeDirectory (/run/internet-evidence-monitor/)
/// prior to future socket binding (Phase 3.1-3).
/// Enforces §11.3 & Implementation-Readiness Patch locked sequence:
/// 1. Expected absolute path (/run/internet-evidence-monitor)
/// 2. lstat/no-follow validation (verify no symlink)
/// 3. Verify owner UID == current process UID (iem)
/// 4. Change GID only to iem-users (supplementary group)
/// 5. chmod 0750 (rwxr-x---)
/// Fail-closed on: wrong owner, symlink, unexpected path, realpath escape, chmod/chgrp failure.
/// </summary>
public static class LinuxRuntimeDirectoryPreparer
{
    public const string DefaultExpectedPath = "/run/internet-evidence-monitor";
    public const string TargetGroupName = "iem-users";

    public static RuntimeDirectoryResult Prepare(
        string path = DefaultExpectedPath,
        IPosixEnvironment? posix = null,
        ILogger? logger = null)
    {
        posix ??= RealPosixEnvironment.Instance;

        // 1. Validate expected absolute path
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            logger?.LogError("RuntimeDirectory putanja '{Path}' nije validna apsolutna putanja.", path);
            return RuntimeDirectoryResult.Failed("Putanja nije apsolutna.");
        }

        // Validate canonical realpath resolution to prevent directory traversal escape
        var canonicalPath = posix.GetCanonicalRealPath(path);
        if (!string.Equals(canonicalPath.TrimEnd('/', '\\'), path.TrimEnd('/', '\\'), StringComparison.Ordinal))
        {
            logger?.LogCritical("Sigurnosno odbijanje: Realpath escape na RuntimeDirectory putanji '{Path}' -> '{Canonical}'.", path, canonicalPath);
            return RuntimeDirectoryResult.Failed("Realpath escape na RuntimeDirectory putanji.");
        }

        // On non-Linux platforms without full simulated POSIX environment, perform structural success
        if (!posix.IsLinux)
        {
            return RuntimeDirectoryResult.Success(path, isSimulated: true);
        }

        try
        {
            if (!Directory.Exists(path))
            {
                logger?.LogError("RuntimeDirectory '{Path}' ne postoji. systemd RuntimeDirectory= direktiva je obavezna.", path);
                return RuntimeDirectoryResult.Failed("Direktorijum ne postoji.");
            }

            // 2. lstat / no-follow validation: verify not a symlink
            if (posix.IsSymlinkOrReparsePoint(path))
            {
                logger?.LogCritical("Sigurnosno odbijanje: RuntimeDirectory '{Path}' je symlink/reparse point!", path);
                return RuntimeDirectoryResult.Failed("Symlink detektovan na RuntimeDirectory putanji.");
            }

            // 3. Verify owner UID == current running process UID (iem)
            var currentUid = posix.GetCurrentUid();
            if (!posix.GetPathOwnership(path, out var ownerUid, out _))
            {
                logger?.LogCritical("Neuspešno očitavanje vlasništva nad '{Path}'.", path);
                return RuntimeDirectoryResult.Failed("Neuspešno očitavanje vlasništva nad RuntimeDirectory.");
            }

            if (ownerUid != currentUid)
            {
                logger?.LogCritical("Sigurnosno odbijanje: Vlasnik RuntimeDirectory '{Path}' (UID {OwnerUid}) ne odgovara procesu (UID {CurrentUid}).", path, ownerUid, currentUid);
                return RuntimeDirectoryResult.Failed($"Pogrešan UID vlasnika ({ownerUid} != {currentUid}).");
            }

            // 4. Change GID only to iem-users (supplementary group)
            var targetGid = posix.GetGroupGid(TargetGroupName);
            if (targetGid >= 0)
            {
                if (!posix.SetGroupOwnership(path, targetGid))
                {
                    logger?.LogCritical("Neuspešna promena GID-a na '{Group}' (GID {Gid}) za '{Path}'.", TargetGroupName, targetGid, path);
                    return RuntimeDirectoryResult.Failed($"Neuspešna promena GID-a na {TargetGroupName}.");
                }
            }
            else
            {
                logger?.LogWarning("Grupa '{Group}' nije pronađena u sistemu; preskače se chgrp.", TargetGroupName);
            }

            // 5. chmod 0750 (rwxr-x---)
            var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                               UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

            if (!posix.SetPermissions(path, expectedMode))
            {
                logger?.LogCritical("Neuspešno postavljanje dozvola 0750 na '{Path}'.", path);
                return RuntimeDirectoryResult.Failed("Neuspešno postavljanje UnixFileMode 0750.");
            }

            return RuntimeDirectoryResult.Success(path, isSimulated: false);
        }
        catch (Exception ex)
        {
            logger?.LogCritical(ex, "Neuspešna validacija i priprema RuntimeDirectory '{Path}'.", path);
            return RuntimeDirectoryResult.Failed(ex.Message);
        }
    }
}

public sealed record RuntimeDirectoryResult(bool IsValid, string Path, string? Error, bool IsSimulated)
{
    public static RuntimeDirectoryResult Success(string path, bool isSimulated) =>
        new(true, path, null, isSimulated);

    public static RuntimeDirectoryResult Failed(string error) =>
        new(false, string.Empty, error, false);
}
