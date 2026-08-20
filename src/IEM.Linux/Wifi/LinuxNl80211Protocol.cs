using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace IEM.Linux.Wifi;

/// <summary>
/// Structured interface facts read from NL80211_CMD_GET_INTERFACE.
/// </summary>
/// <summary>
/// Status of a multi-part or single Netlink dump operation.
/// </summary>
public enum LinuxNl80211DumpStatus
{
    Complete = 0,
    Incomplete = 1,
    Interrupted = 2,
    KernelError = 3,
    TimedOut = 4,
    Cancelled = 5,
    Malformed = 6,
    Unavailable = 7
}

/// <summary>
/// Rich evidence-grade outcome of a Generic Netlink nl80211 dump operation.
/// </summary>
public sealed record LinuxNl80211DumpResult<T>(
    IReadOnlyList<T> Items,
    LinuxNl80211DumpStatus Status,
    int ErrorCode = 0,
    bool SawDone = false,
    bool Interrupted = false)
{
    public bool IsComplete => Status == LinuxNl80211DumpStatus.Complete;
}

public enum LinuxWirelessAssociationState
{
    Unknown = 0,
    NotAssociated = 1,
    Associated = 2
}

public sealed record LinuxAssociatedBssLink(
    string Bssid,
    byte[] BssidBytes,
    byte? MloLinkId,
    string? MldAddress,
    byte[]? MldAddressBytes,
    byte[]? SsidBytes,
    string? DisplaySsid,
    uint? FrequencyMhz,
    int? SignalMbm,
    byte? SignalUnspec,
    uint? SeenMsAgo,
    ulong? LastSeenBootTimeNs);

public sealed record LinuxWirelessAssociationObservation(
    int IfIndex,
    string IfName,
    uint WiphyIndex,
    LinuxWirelessAssociationState State,
    IReadOnlyList<LinuxAssociatedBssLink> Links,
    ulong? Wdev,
    uint? Generation,
    LinuxNl80211DumpStatus DumpStatus);

public sealed record LinuxNl80211BssInfo(
    int IfIndex,
    byte[] Bssid,
    string BssidString,
    byte[]? SsidBytes,
    string? DisplaySsid,
    uint? FrequencyMhz,
    uint? Status,
    int? SignalMbm,
    byte? SignalQuality,
    uint? SeenMsAgo,
    byte? MloLinkId,
    byte[]? MldAddress,
    string? MldAddressString,
    ulong? LastSeenBootTimeNs,
    byte[]? InformationElements,
    ulong? Wdev,
    uint? Generation)
{
    public bool IsAssociated => Status == LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED;
    public int? SignalDbm => SignalMbm.HasValue ? SignalMbm.Value / 100 : null;
}

