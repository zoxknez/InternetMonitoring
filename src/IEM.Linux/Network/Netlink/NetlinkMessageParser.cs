using System.Net;
using System.Net.Sockets;

namespace IEM.Linux.Network.Netlink;

/// <summary>
/// Defensive binary Netlink message parser for FIB route lookups.
/// Strictly enforces message boundaries, alignments, sequence matching, and address lengths.
/// Invariants 271-275.
/// </summary>
public static class NetlinkMessageParser
{
    public static NetlinkRouteResponse ParseRouteLookupResponse(
        ReadOnlySpan<byte> buffer,
        IPAddress destination,
        uint expectedSequence)
    {
        if (buffer.Length < NetlinkConstants.NlmsgHeaderSize)
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Netlink buffer length {buffer.Length} is smaller than nlmsghdr size {NetlinkConstants.NlmsgHeaderSize}.");
        }

        var nlmsgLen = BitConverter.ToInt32(buffer[..4]);
        var nlmsgType = BitConverter.ToUInt16(buffer.Slice(4, 2));
        var nlmsgFlags = BitConverter.ToUInt16(buffer.Slice(6, 2));
        var nlmsgSeq = BitConverter.ToUInt32(buffer.Slice(8, 4));

        if (nlmsgLen < NetlinkConstants.NlmsgHeaderSize || nlmsgLen > buffer.Length)
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Invalid nlmsg_len {nlmsgLen} (Buffer: {buffer.Length}).",
                sequence: nlmsgSeq);
        }

        if (nlmsgSeq != expectedSequence)
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Netlink sequence mismatch (Got: {nlmsgSeq}, Expected: {expectedSequence}).",
                sequence: nlmsgSeq);
        }

        if (nlmsgType == NetlinkConstants.NLMSG_ERROR)
        {
            if (buffer.Length < NetlinkConstants.NlmsgHeaderSize + 4)
            {
                return NetlinkRouteResponse.CreateFailure(
                    destination,
                    "Truncated NLMSG_ERROR payload.",
                    sequence: nlmsgSeq);
            }

            var errorCode = BitConverter.ToInt32(buffer.Slice(NetlinkConstants.NlmsgHeaderSize, 4));
            if (errorCode == 0)
            {
                // ACK response with no payload
                return new NetlinkRouteResponse(
                    IsSuccess: true,
                    Destination: destination,
                    Sequence: nlmsgSeq);
            }

            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Kernel routing error: {errorCode}",
                nativeErrorCode: errorCode,
                sequence: nlmsgSeq);
        }

        if (nlmsgType != NetlinkConstants.RTM_NEWROUTE)
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Unexpected Netlink message type {nlmsgType} (Expected RTM_NEWROUTE).",
                sequence: nlmsgSeq);
        }

        // RTM_NEWROUTE contains rtmsg header (12 bytes)
        var rtmsgOffset = NetlinkConstants.NlmsgHeaderSize;
        if (nlmsgLen < rtmsgOffset + NetlinkConstants.RtmsgHeaderSize)
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Message truncated before rtmsg header (Length: {nlmsgLen}).",
                sequence: nlmsgSeq);
        }

        var rtmFamily = buffer[rtmsgOffset];
        var expectedFamily = destination.AddressFamily == AddressFamily.InterNetwork ? 2 : 10; // AF_INET = 2, AF_INET6 = 10
        if (rtmFamily != expectedFamily)
        {
            return NetlinkRouteResponse.CreateFailure(
                destination,
                $"Address family mismatch in rtmsg (Got: {rtmFamily}, Expected: {expectedFamily}).",
                sequence: nlmsgSeq);
        }

        int? oif = null;
        IPAddress? prefSrc = null;
        IPAddress? fallbackSrc = null;
        IPAddress? gateway = null;
        bool isMultipath = false;

        var rtaOffset = rtmsgOffset + NetlinkConstants.RtmsgHeaderSize;

        while (rtaOffset + NetlinkConstants.RtattrHeaderSize <= nlmsgLen)
        {
            var rtaLen = BitConverter.ToUInt16(buffer.Slice(rtaOffset, 2));
            var rtaType = BitConverter.ToUInt16(buffer.Slice(rtaOffset + 2, 2));

            if (rtaLen < NetlinkConstants.RtattrHeaderSize || rtaOffset + rtaLen > nlmsgLen)
            {
                return NetlinkRouteResponse.CreateFailure(
                    destination,
                    $"Malformed rtattr length {rtaLen} at offset {rtaOffset}.",
                    sequence: nlmsgSeq);
            }

            var payloadOffset = rtaOffset + NetlinkConstants.RtattrHeaderSize;
            var payloadLen = rtaLen - NetlinkConstants.RtattrHeaderSize;
            var payload = buffer.Slice(payloadOffset, payloadLen);

            switch (rtaType)
            {
                case NetlinkConstants.RTA_OIF:
                    if (payloadLen >= 4)
                    {
                        oif = BitConverter.ToInt32(payload[..4]);
                    }
                    break;

                case NetlinkConstants.RTA_PREFSRC:
                    if (destination.AddressFamily == AddressFamily.InterNetwork && payloadLen == 4)
                    {
                        prefSrc = new IPAddress(payload[..4]);
                    }
                    else if (destination.AddressFamily == AddressFamily.InterNetworkV6 && payloadLen == 16)
                    {
                        prefSrc = new IPAddress(payload[..16]);
                    }
                    break;

                case NetlinkConstants.RTA_SRC:
                    if (destination.AddressFamily == AddressFamily.InterNetwork && payloadLen == 4)
                    {
                        fallbackSrc = new IPAddress(payload[..4]);
                    }
                    else if (destination.AddressFamily == AddressFamily.InterNetworkV6 && payloadLen == 16)
                    {
                        fallbackSrc = new IPAddress(payload[..16]);
                    }
                    break;

                case NetlinkConstants.RTA_GATEWAY:
                    if (destination.AddressFamily == AddressFamily.InterNetwork && payloadLen == 4)
                    {
                        gateway = new IPAddress(payload[..4]);
                    }
                    else if (destination.AddressFamily == AddressFamily.InterNetworkV6 && payloadLen == 16)
                    {
                        gateway = new IPAddress(payload[..16]);
                    }
                    break;

                case NetlinkConstants.RTA_MULTIPATH:
                    isMultipath = true;
                    break;
            }

            rtaOffset += NetlinkConstants.RtaAlign(rtaLen);
        }

        var effectiveSource = prefSrc ?? fallbackSrc;

        return new NetlinkRouteResponse(
            IsSuccess: true,
            Destination: destination,
            InterfaceIndex: oif,
            PreferredSource: effectiveSource,
            Gateway: gateway,
            IsMultipath: isMultipath,
            Sequence: nlmsgSeq);
    }
}
