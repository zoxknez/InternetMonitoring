using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace IEM.Linux.Wifi;

/// <summary>
/// Information about a resolved Generic Netlink family (e.g. "nl80211").
/// </summary>
public sealed record GenlFamilyInfo(
    ushort FamilyId,
    string FamilyName,
    uint Version,
    uint HeaderSize,
    uint MaxAttr,
    IReadOnlyDictionary<string, uint> MulticastGroups);

/// <summary>
/// Generic Netlink (GENL) wire framing, constants, and nlctrl parser.
/// Implements CTRL_CMD_GETFAMILY discovery per Linux Netlink specification.
/// </summary>
public static class LinuxGenlProtocol
{
    public const int NETLINK_GENERIC = 16;
    public const ushort GENL_ID_CTRL = 16;
    public const int NlmsgHeaderSize = 16;
    public const int GenlHeaderSize = 4;
    public const int NlaHeaderSize = 4;

    // nlmsghdr types
    public const ushort NLMSG_NOOP = 1;
    public const ushort NLMSG_ERROR = 2;
    public const ushort NLMSG_DONE = 3;
    public const ushort NLMSG_OVERRUN = 4;

    // nlmsghdr flags
    public const ushort NLM_F_REQUEST = 0x01;
    public const ushort NLM_F_MULTI = 0x02;
    public const ushort NLM_F_ACK = 0x04;
    public const ushort NLM_F_ECHO = 0x08;
    public const ushort NLM_F_DUMP_INTR = 0x10;
    public const ushort NLM_F_DUMP_FILTERED = 0x20;

    public const ushort NLM_F_ROOT = 0x100;
    public const ushort NLM_F_MATCH = 0x200;
    public const ushort NLM_F_ATOMIC = 0x400;
    public const ushort NLM_F_DUMP = NLM_F_ROOT | NLM_F_MATCH;

    // genl_ctrl commands
    public const byte CTRL_CMD_UNSPEC = 0;
    public const byte CTRL_CMD_NEWFAMILY = 1;
    public const byte CTRL_CMD_DELFAMILY = 2;
    public const byte CTRL_CMD_GETFAMILY = 3;
    public const byte CTRL_CMD_NEWOPS = 4;
    public const byte CTRL_CMD_DELOPS = 5;
    public const byte CTRL_CMD_GETOPS = 6;
    public const byte CTRL_CMD_NEWMCAST_GRP = 7;
    public const byte CTRL_CMD_DELMCAST_GRP = 8;
    public const byte CTRL_CMD_GETMCAST_GRP = 9;

    // genl_ctrl attributes
    public const ushort CTRL_ATTR_UNSPEC = 0;
    public const ushort CTRL_ATTR_FAMILY_ID = 1;
    public const ushort CTRL_ATTR_FAMILY_NAME = 2;
    public const ushort CTRL_ATTR_VERSION = 3;
    public const ushort CTRL_ATTR_HDRSIZE = 4;
    public const ushort CTRL_ATTR_MAXATTR = 5;
    public const ushort CTRL_ATTR_OPS = 6;
    public const ushort CTRL_ATTR_MCAST_GROUPS = 7;

    // genl_ctrl mcast group attributes
    public const ushort CTRL_ATTR_MCAST_GRP_UNSPEC = 0;
    public const ushort CTRL_ATTR_MCAST_GRP_NAME = 1;
    public const ushort CTRL_ATTR_MCAST_GRP_ID = 2;

    public static int NlmsgAlign(int len) => (len + 3) & ~3;
    public static int NlaAlign(int len) => (len + 3) & ~3;

