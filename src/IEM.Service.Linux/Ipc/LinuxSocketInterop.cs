using System.Runtime.InteropServices;

namespace IEM.Service.Linux.Ipc;

/// <summary>
/// Native Linux socket & POSIX constants and P/Invoke interop.
/// </summary>
public static class LinuxSocketInterop
{
    public const int SOL_SOCKET = 1;
    public const int SO_PEERCRED = 17;
    public const int SO_PEERGROUPS = 59;

    public const int S_IFMT = 0xF000;
    public const int S_IFSOCK = 0xC000;
    public const int S_IFLNK = 0xA000;

    [StructLayout(LayoutKind.Sequential)]
    public struct UCred
    {
        public int pid;
        public uint uid;
        public uint gid;
    }

    [DllImport("libc", SetLastError = true)]
    public static extern int getsockopt(
        IntPtr sockfd,
        int level,
        int optname,
        [Out] byte[] optval,
        ref int optlen);

    [DllImport("libc", SetLastError = true)]
    public static extern int getsockopt(
        IntPtr sockfd,
        int level,
        int optname,
        out UCred optval,
        ref int optlen);

    [DllImport("libc", SetLastError = true)]
    public static extern int unlink([MarshalAs(UnmanagedType.LPStr)] string pathname);

    [DllImport("libc", SetLastError = true)]
    public static extern int chmod([MarshalAs(UnmanagedType.LPStr)] string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    public static extern int chown([MarshalAs(UnmanagedType.LPStr)] string path, int owner, int group);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    public static extern int lstat([MarshalAs(UnmanagedType.LPStr)] string path, [Out] byte[] statbuf);

    [DllImport("libc", SetLastError = true)]
    public static extern IntPtr getgrnam([MarshalAs(UnmanagedType.LPStr)] string name);
}
