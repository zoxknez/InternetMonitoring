using System.Runtime.InteropServices;

namespace IEM.Linux.Storage;

public static class LinuxPosixStorageConstants
{
    public const int O_RDONLY = 0x0000;
    public const int O_WRONLY = 0x0001;
    public const int O_RDWR = 0x0002;
    public const int O_CREAT = 0x0040;
    public const int O_EXCL = 0x0080;
    public const int O_DIRECTORY = 0x00010000;
    public const int O_NOFOLLOW = 0x00020000;
    public const int O_CLOEXEC = 0x00080000;
    public const int O_PATH = 0x00200000;

    public const int AT_FDCWD = -100;
    public const int AT_SYMLINK_NOFOLLOW = 0x0100;

    public const ulong RESOLVE_NO_XDEV = 0x01;
    public const ulong RESOLVE_NO_MAGICLINKS = 0x02;
    public const ulong RESOLVE_NO_SYMLINKS = 0x04;
    public const ulong RESOLVE_BENEATH = 0x08;

    public const uint S_IFMT = 0xF000;
    public const uint S_IFDIR = 0x4000;
    public const uint S_IFREG = 0x8000;
    public const uint S_IFLNK = 0xA000;

    public const int Mode0700 = 0x1C0; // rwx------ (448 decimal, 0700 octal)
    public const int Mode0600 = 0x180; // rw------- (384 decimal, 0600 octal)
}

/// <summary>
/// Managed layout matching Linux glibc x86_64 struct stat (exactly 144 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PosixStat
{
    public ulong Dev;
    public ulong Ino;
    public ulong Nlink;
    public uint Mode;
    public uint Uid;
    public uint Gid;
    public uint Pad0;
    public ulong Rdev;
    public long Size;
    public long Blksize;
    public long Blocks;
    public long AtimSec;
    public long AtimNsec;
    public long MtimSec;
    public long MtimNsec;
    public long CtimSec;
    public long CtimNsec;
    public long Reserved0;
    public long Reserved1;
    public long Reserved2;

    public bool IsDirectory => (Mode & LinuxPosixStorageConstants.S_IFMT) == LinuxPosixStorageConstants.S_IFDIR;
    public bool IsRegularFile => (Mode & LinuxPosixStorageConstants.S_IFMT) == LinuxPosixStorageConstants.S_IFREG;
    public bool IsSymlink => (Mode & LinuxPosixStorageConstants.S_IFMT) == LinuxPosixStorageConstants.S_IFLNK;
    public int PermissionBits => (int)(Mode & 0x1FF); // 0777 octal
}

[StructLayout(LayoutKind.Sequential)]
public struct OpenHow
{
    public ulong Flags;
    public ulong Mode;
    public ulong Resolve;
}

public interface ILinuxPosixStorageApi
{
    int Open(string path, int flags, int mode);
    int OpenAt(int dirfd, string pathname, int flags, int mode);
    int OpenAt2(int dirfd, string pathname, ref OpenHow how);
    int FstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags);
    int Fstat(int fd, out PosixStat statbuf);
    int MkdirAt(int dirfd, string pathname, int mode);
    int Fchmod(int fd, int mode);
    int Fchown(int fd, uint uid, uint gid);
    int Write(int fd, ReadOnlySpan<byte> buffer);
    int Read(int fd, Span<byte> buffer);
    int Fsync(int fd);
    int Close(int fd);
    uint GetEuid();
    uint GetEgid();
}

public sealed class LinuxNativePosixStorageApi : ILinuxPosixStorageApi
{
    private const string LibC = "libc";
    private const long SYS_openat2 = 437; // Linux x86_64 / aarch64 openat2 syscall

