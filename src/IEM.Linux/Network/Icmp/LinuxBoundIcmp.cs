using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using IEM.Core.Probes;

namespace IEM.Linux.Network.Icmp;

/// <summary>
/// Production Linux ICMP echo sender using unprivileged datagram sockets (AF_INET/AF_INET6, SOCK_DGRAM, IPPROTO_ICMP/IPPROTO_ICMPV6).
/// Invariants 271-275:
/// 1. Binds the chosen source address before transmitting.
/// 2. Strict distinction: Local capability failures return Succeeded=false, TimedOut=false (ProbeOutcome.Skipped).
/// 3. Network timeouts occur ONLY after successful transmission to wire.
/// 4. Correlates echo replies via monotonic sequence, 64-bit nonce, and destination peer IP.
/// </summary>
public sealed class LinuxBoundIcmp : IBoundIcmp
{
    public static LinuxBoundIcmp Instance { get; } = new();

    private static int _sequenceCounter;

    public async Task<IcmpEcho?> SendAsync(
        IPAddress destination,
        IPAddress source,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Non-Linux platform fallback
            return new IcmpEcho(
                Succeeded: false,
                TimedOut: false,
                RoundTrip: TimeSpan.Zero,
                Status: LinuxIcmpStatus.UnspecifiedLocalError);
        }

        var isV6 = destination.AddressFamily == AddressFamily.InterNetworkV6;
        if (!isV6 && destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return new IcmpEcho(
                Succeeded: false,
                TimedOut: false,
                RoundTrip: TimeSpan.Zero,
                Status: LinuxIcmpStatus.UnspecifiedLocalError);
        }

        var sequence = (ushort)(Interlocked.Increment(ref _sequenceCounter) & 0xFFFF);
        var nonce = (ulong)Random.Shared.NextInt64();
        var startTimestamp = Stopwatch.GetTimestamp();
        var timeoutMs = (int)Math.Max(1, timeout.TotalMilliseconds);

        Socket? socket = null;

        try
        {
            // 1. Unprivileged datagram ICMP socket creation
            var family = isV6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
            var protocol = isV6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp;

            try
            {
                socket = new Socket(family, SocketType.Dgram, protocol);
            }
            catch (SocketException ex)
            {
                var status = ex.SocketErrorCode switch
                {
                    SocketError.AccessDenied => LinuxIcmpStatus.LocalCapabilityDenied, // EPERM / EACCES
                    _ => LinuxIcmpStatus.SocketCreateFailed
                };

                return new IcmpEcho(
                    Succeeded: false,
                    TimedOut: false,
                    RoundTrip: TimeSpan.Zero,
                    Status: status);
            }

            // 2. Source-Address Binding
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
            }
            catch (SocketException ex)
            {
                var status = ex.SocketErrorCode switch
                {
                    SocketError.AddressNotAvailable => LinuxIcmpStatus.AddressNotAvailable,
                    SocketError.AccessDenied => LinuxIcmpStatus.LocalCapabilityDenied,
                    _ => LinuxIcmpStatus.BindFailed
                };

                return new IcmpEcho(
                    Succeeded: false,
                    TimedOut: false,
                    RoundTrip: TimeSpan.Zero,
                    Status: status);
            }

            // 3. Build and Transmit Echo Request
            var packet = LinuxIcmpPacket.BuildEchoRequest(isV6, sequence, nonce, DateTime.UtcNow.Ticks);
            var targetEndPoint = new IPEndPoint(destination, 0);

            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, targetEndPoint).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                var status = ex.SocketErrorCode switch
                {
                    SocketError.AccessDenied => LinuxIcmpStatus.LocalCapabilityDenied,
                    SocketError.AddressNotAvailable => LinuxIcmpStatus.AddressNotAvailable,
                    _ => LinuxIcmpStatus.SendFailed
                };

                return new IcmpEcho(
                    Succeeded: false,
                    TimedOut: false,
                    RoundTrip: TimeSpan.Zero,
                    Status: status);
            }

            // 4. Bounded Receive Loop with Single Absolute Deadline
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var receiveBuffer = new byte[LinuxIcmpConstants.ReceiveBufferSize];
            EndPoint remoteSender = new IPEndPoint(isV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(
                        receiveBuffer,
                        SocketFlags.None,
                        remoteSender,
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                // 5. Correlate Echo Reply: Peer IP, Length, Type, Code, Sequence, and Nonce
                if (result.ReceivedBytes >= LinuxIcmpConstants.TotalEchoPacketSize &&
                    result.RemoteEndPoint is IPEndPoint remoteIp &&
                    remoteIp.Address.Equals(destination) &&
                    LinuxIcmpPacket.TryValidateEchoReply(receiveBuffer.AsSpan(0, result.ReceivedBytes), isV6, sequence, nonce))
                {
                    var rtt = Stopwatch.GetElapsedTime(startTimestamp);
                    return new IcmpEcho(
                        Succeeded: true,
                        TimedOut: false,
                        RoundTrip: rtt,
                        Status: LinuxIcmpStatus.Success);
                }

                // Irrelevant packet from unrelated sender or sequence mismatch: continue loop until deadline
            }

            // 6. Network Timeout after actual transmission
            return new IcmpEcho(
                Succeeded: false,
                TimedOut: true,
                RoundTrip: timeout,
                Status: LinuxIcmpStatus.TimedOut);
        }
        catch (Exception)
        {
            return new IcmpEcho(
                Succeeded: false,
                TimedOut: false,
                RoundTrip: TimeSpan.Zero,
                Status: LinuxIcmpStatus.UnspecifiedLocalError);
        }
        finally
        {
            socket?.Dispose();
        }
    }
}
