namespace IEM.Verification.Safety;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class NativePackageConfinementInterop
{
    // =========================================================================
    // Linux POSIX / openat2
    // =========================================================================
    private const string LibC = "libc";
    public const long SYS_openat2 = 437; // Linux x86_64 / aarch64 openat2 syscall

    public const int O_RDONLY = 0x0000;
    public const int O_DIRECTORY = 0x00010000;
    public const int O_CLOEXEC = 0x00080000;
    public const int O_NOFOLLOW = 0x00020000;

    public const ulong RESOLVE_NO_XDEV = 0x01;
    public const ulong RESOLVE_NO_MAGICLINKS = 0x02;
    public const ulong RESOLVE_NO_SYMLINKS = 0x04;
    public const ulong RESOLVE_BENEATH = 0x08;

    public const uint S_IFMT = 0xF000;
    public const uint S_IFREG = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PosixStat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public uint st_uid;
        public uint st_gid;
        public uint __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime;
        public ulong st_atime_nsec;
        public long st_mtime;
        public ulong st_mtime_nsec;
        public long st_ctime;
        public ulong st_ctime_nsec;
        public long __unused4;
        public long __unused5;
        public long __unused6;
    }

    [DllImport(LibC, EntryPoint = "open", SetLastError = true)]
    public static extern int LinuxOpen(string path, int flags, int mode);

    [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
    public static extern int LinuxClose(int fd);

    [DllImport(LibC, EntryPoint = "fstat", SetLastError = true)]
    public static extern int LinuxFstat(int fd, out PosixStat statbuf);

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    public static extern int LinuxSyscallOpenAt2(long number, int dirfd, string pathname, ref OpenHow how, nuint size);

    // =========================================================================
    // Windows Win32 / NT Handle Confinement
    // =========================================================================
    private const string Kernel32 = "kernel32.dll";

    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;
    public const uint OPEN_EXISTING = 3;

    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    public const uint FILE_TYPE_DISK = 0x0001;
    public const uint VOLUME_NAME_DOS = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [DllImport(Kernel32, EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern uint GetFileType(SafeFileHandle hFile);

    [DllImport(Kernel32, EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] System.Text.StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
