using System.Net.Sockets;
using System.Runtime.InteropServices;
using IEM.Linux.Installation;
using IEM.Service.Linux.Ipc;
using IEM.Storage;

namespace IEM.Service.Linux.Installation;

/// <summary>
/// Authoritative reachability source for Linux AF_UNIX control socket.
/// Invariant 8E-J: Determines IPC reachability independently from service installation presence.
/// </summary>
public sealed class LinuxControlSocketReachabilitySource : ILinuxServiceReachabilitySource
{
    private readonly string _socketPath;
    private readonly TimeSpan _probeTimeout;

    public LinuxControlSocketReachabilitySource(
        string socketPath = LinuxUnixDomainSocketTransport.DefaultSocketPath,
        TimeSpan? probeTimeout = null)
    {
        _socketPath = socketPath;
        _probeTimeout = probeTimeout ?? TimeSpan.FromMilliseconds(500);
    }

    public static readonly LinuxControlSocketReachabilitySource Default = new();

    public ServiceReachability ProbeReachability()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return ServiceReachability.Unreachable;
        }

        try
        {
            if (!File.Exists(_socketPath))
            {
                return ServiceReachability.Unreachable;
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(_socketPath);

            var connectTask = socket.ConnectAsync(endpoint);
            if (!connectTask.Wait(_probeTimeout))
            {
                return ServiceReachability.Unreachable;
            }

            return ServiceReachability.Reachable;
        }
        catch
        {
            return ServiceReachability.Unreachable;
        }
    }

    public async Task<ServiceReachability> ProbeReachabilityAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return ServiceReachability.Unreachable;
        }

        try
        {
            if (!File.Exists(_socketPath))
            {
                return ServiceReachability.Unreachable;
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(_socketPath);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_probeTimeout);

            await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
            return ServiceReachability.Reachable;
        }
        catch
        {
            return ServiceReachability.Unreachable;
        }
    }
}
