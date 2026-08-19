using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IEM.Core.Model;
using IEM.Linux.Network;
using IEM.Linux.Network.Netlink;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Linux FIB Route Resolver and Netlink message parser.
/// Invariants 271-275 (Phase 3.1-4B).
/// </summary>
public sealed class LinuxRouteResolverTests
{
    private static byte[] BuildRtnetlinkRouteResponse(
        uint seq,
        IPAddress destination,
        int? oif = null,
        IPAddress? prefSrc = null,
        IPAddress? gateway = null,
        bool isMultipath = false,
        ushort msgType = NetlinkConstants.RTM_NEWROUTE,
        int? errorCode = null)
    {
        var isV4 = destination.AddressFamily == AddressFamily.InterNetwork;
        var rtmFamily = isV4 ? (byte)2 : (byte)10;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Placeholder for nlmsghdr (16 bytes)
        writer.Write(new byte[16]);

        if (msgType == NetlinkConstants.NLMSG_ERROR)
        {
            writer.Write(errorCode ?? -101); // -ENETUNREACH
            // followed by original header
            writer.Write(new byte[16]);
        }
        else if (msgType == NetlinkConstants.RTM_NEWROUTE)
        {
            // rtmsg header (12 bytes)
            writer.Write(rtmFamily);
            writer.Write(isV4 ? (byte)32 : (byte)128); // dst_len
            writer.Write((byte)0); // src_len
            writer.Write((byte)0); // tos
            writer.Write((byte)254); // table
            writer.Write((byte)0); // protocol
            writer.Write((byte)0); // scope
            writer.Write((byte)0); // type
            writer.Write((uint)0); // flags

            // RTA_OIF
            if (oif.HasValue)
            {
                WriteRtAttr(writer, NetlinkConstants.RTA_OIF, BitConverter.GetBytes(oif.Value));
            }

            // RTA_PREFSRC
            if (prefSrc is not null)
            {
                WriteRtAttr(writer, NetlinkConstants.RTA_PREFSRC, prefSrc.GetAddressBytes());
            }

            // RTA_GATEWAY
            if (gateway is not null)
            {
                WriteRtAttr(writer, NetlinkConstants.RTA_GATEWAY, gateway.GetAddressBytes());
            }

            // RTA_MULTIPATH
            if (isMultipath)
            {
                WriteRtAttr(writer, NetlinkConstants.RTA_MULTIPATH, new byte[8]);
            }

            // RTA_UNKNOWN (custom attribute to verify safe ignoring)
            WriteRtAttr(writer, 999, new byte[] { 1, 2, 3, 4 });
        }

        var totalLength = (int)ms.Length;
        var bytes = ms.ToArray();

        // Write actual nlmsghdr
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), totalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), msgType);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), 0);

        return bytes;
    }

    private static void WriteRtAttr(BinaryWriter writer, ushort type, byte[] payload)
    {
        var rtaLen = (ushort)(NetlinkConstants.RtattrHeaderSize + payload.Length);
        writer.Write(rtaLen);
        writer.Write(type);
        writer.Write(payload);

        // Alignment to 4-byte boundary
        var aligned = NetlinkConstants.RtaAlign(rtaLen);
        var padding = aligned - rtaLen;
        if (padding > 0)
        {
            writer.Write(new byte[padding]);
        }
    }

    [Fact]
    public void Parser_successfully_parses_IPv4_RTM_NEWROUTE_fixture()
    {
        var dest = IPAddress.Parse("8.8.8.8");
        var prefSrc = IPAddress.Parse("192.168.1.100");
        var gateway = IPAddress.Parse("192.168.1.1");
        var raw = BuildRtnetlinkRouteResponse(seq: 42, dest, oif: 3, prefSrc: prefSrc, gateway: gateway);

        var parsed = NetlinkMessageParser.ParseRouteLookupResponse(raw, dest, expectedSequence: 42);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(dest, parsed.Destination);
        Assert.Equal(3, parsed.InterfaceIndex);
        Assert.Equal(prefSrc, parsed.PreferredSource);
        Assert.Equal(gateway, parsed.Gateway);
        Assert.False(parsed.IsMultipath);
        Assert.Equal(42u, parsed.Sequence);
    }

    [Fact]
    public void Parser_successfully_parses_IPv6_RTM_NEWROUTE_fixture()
    {
        var dest = IPAddress.Parse("2001:4860:4860::8888");
        var prefSrc = IPAddress.Parse("2a02:1234:5678::1");
        var gateway = IPAddress.Parse("fe80::1");
        var raw = BuildRtnetlinkRouteResponse(seq: 101, dest, oif: 5, prefSrc: prefSrc, gateway: gateway);

        var parsed = NetlinkMessageParser.ParseRouteLookupResponse(raw, dest, expectedSequence: 101);

        Assert.True(parsed.IsSuccess);
        Assert.Equal(dest, parsed.Destination);
        Assert.Equal(5, parsed.InterfaceIndex);
        Assert.Equal(prefSrc, parsed.PreferredSource);
        Assert.Equal(gateway, parsed.Gateway);
        Assert.False(parsed.IsMultipath);
        Assert.Equal(101u, parsed.Sequence);
    }

    [Fact]
    public void Parser_detects_and_flags_RTA_MULTIPATH()
    {
        var dest = IPAddress.Parse("1.1.1.1");
        var raw = BuildRtnetlinkRouteResponse(seq: 7, dest, oif: 2, isMultipath: true);

        var parsed = NetlinkMessageParser.ParseRouteLookupResponse(raw, dest, expectedSequence: 7);

        Assert.True(parsed.IsSuccess);
        Assert.True(parsed.IsMultipath);
    }

    [Fact]
    public void Parser_handles_NLMSG_ERROR_with_ENETUNREACH()
    {
        var dest = IPAddress.Parse("192.0.2.1");
        var raw = BuildRtnetlinkRouteResponse(seq: 88, dest, msgType: NetlinkConstants.NLMSG_ERROR, errorCode: -101);

        var parsed = NetlinkMessageParser.ParseRouteLookupResponse(raw, dest, expectedSequence: 88);

        Assert.False(parsed.IsSuccess);
        Assert.Equal(-101, parsed.NativeErrorCode);
        Assert.Contains("101", parsed.ErrorMessage);
    }

    [Fact]
    public void Parser_rejects_mismatched_sequence()
    {
        var dest = IPAddress.Parse("8.8.8.8");
        var raw = BuildRtnetlinkRouteResponse(seq: 123, dest, oif: 1);

        var parsed = NetlinkMessageParser.ParseRouteLookupResponse(raw, dest, expectedSequence: 999);

        Assert.False(parsed.IsSuccess);
        Assert.Contains("sequence mismatch", parsed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_rejects_truncated_or_malformed_buffer()
    {
        var dest = IPAddress.Parse("8.8.8.8");

        // Buffer smaller than nlmsghdr
        var tiny = new byte[8];
        var resTiny = NetlinkMessageParser.ParseRouteLookupResponse(tiny, dest, 1);
        Assert.False(resTiny.IsSuccess);

        // Buffer with invalid nlmsg_len exceeding buffer size
        var invalidLen = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(invalidLen.AsSpan(0, 4), 100);
        var resInvalid = NetlinkMessageParser.ParseRouteLookupResponse(invalidLen, dest, 0);
        Assert.False(resInvalid.IsSuccess);
    }

    [Fact]
    public void Parser_rejects_truncated_address_attribute()
    {
        var dest = IPAddress.Parse("8.8.8.8");
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(new byte[16]); // nlmsghdr placeholder
        writer.Write((byte)2);      // rtm_family = AF_INET
        writer.Write(new byte[11]); // remainder of rtmsg header (11 bytes)

        // Write RTA_PREFSRC with only 2 bytes payload instead of 4
        writer.Write((ushort)6); // rta_len = 4 + 2 = 6
        writer.Write(NetlinkConstants.RTA_PREFSRC);
        writer.Write((byte)192);
        writer.Write((byte)168);

        var bytes = ms.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), NetlinkConstants.RTM_NEWROUTE);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 50);

        var parsed = NetlinkMessageParser.ParseRouteLookupResponse(bytes, dest, expectedSequence: 50);

        Assert.True(parsed.IsSuccess);
        // Truncated PREFSRC is ignored, PreferredSource remains null (no partial IP)
        Assert.Null(parsed.PreferredSource);
    }

    [Fact]
    public void RouteResolver_cache_and_invalidation()
    {
        var resolver = new LinuxRouteResolver();

        // Querying non-existent/loopback on non-Linux test runner returns Unresolved safely
        var dest = IPAddress.Parse("127.0.0.1");
        var path1 = resolver.Resolve(dest);

        Assert.Equal(ProbePath.Unresolved, path1);

        // Invalidate does not throw
        resolver.Invalidate();
        var path2 = resolver.Resolve(dest);
        Assert.Equal(ProbePath.Unresolved, path2);
    }
}
