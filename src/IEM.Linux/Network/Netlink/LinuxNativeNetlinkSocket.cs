using System;
using System.IO;
using System.Runtime.InteropServices;

namespace IEM.Linux.Network.Netlink;

/// <summary>
/// Native sockaddr_nl structure for Linux AF_NETLINK sockets (12 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SockAddrNl
{
    public ushort nl_family;   // AF_NETLINK = 16
    public ushort nl_pad;      // 0
    public uint nl_pid;        // Port ID (0 = kernel / bind auto)
    public uint nl_groups;     // Multicast groups mask

    public static SockAddrNl Create(uint pid = 0, uint groups = 0) => new()
    {
        nl_family = 16, // AF_NETLINK
        nl_pad = 0,
        nl_pid = pid,
        nl_groups = groups
    };
}

/// <summary>
/// Native pollfd structure for bounded timeout operations on Linux file descriptors.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PollFd
{
    public int fd;
    public short events;   // POLLIN = 0x0001
    public short revents;

    public const short POLLIN = 0x0001;
    public const short POLLERR = 0x0008;
    public const short POLLHUP = 0x0010;
    public const short POLLNVAL = 0x0020;
}

/// <summary>
/// Production native AF_NETLINK socket client using direct libc P/Invoke.
/// Invariants 249-254, 271-275.
/// </summary>
public sealed partial class LinuxNativeNetlinkSocket : IDisposable
{
    private const int AF_NETLINK = 16;
    private const int SOCK_RAW = 3;
    private const int SOCK_CLOEXEC = 0x80000;

    private int _fd = -1;
    private bool _disposed;
    private readonly int _protocol;
    private readonly object _lock = new();

    private LinuxNativeNetlinkSocket(int protocol)
    {
        _protocol = protocol;
    }

    public static LinuxNativeNetlinkSocket Open(int protocol)
    {
        var sock = new LinuxNativeNetlinkSocket(protocol);
        sock.EnsureOpen();
        return sock;
    }

    public bool IsOpen => _fd >= 0 && !_disposed;

    private void EnsureOpen()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LinuxNativeNetlinkSocket));
        if (_fd >= 0) return;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("AF_NETLINK is only supported on Linux.");
        }

        int fd = NativeMethods.socket(AF_NETLINK, SOCK_RAW | SOCK_CLOEXEC, _protocol);
        if (fd < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new IOException($"Failed to open AF_NETLINK socket (protocol={_protocol}): errno {err}");
        }

        var localAddr = SockAddrNl.Create(pid: 0, groups: 0);
        int bindRes = NativeMethods.bind(fd, ref localAddr, (uint)Marshal.SizeOf<SockAddrNl>());
        if (bindRes < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            NativeMethods.close(fd);
            throw new IOException($"Failed to bind AF_NETLINK socket: errno {err}");
        }

        _fd = fd;
    }

    public int Send(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            EnsureOpen();
            var kernelAddr = SockAddrNl.Create(pid: 0, groups: 0);

            unsafe
            {
                fixed (byte* pData = data)
                {
                    nint sent = NativeMethods.sendto(
                        _fd,
                        pData,
                        (nuint)data.Length,
                        0,
                        ref kernelAddr,
                        (uint)Marshal.SizeOf<SockAddrNl>());

                    if (sent < 0)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        throw new IOException($"AF_NETLINK sendto failed: errno {err}");
                    }

                    return (int)sent;
                }
            }
        }
    }

    public int Receive(Span<byte> buffer, int timeoutMs = 3000)
    {
        lock (_lock)
        {
            EnsureOpen();

            if (timeoutMs > 0)
            {
                var pfd = new PollFd
                {
                    fd = _fd,
                    events = PollFd.POLLIN,
                    revents = 0
                };

                int pollRes = NativeMethods.poll(ref pfd, 1, timeoutMs);
                if (pollRes < 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new IOException($"AF_NETLINK poll failed: errno {err}");
                }
                if (pollRes == 0)
                {
                    throw new TimeoutException($"AF_NETLINK receive timed out after {timeoutMs}ms");
                }
            }

            unsafe
            {
                fixed (byte* pBuf = buffer)
                {
                    nint read = NativeMethods.recv(_fd, pBuf, (nuint)buffer.Length, 0);
                    if (read < 0)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        throw new IOException($"AF_NETLINK recv failed: errno {err}");
                    }

                    return (int)read;
                }
            }
        }
    }

    public const int SOL_NETLINK = 270;
    public const int NETLINK_ADD_MEMBERSHIP = 1;
    public const int NETLINK_DROP_MEMBERSHIP = 2;

    public void JoinMulticastGroup(uint groupId)
    {
        lock (_lock)
        {
            EnsureOpen();
            unsafe
            {
                int group = (int)groupId;
                int res = NativeMethods.setsockopt(_fd, SOL_NETLINK, NETLINK_ADD_MEMBERSHIP, &group, sizeof(int));
                if (res < 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new IOException($"Failed to join Netlink multicast group {groupId}: errno {err}");
                }
            }
        }
    }

    public void LeaveMulticastGroup(uint groupId)
    {
        lock (_lock)
        {
            EnsureOpen();
            unsafe
            {
                int group = (int)groupId;
                int res = NativeMethods.setsockopt(_fd, SOL_NETLINK, NETLINK_DROP_MEMBERSHIP, &group, sizeof(int));
                if (res < 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    throw new IOException($"Failed to leave Netlink multicast group {groupId}: errno {err}");
                }
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_fd >= 0)
            {
                NativeMethods.close(_fd);
                _fd = -1;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    internal static partial class NativeMethods
    {
        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial int socket(int domain, int type, int protocol);

        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial int bind(int sockfd, ref SockAddrNl addr, uint addrlen);

        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial nint sendto(int sockfd, byte* buf, nuint len, int flags, ref SockAddrNl dest_addr, uint addrlen);

        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial nint recv(int sockfd, byte* buf, nuint len, int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial int close(int fd);

        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial int poll(ref PollFd fds, uint nfds, int timeout);

        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial int setsockopt(int sockfd, int level, int optname, void* optval, uint optlen);

        [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        public static unsafe partial int open(string pathname, int flags);

        [LibraryImport("libc", SetLastError = true)]
        public static unsafe partial nint read(int fd, byte* buf, nuint count);
    }
}