public sealed record LinuxNl80211InterfaceInfo(
    int IfIndex,
    string IfName,
    uint? WiphyIndex,
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
/// Invariants 249-262.
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

    // Top-level attributes
    public const ushort NL80211_ATTR_UNSPEC = 0;
    public const ushort NL80211_ATTR_WIPHY = 1;
    public const ushort NL80211_ATTR_WIPHY_NAME = 2;
    public const ushort NL80211_ATTR_IFINDEX = 3;
    public const ushort NL80211_ATTR_IFNAME = 4;
    public const ushort NL80211_ATTR_IFTYPE = 5;
    public const ushort NL80211_ATTR_MAC = 6;
    public const ushort NL80211_ATTR_STA_INFO = 21;
    public const ushort NL80211_ATTR_WIPHY_FREQ = 38;
    public const ushort NL80211_ATTR_GENERATION = 46;
    public const ushort NL80211_ATTR_BSS = 47;
    public const ushort NL80211_ATTR_SSID = 52;
    public const ushort NL80211_ATTR_WDEV = 150;
    public const ushort NL80211_ATTR_SPLIT_WIPHY_DUMP = 174;

    // Nested NL80211_ATTR_BSS attributes
    public const ushort NL80211_BSS_UNSPEC = 0;
    public const ushort NL80211_BSS_BSSID = 1;
    public const ushort NL80211_BSS_FREQUENCY = 2;
    public const ushort NL80211_BSS_TSF = 3;
    public const ushort NL80211_BSS_BEACON_INTERVAL = 4;
    public const ushort NL80211_BSS_CAPABILITY = 5;
    public const ushort NL80211_BSS_INFORMATION_ELEMENTS = 6;
    public const ushort NL80211_BSS_SIGNAL_MBM = 7;
    public const ushort NL80211_BSS_SIGNAL_UNSPEC = 8;
    public const ushort NL80211_BSS_STATUS = 9;
    public const ushort NL80211_BSS_BEACON_IES = 10;
    public const ushort NL80211_BSS_SEEN_MS_AGO = 11;
    public const ushort NL80211_BSS_BEACON_TSF = 12;
    public const ushort NL80211_BSS_PRESP_DATA = 13;
    public const ushort NL80211_BSS_LAST_SEEN_BOOTTIME = 14;
    public const ushort NL80211_BSS_FREQUENCY_OFFSET = 20;
    public const ushort NL80211_BSS_MLO_LINK_ID = 21;
    public const ushort NL80211_BSS_MLD_ADDR = 22;

    // BSS status enum (NL80211_BSS_STATUS)
    public const uint NL80211_BSS_STATUS_AUTHENTICATED = 0;
    public const uint NL80211_BSS_STATUS_ASSOCIATED = 1;
    public const uint NL80211_BSS_STATUS_IBSS_JOINED = 2;

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
    /// Parses an NL80211_CMD_GET_INTERFACE single or multi-part dump response with full status provenance.
    /// </summary>
    public static LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo> ParseInterfaceDump(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        bool isDump)
    {
        var interfaces = new List<LinuxNl80211InterfaceInfo>();

        if (buffer.Length < LinuxGenlProtocol.NlmsgHeaderSize)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
        }

        bool seenDone = false;
        int offset = 0;

        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            ushort flags = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 6, 2));
            uint seq = MemoryMarshal.Read<uint>(buffer.Slice(offset + 8, 4));

            // Strict sequence matching: ignore unsolicited notifications / mismatched seq (e.g. seq == 0) BEFORE semantic flags
            if (seq != expectedSequence)
            {
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if ((flags & LinuxGenlProtocol.NLM_F_DUMP_INTR) != 0)
            {
                // Dump was interrupted in kernel for this matching sequence
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Interrupted, -4, Interrupted: true);
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }
                int errorCode = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (errorCode < 0)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.KernelError, errorCode);
                }
                // Pure ACK (errorCode == 0): continue processing dump/response
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE)
            {
                // Strict dump rule: NLMSG_DONE must carry int return code (>= 20 bytes)
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }

                int doneErr = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (doneErr < 0)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.KernelError, doneErr, SawDone: true);
                }

                seenDone = true;
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

        if (isDump)
        {
            if (!seenDone)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Incomplete, -11);
            }
            return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(interfaces, LinuxNl80211DumpStatus.Complete, 0, SawDone: true);
        }
        else
        {
            if (interfaces.Count == 0)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(Array.Empty<LinuxNl80211InterfaceInfo>(), LinuxNl80211DumpStatus.Incomplete, -11);
            }
            return new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(interfaces, LinuxNl80211DumpStatus.Complete, 0, SawDone: true);
        }
    }

    /// <summary>
    /// Parses an NL80211_CMD_GET_INTERFACE single or multi-part dump response.
    /// Invariants 249, 252: Incomplete or interrupted dumps are rejected with negative error.
    /// </summary>
    public static int ParseInterfaceResponse(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        bool isDump,
        out List<LinuxNl80211InterfaceInfo> interfaces)
    {
        var result = ParseInterfaceDump(buffer, expectedSequence, isDump);
        interfaces = new List<LinuxNl80211InterfaceInfo>(result.Items);
        return result.IsComplete ? 0 : (result.ErrorCode != 0 ? result.ErrorCode : -11);
    }

    private static bool TryParseInterfacePayload(ReadOnlySpan<byte> payload, out LinuxNl80211InterfaceInfo? ifinfo)
    {
        ifinfo = null;

        int ifindex = 0;
        string? ifname = null;
        uint? wiphy = null;
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
    /// Parses an NL80211_CMD_GET_WIPHY single or dump response with full status provenance.
    /// </summary>
    public static LinuxNl80211DumpResult<LinuxNl80211WiphyInfo> ParseWiphyDump(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        bool isDump)
    {
        var wiphys = new List<LinuxNl80211WiphyInfo>();

        if (buffer.Length < LinuxGenlProtocol.NlmsgHeaderSize)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
        }

        bool seenDone = false;
        int offset = 0;

        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            ushort flags = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 6, 2));
            uint seq = MemoryMarshal.Read<uint>(buffer.Slice(offset + 8, 4));

            // Strict sequence matching: ignore unsolicited notifications / mismatched seq (e.g. seq == 0) BEFORE semantic flags
            if (seq != expectedSequence)
            {
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if ((flags & LinuxGenlProtocol.NLM_F_DUMP_INTR) != 0)
            {
                // Dump was interrupted in kernel for this matching sequence
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Interrupted, -4, Interrupted: true);
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }
                int errorCode = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (errorCode < 0)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.KernelError, errorCode);
                }
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }

                int doneErr = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (doneErr < 0)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.KernelError, doneErr, SawDone: true);
                }

                seenDone = true;
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

        if (isDump)
        {
            if (!seenDone)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Incomplete, -11);
            }
            return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(wiphys, LinuxNl80211DumpStatus.Complete, 0, SawDone: true);
        }
        else
        {
            if (wiphys.Count == 0)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Incomplete, -11);
            }
            return new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(wiphys, LinuxNl80211DumpStatus.Complete, 0, SawDone: true);
        }
    }

    /// <summary>
    /// Parses an NL80211_CMD_GET_WIPHY single or dump response.
    /// Invariants 249, 252: Incomplete or interrupted dumps are rejected with negative error.
    /// </summary>
    public static int ParseWiphyResponse(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        bool isDump,
        out List<LinuxNl80211WiphyInfo> wiphys)
    {
        var result = ParseWiphyDump(buffer, expectedSequence, isDump);
        wiphys = new List<LinuxNl80211WiphyInfo>(result.Items);
        return result.IsComplete ? 0 : (result.ErrorCode != 0 ? result.ErrorCode : -11);
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

    /// <summary>
    /// Formats a binary MAC address as an uppercase colon-separated string (e.g. "AA:BB:CC:DD:EE:FF").
    /// </summary>
    public static string FormatMacAddress(ReadOnlySpan<byte> mac)
    {
        if (mac.Length == 6)
        {
            return $"{mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}:{mac[3]:X2}:{mac[4]:X2}:{mac[5]:X2}";
        }
        return Convert.ToHexString(mac);
    }

    /// <summary>
    /// Safely extracts SSID bytes and display string from IEEE 802.11 Information Elements (EID 0).
    /// </summary>
    public static (byte[]? SsidBytes, string? DisplaySsid, bool IeParseValid) ExtractSsidFromInformationElements(ReadOnlySpan<byte> ies)
    {
        if (ies.IsEmpty)
        {
            return (null, null, true);
        }

        int offset = 0;
        while (offset + 2 <= ies.Length)
        {
            byte eid = ies[offset];
            byte len = ies[offset + 1];
            offset += 2;

            if (offset + len > ies.Length)
            {
                // Truncated IE sequence
                return (null, null, false);
            }

            if (eid == 0) // SSID parameter set
            {
                if (len > 32)
                {
                    // IEEE 802.11 SSID cannot exceed 32 octets
                    return (null, null, false);
                }

                var ssidBytes = ies.Slice(offset, len).ToArray();
                if (len == 0)
                {
                    // Hidden / zero-length SSID: preserve bytes without synthetic string
                    return (ssidBytes, null, true);
                }

                string display = Encoding.UTF8.GetString(ssidBytes);
                return (ssidBytes, display, true);
            }

            offset += len;
        }

        return (null, null, true);
    }

    /// <summary>
    /// Builds an NL80211_CMD_GET_SCAN dump request for cached BSS records scoped to an interface index.
    /// Invariant 259: Never issues NL80211_CMD_TRIGGER_SCAN.
    /// </summary>
    public static byte[] BuildGetScanRequest(ushort nl80211FamilyId, int ifindex, uint sequence, uint pid = 0)
    {
        ushort flags = (ushort)(LinuxGenlProtocol.NLM_F_REQUEST | LinuxGenlProtocol.NLM_F_DUMP | LinuxGenlProtocol.NLM_F_ACK);
        int attrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
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
        span[16] = NL80211_CMD_GET_SCAN;
        span[17] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 0);

        // 3. NL80211_ATTR_IFINDEX
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), (ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), NL80211_ATTR_IFINDEX);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), ifindex);

        return buffer;
    }

    /// <summary>
    /// Parses an NL80211_CMD_GET_SCAN multi-part dump response with full status provenance,
    /// strict sequence matching, top-level ifindex verification, and MLO link preservation.
    /// </summary>
    public static LinuxNl80211DumpResult<LinuxNl80211BssInfo> ParseBssDump(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        ushort expectedFamilyId,
        int expectedIfIndex)
    {
        var bssList = new List<LinuxNl80211BssInfo>();
        if (buffer.Length < LinuxGenlProtocol.NlmsgHeaderSize)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
        }

        bool seenDone = false;
        int offset = 0;

        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
            }

            ushort nlmsgType = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 4, 2));
            ushort flags = MemoryMarshal.Read<ushort>(buffer.Slice(offset + 6, 2));
            uint seq = MemoryMarshal.Read<uint>(buffer.Slice(offset + 8, 4));

            if (seq != expectedSequence)
            {
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if ((flags & LinuxGenlProtocol.NLM_F_DUMP_INTR) != 0)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Interrupted, -4, Interrupted: true);
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }
                int errorCode = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (errorCode < 0)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.KernelError, errorCode);
                }
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }
                int doneErr = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (doneErr < 0)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.KernelError, doneErr, SawDone: true);
                }
                seenDone = true;
                break;
            }

            if (nlmsgType != expectedFamilyId)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
            }

            if (nlmsgLen >= LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize)
            {
                byte genlCmd = buffer[offset + LinuxGenlProtocol.NlmsgHeaderSize];
                if (genlCmd != NL80211_CMD_NEW_SCAN_RESULTS)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }

                var payload = buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize,
                                           nlmsgLen - (LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize));
                if (!TryParseBssPayload(payload, expectedIfIndex, out var bssInfo))
                {
                    // Structural Netlink or attribution failure
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }

                if (bssInfo != null)
                {
                    bssList.Add(bssInfo);
                }
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }

        if (!seenDone)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Incomplete, -11);
        }

        return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(bssList, LinuxNl80211DumpStatus.Complete, 0, SawDone: true);
    }

    private static bool TryParseBssPayload(ReadOnlySpan<byte> payload, int expectedIfIndex, out LinuxNl80211BssInfo? bssInfo)
    {
        bssInfo = null;

        if (!LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var topAttrs))
        {
            return false; // Malformed top-level attributes
        }

        int? msgIfIndex = null;
        ulong? wdev = null;
        uint? generation = null;
        byte[]? bssAttrBytes = null;

        foreach (var (type, value) in topAttrs)
        {
            switch (type)
            {
                case NL80211_ATTR_IFINDEX:
                    if (value.Length >= 4) msgIfIndex = MemoryMarshal.Read<int>(value);
                    break;
                case NL80211_ATTR_WDEV:
                    if (value.Length >= 8) wdev = MemoryMarshal.Read<ulong>(value);
                    break;
                case NL80211_ATTR_GENERATION:
                    if (value.Length >= 4) generation = MemoryMarshal.Read<uint>(value);
                    break;
                case NL80211_ATTR_BSS:
                    bssAttrBytes = value;
                    break;
            }
        }

        if (msgIfIndex.HasValue && msgIfIndex.Value != expectedIfIndex)
        {
            return false; // Cross-interface attribution violation
        }

        if (bssAttrBytes == null || bssAttrBytes.Length == 0)
        {
            return false; // No BSS nested attribute in NEW_SCAN_RESULTS
        }

        if (!LinuxGenlProtocol.TryEnumerateAttributesStrict(bssAttrBytes, out var bssAttrs))
        {
            return false; // Malformed nested BSS attributes
        }

        byte[]? bssid = null;
        uint? freq = null;
        uint? status = null;
        int? signalMbm = null;
        byte? signalUnspec = null;
        uint? seenMsAgo = null;
        byte? mloLinkId = null;
        byte[]? mldAddr = null;
        ulong? lastSeenBoottime = null;
        byte[]? ies = null;

        foreach (var (btype, bval) in bssAttrs)
        {
            switch (btype)
            {
                case NL80211_BSS_BSSID:
                    if (bval.Length == 6) bssid = bval;
                    else return false; // Invalid BSSID length
                    break;
                case NL80211_BSS_FREQUENCY:
                    if (bval.Length >= 4) freq = MemoryMarshal.Read<uint>(bval);
                    break;
                case NL80211_BSS_STATUS:
                    if (bval.Length >= 4) status = MemoryMarshal.Read<uint>(bval);
                    break;
                case NL80211_BSS_SIGNAL_MBM:
                    if (bval.Length >= 4) signalMbm = MemoryMarshal.Read<int>(bval); // signed s32
                    break;
                case NL80211_BSS_SIGNAL_UNSPEC:
                    if (bval.Length >= 1) signalUnspec = bval[0];
                    break;
                case NL80211_BSS_SEEN_MS_AGO:
                    if (bval.Length >= 4) seenMsAgo = MemoryMarshal.Read<uint>(bval);
                    break;
                case NL80211_BSS_MLO_LINK_ID:
                    if (bval.Length >= 1) mloLinkId = bval[0];
                    break;
                case NL80211_BSS_MLD_ADDR:
                    if (bval.Length == 6) mldAddr = bval;
                    break;
                case NL80211_BSS_LAST_SEEN_BOOTTIME:
                    if (bval.Length >= 8) lastSeenBoottime = MemoryMarshal.Read<ulong>(bval);
                    break;
                case NL80211_BSS_INFORMATION_ELEMENTS:
                    ies = bval;
                    break;
            }
        }

        if (bssid == null)
        {
            return false; // BSSID is mandatory for valid BSS record
        }

        string bssidStr = FormatMacAddress(bssid);
        string? mldStr = mldAddr != null ? FormatMacAddress(mldAddr) : null;

        var (ssidBytes, displaySsid, _) = ies != null
            ? ExtractSsidFromInformationElements(ies)
            : (null, null, true);

        bssInfo = new LinuxNl80211BssInfo(
            IfIndex: msgIfIndex ?? expectedIfIndex,
            Bssid: bssid,
            BssidString: bssidStr,
            SsidBytes: ssidBytes,
            DisplaySsid: displaySsid,
            FrequencyMhz: freq,
            Status: status,
            SignalMbm: signalMbm,
            SignalQuality: signalUnspec,
            SeenMsAgo: seenMsAgo,
            MloLinkId: mloLinkId,
            MldAddress: mldAddr,
            MldAddressString: mldStr,
            LastSeenBootTimeNs: lastSeenBoottime,
            InformationElements: ies,
            Wdev: wdev,
            Generation: generation);

        return true;
    }
}