    /// <summary>
    /// Builds a CTRL_CMD_GETFAMILY Netlink request packet for the given family name.
    /// </summary>
    public static byte[] BuildGetFamilyRequest(string familyName, uint sequence, uint pid = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(familyName);

        byte[] nameBytes = Encoding.UTF8.GetBytes(familyName);
        // Include null terminator in string attribute payload
        int attrPayloadLen = nameBytes.Length + 1;
        int attrLen = NlaHeaderSize + attrPayloadLen;
        int attrAlignedLen = NlaAlign(attrLen);

        int totalLen = NlmsgHeaderSize + GenlHeaderSize + attrAlignedLen;
        byte[] buffer = new byte[totalLen];
        var span = buffer.AsSpan();

        // 1. nlmsghdr
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), totalLen);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), GENL_ID_CTRL);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), (ushort)(NLM_F_REQUEST | NLM_F_ACK));
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), pid);

        // 2. genlmsghdr
        span[16] = CTRL_CMD_GETFAMILY; // cmd
        span[17] = 1;                  // version (1)
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 0); // reserved

        // 3. CTRL_ATTR_FAMILY_NAME (nlattr)
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), (ushort)attrLen);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), CTRL_ATTR_FAMILY_NAME);
        nameBytes.CopyTo(span.Slice(24, nameBytes.Length));
        span[24 + nameBytes.Length] = 0; // null terminator

        return buffer;
    }

    /// <summary>
    /// Parses a Generic Netlink CTRL_CMD_GETFAMILY response buffer.
    /// Returns 0 on success, or negative errno on NLMSG_ERROR.
    /// </summary>
    public static int ParseGetFamilyResponse(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        out GenlFamilyInfo? familyInfo)
    {
        familyInfo = null;

        if (buffer.Length < NlmsgHeaderSize)
        {
            return -22; // EINVAL: Buffer too small
        }

        int offset = 0;
        while (offset + NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return -22; // EINVAL: Invalid / truncated message length
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            ushort flags = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 6, 2));
            uint seq = MemoryMarshal.Read<uint>(buffer.Slice(offset + 8, 4));

            // Verify sequence
            if (seq != expectedSequence && seq != 0)
            {
                offset += NlmsgAlign(nlmsgLen);
                continue;
            }

            // Handle NLMSG_ERROR / ACK
            if (nlmsgType == NLMSG_ERROR)
            {
                if (nlmsgLen < NlmsgHeaderSize + 4)
                {
                    return -22;
                }
                int errorCode = MemoryMarshal.Read<int>(buffer.Slice(offset + NlmsgHeaderSize, 4));
                if (errorCode < 0)
                {
                    return errorCode; // Negative errno
                }
                // ACK with error == 0
                offset += NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == NLMSG_DONE)
            {
                break;
            }

            // Must be GENL family response
            if (nlmsgType == GENL_ID_CTRL || nlmsgType >= 16)
            {
                if (nlmsgLen < NlmsgHeaderSize + GenlHeaderSize)
                {
                    return -22;
                }

                var genlPayload = buffer.Slice(offset + NlmsgHeaderSize + GenlHeaderSize, nlmsgLen - (NlmsgHeaderSize + GenlHeaderSize));
                if (TryParseFamilyPayload(genlPayload, out var parsed))
                {
                    familyInfo = parsed;
                    return 0;
                }
            }

            offset += NlmsgAlign(nlmsgLen);
        }

        return familyInfo != null ? 0 : -2; // ENOENT if not found
    }

    private static bool TryParseFamilyPayload(ReadOnlySpan<byte> payload, out GenlFamilyInfo? familyInfo)
    {
        familyInfo = null;

        ushort familyId = 0;
        string? familyName = null;
        uint version = 0;
        uint hdrSize = 0;
        uint maxAttr = 0;
        var mcastGroups = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach (var (type, value) in EnumerateAttributes(payload))
        {
            switch (type)
            {
                case CTRL_ATTR_FAMILY_ID:
                    if (value.Length >= 2)
                    {
                        familyId = MemoryMarshal.Read<ushort>(value);
                    }
                    break;

                case CTRL_ATTR_FAMILY_NAME:
                    familyName = ReadNullTerminatedString(value);
                    break;

                case CTRL_ATTR_VERSION:
                    if (value.Length >= 4)
                    {
                        version = MemoryMarshal.Read<uint>(value);
                    }
                    else if (value.Length >= 2)
                    {
                        version = MemoryMarshal.Read<ushort>(value);
                    }
                    break;

                case CTRL_ATTR_HDRSIZE:
                    if (value.Length >= 4)
                    {
                        hdrSize = MemoryMarshal.Read<uint>(value);
                    }
                    break;

                case CTRL_ATTR_MAXATTR:
                    if (value.Length >= 4)
                    {
                        maxAttr = MemoryMarshal.Read<uint>(value);
                    }
                    break;

                case CTRL_ATTR_MCAST_GROUPS:
                    ParseMcastGroups(value, mcastGroups);
                    break;
            }
        }

        if (familyId != 0 && !string.IsNullOrEmpty(familyName))
        {
            familyInfo = new GenlFamilyInfo(familyId, familyName, version, hdrSize, maxAttr, mcastGroups);
            return true;
        }

        return false;
    }

    private static void ParseMcastGroups(ReadOnlySpan<byte> payload, Dictionary<string, uint> mcastGroups)
    {
        foreach (var (_, groupData) in EnumerateAttributes(payload))
        {
            string? grpName = null;
            uint grpId = 0;

            foreach (var (subType, subVal) in EnumerateAttributes(groupData))
            {
                switch (subType)
                {
                    case CTRL_ATTR_MCAST_GRP_NAME:
                        grpName = ReadNullTerminatedString(subVal);
                        break;
                    case CTRL_ATTR_MCAST_GRP_ID:
                        if (subVal.Length >= 4)
                        {
                            grpId = MemoryMarshal.Read<uint>(subVal);
                        }
                        break;
                }
            }

            if (!string.IsNullOrEmpty(grpName) && grpId != 0)
            {
                mcastGroups[grpName] = grpId;
            }
        }
    }

    /// <summary>
    /// Enumerates Netlink attributes with 4-byte alignment and truncation guards.
    /// </summary>
    public static List<(ushort Type, byte[] Value)> EnumerateAttributes(ReadOnlySpan<byte> payload)
    {
        var results = new List<(ushort, byte[])>();
        int offset = 0;

        while (offset + NlaHeaderSize <= payload.Length)
        {
            ushort nlaLen = MemoryMarshal.Read<ushort>(payload.Slice(offset, 2));
            ushort rawType = MemoryMarshal.Read<ushort>(payload.Slice(offset + 2, 2));
            // Mask out NLA_F_NESTED (0x8000) and NLA_F_NET_BYTEORDER (0x4000)
            ushort nlaType = (ushort)(rawType & 0x3FFF);

            if (nlaLen < NlaHeaderSize || offset + nlaLen > payload.Length)
            {
                // Invalid or truncated attribute, stop enumeration safely
                break;
            }

            int valLen = nlaLen - NlaHeaderSize;
            byte[] valBytes = payload.Slice(offset + NlaHeaderSize, valLen).ToArray();
            results.Add((nlaType, valBytes));

            offset += NlaAlign(nlaLen);
        }

        return results;
    }

    public static string ReadNullTerminatedString(ReadOnlySpan<byte> span)
    {
        int nullIdx = span.IndexOf((byte)0);
        if (nullIdx >= 0)
        {
            span = span.Slice(0, nullIdx);
        }
        return Encoding.UTF8.GetString(span);
    }
}
