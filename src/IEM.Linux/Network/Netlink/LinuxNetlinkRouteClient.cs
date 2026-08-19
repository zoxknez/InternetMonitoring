using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IEM.Linux.Network.Netlink;

/// <summary>
/// Client that performs FIB route lookups via Linux AF_NETLINK / NETLINK_ROUTE socket.
/// Queries RTM_GETROUTE directly from the kernel routing table without invoking any shell or CLI tools.
/// Invariants 271-275.
/// </summary>
public sealed class LinuxNetlinkRouteClient : IDisposable
{
    private static int _globalSequence;
    private Socket? _socket;
    private readonly object _syncLock = new();

    public static LinuxNetlinkRouteClient Instance { get; } = new();

    public NetlinkRouteResponse QueryRoute(IPAddress destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                "Netlink route lookup is only available on Linux.");
        }

        var isV4 = destination.AddressFamily == AddressFamily.InterNetwork;
        var isV6 = destination.AddressFamily == AddressFamily.InterNetworkV6;

        if (!isV4 && !isV6)
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Unsupported address family {destination.AddressFamily}.");
        }

        var addrBytes = destination.GetAddressBytes();
        var seq = (uint)Interlocked.Increment(ref _globalSequence);

        var rtaLen = (ushort)(NetlinkConstants.RtattrHeaderSize + addrBytes.Length);
        var totalLen = NetlinkConstants.NlmsgHeaderSize + NetlinkConstants.RtmsgHeaderSize + rtaLen;

        var requestBuffer = new byte[totalLen];

        // 1. nlmsghdr (16 bytes)
        BinaryPrimitives.WriteInt32LittleEndian(requestBuffer.AsSpan(0, 4), totalLen);
        BinaryPrimitives.WriteUInt16LittleEndian(requestBuffer.AsSpan(4, 2), NetlinkConstants.RTM_GETROUTE);
        BinaryPrimitives.WriteUInt16LittleEndian(requestBuffer.AsSpan(6, 2), NetlinkConstants.NLM_F_REQUEST);
        BinaryPrimitives.WriteUInt32LittleEndian(requestBuffer.AsSpan(8, 4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(requestBuffer.AsSpan(12, 4), 0); // PID (kernel fills)

        // 2. rtmsg (12 bytes)
        var rtmsgSpan = requestBuffer.AsSpan(NetlinkConstants.NlmsgHeaderSize, NetlinkConstants.RtmsgHeaderSize);
        rtmsgSpan[0] = isV4 ? (byte)2 : (byte)10; // AF_INET = 2, AF_INET6 = 10
        rtmsgSpan[1] = isV4 ? (byte)32 : (byte)128; // rtm_dst_len (/32 or /128)
        rtmsgSpan[2] = 0; // rtm_src_len
        rtmsgSpan[3] = 0; // rtm_tos
        rtmsgSpan[4] = NetlinkConstants.RT_TABLE_MAIN; // rtm_table
        rtmsgSpan[5] = 0; // rtm_protocol (RTPROT_UNSPEC)
        rtmsgSpan[6] = 0; // rtm_scope (RT_SCOPE_UNIVERSE)
        rtmsgSpan[7] = 0; // rtm_type (RTN_UNSPEC)
        BinaryPrimitives.WriteUInt32LittleEndian(rtmsgSpan[8..12], 0); // rtm_flags

        // 3. rtattr RTA_DST (4 + addrBytes.Length)
        var rtaOffset = NetlinkConstants.NlmsgHeaderSize + NetlinkConstants.RtmsgHeaderSize;
        BinaryPrimitives.WriteUInt16LittleEndian(requestBuffer.AsSpan(rtaOffset, 2), rtaLen);
        BinaryPrimitives.WriteUInt16LittleEndian(requestBuffer.AsSpan(rtaOffset + 2, 2), NetlinkConstants.RTA_DST);
        addrBytes.CopyTo(requestBuffer.AsSpan(rtaOffset + 4, addrBytes.Length));

        lock (_syncLock)
        {
            try
            {
                EnsureSocket();
                if (_socket is null)
                {
                    return NetlinkRouteResponse.CreateFailure(destination, "Failed to initialize Netlink socket.", sequence: seq);
                }

                _socket.Send(requestBuffer, SocketFlags.None);

                var responseBuffer = new byte[4096];
                var received = _socket.Receive(responseBuffer, SocketFlags.None);

                if (received <= 0)
                {
                    return NetlinkRouteResponse.CreateFailure(destination, "Empty response from Netlink socket.", sequence: seq);
                }

                return NetlinkMessageParser.ParseRouteLookupResponse(
                    responseBuffer.AsSpan(0, received),
                    destination,
                    expectedSequence: seq);
            }
            catch (SocketException ex)
            {
                ResetSocket();
                return NetlinkRouteResponse.CreateFailure(
                    destination,
                    $"Netlink socket exception: {ex.Message} (ErrorCode: {ex.ErrorCode})",
                    nativeErrorCode: ex.ErrorCode,
                    sequence: seq);
            }
            catch (Exception ex)
            {
                ResetSocket();
                return NetlinkRouteResponse.CreateFailure(
                    destination,
                    $"Unexpected Netlink lookup exception: {ex.Message}",
                    sequence: seq);
            }
        }
    }

    private void EnsureSocket()
    {
        if (_socket is not null) return;

        try
        {
            _socket = new Socket((AddressFamily)NetlinkConstants.AF_NETLINK, SocketType.Raw, (ProtocolType)NetlinkConstants.NETLINK_ROUTE)
            {
                ReceiveTimeout = 2000,
                SendTimeout = 2000
            };
        }
        catch
        {
            _socket = null;
        }
    }

    private void ResetSocket()
    {
        try
        {
            _socket?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _socket = null;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            ResetSocket();
        }
    }
}
