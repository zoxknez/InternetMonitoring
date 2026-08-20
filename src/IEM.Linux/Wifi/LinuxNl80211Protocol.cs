using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace IEM.Linux.Wifi;

/// <summary>
/// Structured interface facts read from NL80211_CMD_GET_INTERFACE.
/// </summary>
public sealed record LinuxNl80211InterfaceInfo(
    int IfIndex,
    string IfName,
    uint WiphyIndex,
    string? WiphyName,
    byte[]? MacAddress,
    int IfType,
    byte[]? Ssid,
    uint? Frequency);

/// <summary>
/// Structured physical wireless device facts read from NL80211_CMD_GET_WIPHY.
/// </summary>
public sealed record LinuxNl80211WiphyInfo(
    uint WiphyIndex,
    string WiphyName);

/// <summary>
/// nl80211 commands, attributes, request encoders, and response decoders.
/// Invariants 249-254.
/// </summary>
public static class LinuxNl80211Protocol
{
    // Commands
    public const byte NL80211_CMD_UNSPEC = 0;
    public const byte NL80211_CMD_GET_WIPHY = 1;
    public const byte NL80211_CMD_SET_WIPHY = 2;
    public const byte NL80211_CMD_NEW_WIPHY = 3;
    public const byte NL80211_CMD_DEL_WIPHY = 4;
    public const byte NL80211_CMD_GET_INTERFACE = 5;
    public const byte NL80211_CMD_SET_INTERFACE = 6;
    public const byte NL80211_CMD_NEW_INTERFACE = 7;
    public const byte NL80211_CMD_DEL_INTERFACE = 8;
    public const byte NL80211_CMD_GET_STATION = 17;
    public const byte NL80211_CMD_GET_SCAN = 32;
    public const byte NL80211_CMD_TRIGGER_SCAN = 33;
    public const byte NL80211_CMD_NEW_SCAN_RESULTS = 34;
    public const byte NL80211_CMD_SCAN_ABORTED = 35;

    // Attributes
    public const ushort NL80211_ATTR_UNSPEC = 0;
    public const ushort NL80211_ATTR_WIPHY = 1;
    public const ushort NL80211_ATTR_WIPHY_NAME = 2;
    public const ushort NL80211_ATTR_IFINDEX = 3;
    public const ushort NL80211_ATTR_IFNAME = 4;
    public const ushort NL80211_ATTR_IFTYPE = 5;
    public const ushort NL80211_ATTR_MAC = 6;
    public const ushort NL80211_ATTR_WIPHY_FREQ = 38;
    public const ushort NL80211_ATTR_SSID = 52;
    public const ushort NL80211_ATTR_STA_INFO = 21;
    public const ushort NL80211_ATTR_BSS = 47;
    public const ushort NL80211_ATTR_SPLIT_WIPHY_DUMP = 174;

    // Interface types
    public const int NL80211_IFTYPE_UNSPECIFIED = 0;
    public const int NL80211_IFTYPE_ADHOC = 1;
    public const int NL80211_IFTYPE_STATION = 2;
    public const int NL80211_IFTYPE_AP = 3;
    public const int NL80211_IFTYPE_AP_VLAN = 4;
    public const int NL80211_IFTYPE_WDS = 5;
    public const int NL80211_IFTYPE_MONITOR = 6;
    public const int NL80211_IFTYPE_MESH_POINT = 7;
    public const int NL80211_IFTYPE_P2P_CLIENT = 8;
    public const int NL80211_IFTYPE_P2P_GO = 9;
    public const int NL80211_IFTYPE_P2P_DEVICE = 10;

    /// <summary>
    /// Builds a GET_INTERFACE request. If ifindex is null, requests a dump of all interfaces.
    /// </summary>
    public static byte[] BuildGetInterfaceRequest(ushort nl80211FamilyId, int? ifindex, uint sequence, uint pid = 0)
    {
        bool isDump = !ifindex.HasValue;
        ushort flags = isDump ? (ushort)(LinuxGenlProtocol.NLM_F_REQUEST | LinuxGenlProtocol.NLM_F_DUMP | LinuxGenlProtocol.NLM_F_ACK)
                              : (ushort)(LinuxGenlProtocol.NLM_F_REQUEST | LinuxGenlProtocol.NLM_F_ACK);

        int attrLen = ifindex.HasValue ? LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) : 0;
        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize + attrLen;

