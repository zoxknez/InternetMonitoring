using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IEM.Core.Ipc;
using IEM.Linux.Installation;
using IEM.Service.Linux.Ipc;
using IEM.Storage;

namespace IEM.Service.Linux.Installation;

/// <summary>
/// Authoritative reachability source for Linux AF_UNIX control socket.
/// Invariants 8E-J, 8E-R1-B:
/// - Determines IPC reachability through an explicit, bounded IEM protocol handshake.
/// - Socket connection alone is insufficient: verifies protocol version, request ID, and service instance ID.
/// - Incompatible protocol versions or malformed envelopes fail closed to Unreachable.
/// </summary>
public sealed class LinuxControlSocketReachabilitySource : ILinuxServiceReachabilitySource
{
    private readonly string _socketPath;
    private readonly TimeSpan _probeTimeout;
    private readonly Func<CancellationToken, Task<Stream>>? _streamFactory;

    public LinuxControlSocketReachabilitySource(
        string socketPath = LinuxUnixDomainSocketTransport.DefaultSocketPath,
        TimeSpan? probeTimeout = null,
        Func<CancellationToken, Task<Stream>>? streamFactory = null)
    {
        _socketPath = socketPath;
        _probeTimeout = probeTimeout ?? TimeSpan.FromMilliseconds(500);
        _streamFactory = streamFactory;
    }

    public static readonly LinuxControlSocketReachabilitySource Default = new();

    public async Task<ServiceReachability> ProbeReachabilityAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_probeTimeout);

        try
        {
            Stream stream;
            Socket? socket = null;

            if (_streamFactory != null)
            {
                stream = await _streamFactory(timeoutCts.Token).ConfigureAwait(false);
            }
            else
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return ServiceReachability.Unreachable;
                }

                if (!File.Exists(_socketPath))
                {
                    return ServiceReachability.Unreachable;
                }

                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(_socketPath);

                await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
                stream = new NetworkStream(socket, ownsSocket: true);
            }

            await using (stream.ConfigureAwait(false))
            {
                var requestId = Guid.NewGuid().ToString("N");
                var request = new IpcRequestEnvelope
                {
                    ProtocolVersion = IpcRequestEnvelope.CurrentProtocolVersion,
                    RequestId = requestId,
                    CommandName = "GetServiceStatus",
                    SentAtUtc = DateTimeOffset.UtcNow,
                    ClientInstanceId = "probe-" + requestId[..8]
                };

                var requestJson = JsonSerializer.Serialize(request);
                var requestBytes = Encoding.UTF8.GetBytes(requestJson);

                await IpcMessageFraming.WriteFrameAsync(stream, requestBytes, timeoutCts.Token).ConfigureAwait(false);

                var responseBytes = await IpcMessageFraming.ReadFrameAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                if (responseBytes == null || responseBytes.Length == 0)
                {
                    return ServiceReachability.Unreachable;
                }

                var responseJson = Encoding.UTF8.GetString(responseBytes);
                var response = JsonSerializer.Deserialize<IpcResponseEnvelope>(responseJson);

                if (response == null)
                {
                    return ServiceReachability.Unreachable;
                }

                if (response.ProtocolVersion != IpcResponseEnvelope.CurrentProtocolVersion)
                {
                    return ServiceReachability.Unreachable;
                }

                if (response.RequestId != requestId)
                {
                    return ServiceReachability.Unreachable;
                }

                if (string.IsNullOrWhiteSpace(response.ServiceInstanceId))
                {
                    return ServiceReachability.Unreachable;
                }

                if (response.Status == IpcResponseStatus.UnsupportedProtocol)
                {
                    return ServiceReachability.Unreachable;
                }

                return ServiceReachability.Reachable;
            }
        }
        catch
        {
            return ServiceReachability.Unreachable;
        }
    }

    public ServiceReachability ProbeReachability()
    {
        try
        {
            return ProbeReachabilityAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return ServiceReachability.Unreachable;
        }
    }
}