    [DllImport(LibC, EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen(string path, int flags, int mode);

    [DllImport(LibC, EntryPoint = "openat", SetLastError = true)]
    private static extern int NativeOpenAt(int dirfd, string pathname, int flags, int mode);

    [DllImport(LibC, EntryPoint = "fstatat", SetLastError = true)]
    private static extern int NativeFstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags);

    [DllImport(LibC, EntryPoint = "fstat", SetLastError = true)]
    private static extern int NativeFstat(int fd, out PosixStat statbuf);

    [DllImport(LibC, EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int NativeMkdirAt(int dirfd, string pathname, int mode);

    [DllImport(LibC, EntryPoint = "fchmod", SetLastError = true)]
    private static extern int NativeFchmod(int fd, int mode);

    [DllImport(LibC, EntryPoint = "fchown", SetLastError = true)]
    private static extern int NativeFchown(int fd, uint uid, uint gid);

    [DllImport(LibC, EntryPoint = "write", SetLastError = true)]
    private static extern unsafe nint NativeWrite(int fd, byte* buf, nuint count);

    [DllImport(LibC, EntryPoint = "read", SetLastError = true)]
    private static extern unsafe nint NativeRead(int fd, byte* buf, nuint count);

    [DllImport(LibC, EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(int fd);

    [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
    private static extern int NativeClose(int fd);

    [DllImport(LibC, EntryPoint = "geteuid")]
    private static extern uint NativeGetEuid();

    [DllImport(LibC, EntryPoint = "getegid")]
    private static extern uint NativeGetEgid();

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static extern int NativeSyscallOpenAt2(long number, int dirfd, string pathname, ref OpenHow how, nuint size);

    public int Open(string path, int flags, int mode) =>
        OperatingSystem.IsLinux() ? NativeOpen(path, flags, mode) : -1;

    public int OpenAt(int dirfd, string pathname, int flags, int mode) =>
        OperatingSystem.IsLinux() ? NativeOpenAt(dirfd, pathname, flags, mode) : -1;

    public int OpenAt2(int dirfd, string pathname, ref OpenHow how) =>
        OperatingSystem.IsLinux() ? NativeSyscallOpenAt2(SYS_openat2, dirfd, pathname, ref how, (nuint)Marshal.SizeOf<OpenHow>()) : -1;

    public int FstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags)
    {
        if (OperatingSystem.IsLinux())
        {
            return NativeFstatAt(dirfd, pathname, out statbuf, flags);
        }
        statbuf = default;
        return -1;
    }

    public int Fstat(int fd, out PosixStat statbuf)
    {
        if (OperatingSystem.IsLinux())
        {
            return NativeFstat(fd, out statbuf);
        }
        statbuf = default;
        return -1;
    }

    public int MkdirAt(int dirfd, string pathname, int mode) =>
        OperatingSystem.IsLinux() ? NativeMkdirAt(dirfd, pathname, mode) : -1;

    public int Fchmod(int fd, int mode) =>
        OperatingSystem.IsLinux() ? NativeFchmod(fd, mode) : -1;

    public int Fchown(int fd, uint uid, uint gid) =>
        OperatingSystem.IsLinux() ? NativeFchown(fd, uid, gid) : -1;

    public unsafe int Write(int fd, ReadOnlySpan<byte> buffer)
    {
        if (!OperatingSystem.IsLinux() || buffer.IsEmpty) return 0;
        fixed (byte* ptr = buffer)
        {
            return (int)NativeWrite(fd, ptr, (nuint)buffer.Length);
        }
    }

    public unsafe int Read(int fd, Span<byte> buffer)
    {
        if (!OperatingSystem.IsLinux() || buffer.IsEmpty) return 0;
        fixed (byte* ptr = buffer)
        {
            return (int)NativeRead(fd, ptr, (nuint)buffer.Length);
        }
    }

    public int Fsync(int fd) =>
        OperatingSystem.IsLinux() ? NativeFsync(fd) : -1;

    public int Close(int fd) =>
        OperatingSystem.IsLinux() ? NativeClose(fd) : -1;

    public uint GetEuid() =>
        OperatingSystem.IsLinux() ? NativeGetEuid() : 0;

    public uint GetEgid() =>
        OperatingSystem.IsLinux() ? NativeGetEgid() : 0;
}