        byte[] buffer = new byte[totalLen];
        var span = buffer.AsSpan();

        // 1. nlmsghdr
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), totalLen);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), nl80211FamilyId);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), pid);

        // 2. genlmsghdr
        span[16] = NL80211_CMD_GET_INTERFACE;
        span[17] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 0); // reserved

        // 3. NL80211_ATTR_IFINDEX (if single query)
        if (ifindex.HasValue)
        {
            ushort nlaLen = (ushort)(LinuxGenlProtocol.NlaHeaderSize + 4);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), nlaLen);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), NL80211_ATTR_IFINDEX);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), ifindex.Value);
        }

        return buffer;
    }

    /// <summary>
    /// Builds a GET_WIPHY request. Includes NL80211_ATTR_SPLIT_WIPHY_DUMP flag for dump requests per kernel spec.
    /// </summary>
    public static byte[] BuildGetWiphyRequest(ushort nl80211FamilyId, uint? wiphyIndex, uint sequence, uint pid = 0)
    {
        bool isDump = !wiphyIndex.HasValue;
        ushort flags = isDump ? (ushort)(LinuxGenlProtocol.NLM_F_REQUEST | LinuxGenlProtocol.NLM_F_DUMP | LinuxGenlProtocol.NLM_F_ACK)
                              : (ushort)(LinuxGenlProtocol.NLM_F_REQUEST | LinuxGenlProtocol.NLM_F_ACK);

        int attrLen = 0;
        if (wiphyIndex.HasValue)
        {
            attrLen += LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
        }
        if (isDump)
        {
            attrLen += LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize); // flag attribute
        }

        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize + attrLen;
        byte[] buffer = new byte[totalLen];
        var span = buffer.AsSpan();

        // 1. nlmsghdr
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), totalLen);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), nl80211FamilyId);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), pid);

        // 2. genlmsghdr
        span[16] = NL80211_CMD_GET_WIPHY;
        span[17] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 0); // reserved

        int offset = 20;
        if (wiphyIndex.HasValue)
        {
            ushort nlaLen = (ushort)(LinuxGenlProtocol.NlaHeaderSize + 4);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), nlaLen);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 2, 2), NL80211_ATTR_WIPHY);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset + 4, 4), wiphyIndex.Value);
            offset += LinuxGenlProtocol.NlaAlign(nlaLen);
        }

        if (isDump)
        {
            ushort nlaLen = LinuxGenlProtocol.NlaHeaderSize; // flag attribute (0 payload)
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), nlaLen);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 2, 2), NL80211_ATTR_SPLIT_WIPHY_DUMP);
        }

        return buffer;
    }

    /// <summary>
    /// Parses an NL80211_CMD_GET_INTERFACE single or multi-part dump response.
    /// </summary>
    public static int ParseInterfaceResponse(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        out List<LinuxNl80211InterfaceInfo> interfaces)
    {
        interfaces = new List<LinuxNl80211InterfaceInfo>();

        if (buffer.Length < LinuxGenlProtocol.NlmsgHeaderSize)
        {
            return -22;
        }

        int offset = 0;
        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return -22;
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            ushort flags = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 6, 2));
            uint seq = MemoryMarshal.Read<uint>(buffer.Slice(offset + 8, 4));

            if ((flags & LinuxGenlProtocol.NLM_F_DUMP_INTR) != 0)
            {
                // Dump was interrupted in kernel; non-authoritative, requires retry
                return -4; // -EINTR
            }

            if (seq != expectedSequence && seq != 0)
            {
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4) return -22;
                int errorCode = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (errorCode < 0) return errorCode;
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE)
            {
                break;
            }

            if (nlmsgLen >= LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize)
            {
                var payload = buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize,
                                           nlmsgLen - (LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize));
                if (TryParseInterfacePayload(payload, out var ifinfo) && ifinfo != null)
                {
                    interfaces.Add(ifinfo);
                }
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }

        return 0;
    }

    private static bool TryParseInterfacePayload(ReadOnlySpan<byte> payload, out LinuxNl80211InterfaceInfo? ifinfo)
    {
        ifinfo = null;

        int ifindex = 0;
        string? ifname = null;
        uint wiphy = 0;
        string? wiphyName = null;
        byte[]? mac = null;
        int iftype = 0;
        byte[]? ssid = null;
        uint? freq = null;

        foreach (var (type, value) in LinuxGenlProtocol.EnumerateAttributes(payload))
        {
            switch (type)
            {
                case NL80211_ATTR_IFINDEX:
                    if (value.Length >= 4) ifindex = MemoryMarshal.Read<int>(value);
                    break;

                case NL80211_ATTR_IFNAME:
                    ifname = LinuxGenlProtocol.ReadNullTerminatedString(value);
                    break;

                case NL80211_ATTR_WIPHY:
                    if (value.Length >= 4) wiphy = MemoryMarshal.Read<uint>(value);
                    break;

                case NL80211_ATTR_WIPHY_NAME:
                    wiphyName = LinuxGenlProtocol.ReadNullTerminatedString(value);
                    break;

                case NL80211_ATTR_MAC:
                    mac = value.ToArray();
                    break;

                case NL80211_ATTR_IFTYPE:
                    if (value.Length >= 4) iftype = MemoryMarshal.Read<int>(value);
                    break;

                case NL80211_ATTR_SSID:
                    ssid = value.ToArray();
                    break;

                case NL80211_ATTR_WIPHY_FREQ:
                    if (value.Length >= 4) freq = MemoryMarshal.Read<uint>(value);
                    break;
            }
        }

        if (ifindex > 0 && !string.IsNullOrEmpty(ifname))
        {
            ifinfo = new LinuxNl80211InterfaceInfo(ifindex, ifname, wiphy, wiphyName, mac, iftype, ssid, freq);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses an NL80211_CMD_GET_WIPHY single or dump response.
    /// </summary>
    public static int ParseWiphyResponse(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        out List<LinuxNl80211WiphyInfo> wiphys)
    {
        wiphys = new List<LinuxNl80211WiphyInfo>();

        if (buffer.Length < LinuxGenlProtocol.NlmsgHeaderSize)
        {
            return -22;
        }

        int offset = 0;
        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return -22;
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            ushort flags = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 6, 2));
            uint seq = MemoryMarshal.Read<uint>(buffer.Slice(offset + 8, 4));

            if ((flags & LinuxGenlProtocol.NLM_F_DUMP_INTR) != 0)
            {
                // Dump was interrupted in kernel; non-authoritative, requires retry
                return -4; // -EINTR
            }

            if (seq != expectedSequence && seq != 0)
            {
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4) return -22;
                int errorCode = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (errorCode < 0) return errorCode;
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE)
            {
                break;
            }

            if (nlmsgLen >= LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize)
            {
                var payload = buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize,
                                           nlmsgLen - (LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize));
                if (TryParseWiphyPayload(payload, out var winfo) && winfo != null)
                {
                    // Deduplicate in split dumps
                    if (!wiphys.Exists(w => w.WiphyIndex == winfo.WiphyIndex))
                    {
                        wiphys.Add(winfo);
                    }
                }
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }

        return 0;
    }

    private static bool TryParseWiphyPayload(ReadOnlySpan<byte> payload, out LinuxNl80211WiphyInfo? winfo)
    {
        winfo = null;
        uint wiphy = 0;
        string? wiphyName = null;

        foreach (var (type, value) in LinuxGenlProtocol.EnumerateAttributes(payload))
        {
            switch (type)
            {
                case NL80211_ATTR_WIPHY:
                    if (value.Length >= 4) wiphy = MemoryMarshal.Read<uint>(value);
                    break;
                case NL80211_ATTR_WIPHY_NAME:
                    wiphyName = LinuxGenlProtocol.ReadNullTerminatedString(value);
                    break;
            }
        }

        if (!string.IsNullOrEmpty(wiphyName))
        {
            winfo = new LinuxNl80211WiphyInfo(wiphy, wiphyName);
            return true;
        }

        return false;
    }
}
