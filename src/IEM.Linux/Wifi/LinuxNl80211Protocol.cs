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

public sealed record LinuxComposedAssociationObservation(
    int IfIndex,
    string IfName,
    uint WiphyIndex,
    LinuxWirelessAssociationState State,
    IReadOnlyList<LinuxAssociatedBssLink> Links,
    ulong? Wdev,
    uint? Generation,
    LinuxNl80211StationInfo? StationInfo,
    bool ContinuityVerified,
    LinuxNl80211DumpStatus DumpStatus);

public enum LinuxMloCompositionState
{
    NotMlo = 0,
    Valid = 1,
    Incomplete = 2,
    Conflicted = 3
}

public sealed record LinuxMloAssociationInfo(
    LinuxMloCompositionState State,
    byte[]? MldAddressBytes,
    string? MldAddress,
    byte[]? SsidBytes,
    string? DisplaySsid,
    IReadOnlyList<LinuxAssociatedBssLink> Links);

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

public sealed record LinuxNl80211RateInfo(
    ulong? BitrateBps,
    uint? Bitrate100Kbps,
    byte? Mcs,
    byte? VhtMcs,
    byte? VhtNss,
    byte? HeMcs,
    byte? HeNss,
    byte? HeGi,
    byte? EhtMcs,
    byte? EhtNss,
    byte? EhtGi,
    byte? EhtRuAlloc,
    bool Is40Mhz,
    bool Is80Mhz,
    bool Is160Mhz,
    bool Is320Mhz,
    bool IsShortGi);

public sealed record LinuxNl80211LinkStationInfo(
    byte LinkId,
    sbyte? SignalDbm,
    sbyte? SignalAverageDbm,
    ulong? RxBytes,
    ulong? TxBytes,
    uint? RxPackets,
    uint? TxPackets,
    LinuxNl80211RateInfo? TxRate,
    LinuxNl80211RateInfo? RxRate);

public sealed record LinuxNl80211StationInfo(
    int IfIndex,
    byte[] PeerMac,
    string PeerMacString,
    uint Generation,
    sbyte? SignalDbm,
    sbyte? SignalAverageDbm,
    ulong? RxBytes,
    ulong? TxBytes,
    uint? RxPackets,
    uint? TxPackets,
    uint? TxRetries,
    uint? TxFailed,
    uint? ConnectedTimeSeconds,
    LinuxNl80211RateInfo? TxRate,
    LinuxNl80211RateInfo? RxRate,
    uint? ExpectedThroughputKbps,
    ulong? RxDurationUsec,
    ulong? TxDurationUsec,
    ulong? AssociationBootTimeNs,
    byte? MloLinkId = null,
    byte[]? MldAddress = null,
    string? MldAddressString = null,
    IReadOnlyList<LinuxNl80211LinkStationInfo>? Links = null)
{
    public IReadOnlyList<LinuxNl80211LinkStationInfo> Links { get; init; } = Links ?? Array.Empty<LinuxNl80211LinkStationInfo>();
}

public sealed record LinuxNl80211StationCorrelationToken(
    int IfIndex,
    ulong Wdev,
    uint WiphyIndex,
    byte[] PeerMac,
    string PeerMacString,
    uint BssGeneration);

public sealed record LinuxNl80211SingleResult<T>(
    T? Item,
    LinuxNl80211DumpStatus Status,
    int ErrorCode = 0,
    bool Interrupted = false,
    bool SawAck = false,
    bool SawDone = false)
{
    public bool IsSuccess => Status == LinuxNl80211DumpStatus.Complete && Item != null;
}

public sealed record LinuxNl80211InterfaceInfo(
    int IfIndex,
    string IfName,
    uint? WiphyIndex,
    string? WiphyName,
    byte[]? MacAddress,
    int IfType,
    byte[]? Ssid,
    uint? Frequency,
    ulong? Wdev = null);

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
    public const byte NL80211_CMD_SET_STATION = 18;
    public const byte NL80211_CMD_NEW_STATION = 19;
    public const byte NL80211_CMD_DEL_STATION = 20;
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
    public const ushort NL80211_ATTR_WDEV = 153;
    public const ushort NL80211_ATTR_SPLIT_WIPHY_DUMP = 174;
    public const ushort NL80211_ATTR_MLO_LINKS = 312;
    public const ushort NL80211_ATTR_MLO_LINK_ID = 313;
    public const ushort NL80211_ATTR_MLD_ADDR = 314;

    // Nested NL80211_ATTR_BSS attributes (Linux kernel enum nl80211_bss)
    public const ushort NL80211_BSS_INVALID = 0;
    public const ushort NL80211_BSS_BSSID = 1;
    public const ushort NL80211_BSS_FREQUENCY = 2;
    public const ushort NL80211_BSS_TSF = 3;
    public const ushort NL80211_BSS_BEACON_INTERVAL = 4;
    public const ushort NL80211_BSS_CAPABILITY = 5;
    public const ushort NL80211_BSS_INFORMATION_ELEMENTS = 6;
    public const ushort NL80211_BSS_SIGNAL_MBM = 7;
    public const ushort NL80211_BSS_SIGNAL_UNSPEC = 8;
    public const ushort NL80211_BSS_STATUS = 9;
    public const ushort NL80211_BSS_SEEN_MS_AGO = 10;
    public const ushort NL80211_BSS_BEACON_IES = 11;
    public const ushort NL80211_BSS_CHAN_WIDTH = 12;
    public const ushort NL80211_BSS_BEACON_TSF = 13;
    public const ushort NL80211_BSS_PRESP_DATA = 14;
    public const ushort NL80211_BSS_LAST_SEEN_BOOTTIME = 15;
    public const ushort NL80211_BSS_PAD = 16;
    public const ushort NL80211_BSS_PARENT_TSF = 17;
    public const ushort NL80211_BSS_PARENT_BSSID = 18;
    public const ushort NL80211_BSS_CHAIN_SIGNAL = 19;
    public const ushort NL80211_BSS_FREQUENCY_OFFSET = 20;
    public const ushort NL80211_BSS_MLO_LINK_ID = 21;
    public const ushort NL80211_BSS_MLD_ADDR = 22;
    public const ushort NL80211_BSS_USE_FOR = 23;
    public const ushort NL80211_BSS_CANNOT_USE_REASONS = 24;

    // Nested NL80211_ATTR_STA_INFO attributes (Linux kernel enum nl80211_sta_info)
    public const ushort NL80211_STA_INFO_INVALID = 0;
    public const ushort NL80211_STA_INFO_INACTIVE_TIME = 1;
    public const ushort NL80211_STA_INFO_RX_BYTES = 2;
    public const ushort NL80211_STA_INFO_TX_BYTES = 3;
    public const ushort NL80211_STA_INFO_SIGNAL = 7;
    public const ushort NL80211_STA_INFO_TX_BITRATE = 8;
    public const ushort NL80211_STA_INFO_RX_PACKETS = 9;
    public const ushort NL80211_STA_INFO_TX_PACKETS = 10;
    public const ushort NL80211_STA_INFO_TX_RETRIES = 11;
    public const ushort NL80211_STA_INFO_TX_FAILED = 12;
    public const ushort NL80211_STA_INFO_SIGNAL_AVG = 13;
    public const ushort NL80211_STA_INFO_RX_BITRATE = 14;
    public const ushort NL80211_STA_INFO_CONNECTED_TIME = 16;
    public const ushort NL80211_STA_INFO_RX_BYTES64 = 23;
    public const ushort NL80211_STA_INFO_TX_BYTES64 = 24;
    public const ushort NL80211_STA_INFO_EXPECTED_THROUGHPUT = 27;
    public const ushort NL80211_STA_INFO_BEACON_RX = 29;
    public const ushort NL80211_STA_INFO_BEACON_SIGNAL_AVG = 30;
    public const ushort NL80211_STA_INFO_RX_DURATION = 32;
    public const ushort NL80211_STA_INFO_ACK_SIGNAL = 34;
    public const ushort NL80211_STA_INFO_ACK_SIGNAL_AVG = 35;
    public const ushort NL80211_STA_INFO_TX_DURATION = 39;
    public const ushort NL80211_STA_INFO_ASSOC_AT_BOOTTIME = 42;

    // Nested rate info attributes (Linux kernel enum nl80211_rate_info)
    public const ushort NL80211_RATE_INFO_INVALID = 0;
    public const ushort NL80211_RATE_INFO_BITRATE = 1;
    public const ushort NL80211_RATE_INFO_MCS = 2;
    public const ushort NL80211_RATE_INFO_40_MHZ_WIDTH = 3;
    public const ushort NL80211_RATE_INFO_SHORT_GI = 4;
    public const ushort NL80211_RATE_INFO_BITRATE32 = 5;
    public const ushort NL80211_RATE_INFO_VHT_MCS = 6;
    public const ushort NL80211_RATE_INFO_VHT_NSS = 7;
    public const ushort NL80211_RATE_INFO_80_MHZ_WIDTH = 8;
    public const ushort NL80211_RATE_INFO_80P80_MHZ_WIDTH = 9;
    public const ushort NL80211_RATE_INFO_160_MHZ_WIDTH = 10;
    public const ushort NL80211_RATE_INFO_10_MHZ_WIDTH = 11;
    public const ushort NL80211_RATE_INFO_5_MHZ_WIDTH = 12;
    public const ushort NL80211_RATE_INFO_HE_MCS = 13;
    public const ushort NL80211_RATE_INFO_HE_NSS = 14;
    public const ushort NL80211_RATE_INFO_HE_GI = 15;
    public const ushort NL80211_RATE_INFO_HE_DCM = 16;
    public const ushort NL80211_RATE_INFO_HE_RU_ALLOC = 17;
    public const ushort NL80211_RATE_INFO_320_MHZ_WIDTH = 18;
    public const ushort NL80211_RATE_INFO_EHT_MCS = 19;
    public const ushort NL80211_RATE_INFO_EHT_NSS = 20;
    public const ushort NL80211_RATE_INFO_EHT_GI = 21;
    public const ushort NL80211_RATE_INFO_EHT_RU_ALLOC = 22;

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
        ulong? wdev = null;

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

                case NL80211_ATTR_WDEV:
                    if (value.Length == 8) wdev = MemoryMarshal.Read<ulong>(value);
                    break;
            }
        }

        if (ifindex > 0 && !string.IsNullOrEmpty(ifname))
        {
            ifinfo = new LinuxNl80211InterfaceInfo(ifindex, ifname, wiphy, wiphyName, mac, iftype, ssid, freq, wdev);
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
    /// strict sequence matching, top-level ifindex and wdev verification, generation consistency, and MLO link preservation.
    /// Invariant 261: Requires non-nullable expectedWdev for strict identity attribution.
    /// </summary>
    public static LinuxNl80211DumpResult<LinuxNl80211BssInfo> ParseBssDump(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        ushort expectedFamilyId,
        int expectedIfIndex,
        ulong expectedWdev)
    {
        var bssList = new List<LinuxNl80211BssInfo>();
        if (buffer.Length < LinuxGenlProtocol.NlmsgHeaderSize)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
        }

        bool seenDone = false;
        int offset = 0;
        uint? dumpGeneration = null;

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

            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize)
            {
                // Invariant 261: Matching-family frame without complete genlmsghdr is never ignored; must fail-closed as Malformed
                return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
            }

            byte genlCmd = buffer[offset + LinuxGenlProtocol.NlmsgHeaderSize];
            if (genlCmd != NL80211_CMD_NEW_SCAN_RESULTS)
            {
                return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
            }

            var payload = buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize,
                                       nlmsgLen - (LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize));
            if (!TryParseBssPayload(payload, expectedIfIndex, expectedWdev, out var bssInfo))
            {
                // Structural Netlink, exact length, or attribution failure
                return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
            }

            if (bssInfo != null)
            {
                if (!bssInfo.Generation.HasValue)
                {
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);
                }

                if (dumpGeneration == null)
                {
                    dumpGeneration = bssInfo.Generation.Value;
                }
                else if (dumpGeneration.Value != bssInfo.Generation.Value)
                {
                    // Invariant: Generation shifted during dump; snapshot must be retried
                    return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Interrupted, -11, Interrupted: true);
                }

                bssList.Add(bssInfo);
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }

        if (!seenDone)
        {
            return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Incomplete, -11);
        }

        return new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(bssList, LinuxNl80211DumpStatus.Complete, 0, SawDone: true);
    }

    /// <summary>
    /// Builds an NL80211_CMD_GET_STATION single request for a specific peer MAC scoped to an interface.
    /// Invariant 257: Requests exact peer station metadata without dumping.
    /// </summary>
    public static byte[] BuildGetStationRequest(ushort nl80211FamilyId, int ifindex, ReadOnlySpan<byte> peerMac, uint sequence, uint pid = 0)
    {
        if (peerMac.Length != 6)
        {
            throw new ArgumentException("Peer MAC address must be exactly 6 octets.", nameof(peerMac));
        }

        ushort flags = (ushort)(LinuxGenlProtocol.NLM_F_REQUEST | LinuxGenlProtocol.NLM_F_ACK);
        int ifindexAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
        int macAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 6);
        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize + ifindexAttrLen + macAttrLen;

        byte[] buffer = new byte[totalLen];
        var span = buffer.AsSpan();

        // 1. nlmsghdr
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(0, 4), totalLen);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), nl80211FamilyId);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8, 4), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), pid);

        // 2. genlmsghdr
        span[16] = NL80211_CMD_GET_STATION;
        span[17] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(18, 2), 0);

        // 3. NL80211_ATTR_IFINDEX
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2), (ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), NL80211_ATTR_IFINDEX);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), ifindex);

        // 4. NL80211_ATTR_MAC
        int offset = 20 + ifindexAttrLen;
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), (ushort)(LinuxGenlProtocol.NlaHeaderSize + 6));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 2, 2), NL80211_ATTR_MAC);
        peerMac.CopyTo(span.Slice(offset + 4, 6));

        return buffer;
    }

    /// <summary>
    /// Parses an NL80211_CMD_GET_STATION response with full status provenance,
    /// strict sequence matching, top-level ifindex, WDEV, MAC, and generation verification, and nested STA_INFO parsing.
    /// Invariant 257: Enforces strict correlation with the expected interface index, expected WDEV, and peer MAC.
    /// </summary>
    public static LinuxNl80211SingleResult<LinuxNl80211StationInfo> ParseStationResponse(
        ReadOnlySpan<byte> buffer,
        uint expectedSequence,
        ushort expectedFamilyId,
        int expectedIfIndex,
        ulong expectedWdev,
        ReadOnlySpan<byte> expectedPeerMac)
    {
        if (buffer.Length < LinuxGenlProtocol.NlmsgHeaderSize || expectedPeerMac.Length != 6)
        {
            return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Malformed, -22);
        }

        bool seenAck = false;
        LinuxNl80211StationInfo? stationInfo = null;
        int offset = 0;

        while (offset + LinuxGenlProtocol.NlmsgHeaderSize <= buffer.Length)
        {
            int nlmsgLen = MemoryMarshal.Read<int>(buffer.Slice(offset, 4));
            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize || offset + nlmsgLen > buffer.Length)
            {
                return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Malformed, -22);
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
                return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Interrupted, -4, Interrupted: true);
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_ERROR)
            {
                if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Malformed, -22);
                }
                int errorCode = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                if (errorCode < 0)
                {
                    return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.KernelError, errorCode);
                }
                seenAck = true;
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType == LinuxGenlProtocol.NLMSG_DONE)
            {
                if (nlmsgLen >= LinuxGenlProtocol.NlmsgHeaderSize + 4)
                {
                    int doneErr = MemoryMarshal.Read<int>(buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize, 4));
                    if (doneErr < 0)
                    {
                        return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.KernelError, doneErr, SawDone: true);
                    }
                }
                offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
                continue;
            }

            if (nlmsgType != expectedFamilyId)
            {
                return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Malformed, -22);
            }

            if (nlmsgLen < LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize)
            {
                return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Malformed, -22);
            }

            byte genlCmd = buffer[offset + LinuxGenlProtocol.NlmsgHeaderSize];
            if (genlCmd != NL80211_CMD_NEW_STATION)
            {
                return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Malformed, -22);
            }

            var payload = buffer.Slice(offset + LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize,
                                       nlmsgLen - (LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize));
            if (!TryParseStationPayload(payload, expectedIfIndex, expectedWdev, expectedPeerMac, out stationInfo))
            {
                return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Malformed, -22);
            }

            offset += LinuxGenlProtocol.NlmsgAlign(nlmsgLen);
        }

        if (stationInfo == null)
        {
            return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.Incomplete, -11, SawAck: seenAck);
        }

        return new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(stationInfo, LinuxNl80211DumpStatus.Complete, 0, SawAck: seenAck);
    }

    private static bool TryParseStationPayload(
        ReadOnlySpan<byte> payload,
        int expectedIfIndex,
        ulong expectedWdev,
        ReadOnlySpan<byte> expectedPeerMac,
        out LinuxNl80211StationInfo? stationInfo)
    {
        stationInfo = null;

        if (!LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var topAttrs))
        {
            return false;
        }

        int? msgIfIndex = null;
        ulong? msgWdev = null;
        byte[]? mac = null;
        uint? generation = null;
        byte[]? staInfoBytes = null;
        byte? mloLinkId = null;
        byte[]? mldAddr = null;

        foreach (var (type, value) in topAttrs)
        {
            switch (type)
            {
                case NL80211_ATTR_IFINDEX:
                    if (value.Length == 4) msgIfIndex = MemoryMarshal.Read<int>(value);
                    else return false;
                    break;
                case NL80211_ATTR_WDEV:
                    if (value.Length == 8) msgWdev = MemoryMarshal.Read<ulong>(value);
                    else return false;
                    break;
                case NL80211_ATTR_MAC:
                    if (value.Length == 6) mac = value;
                    else return false;
                    break;
                case NL80211_ATTR_GENERATION:
                    if (value.Length == 4) generation = MemoryMarshal.Read<uint>(value);
                    else return false;
                    break;
                case NL80211_ATTR_STA_INFO:
                    staInfoBytes = value;
                    break;
                case NL80211_ATTR_MLO_LINK_ID:
                    if (value.Length == 1) mloLinkId = value[0];
                    else return false;
                    break;
                case NL80211_ATTR_MLD_ADDR:
                    if (value.Length == 6) mldAddr = value;
                    else return false;
                    break;
            }
        }

        // Strict provenance check: mandatory IFINDEX, WDEV matching expected, MAC matching expected, GENERATION, and STA_INFO
        if (!msgIfIndex.HasValue || msgIfIndex.Value != expectedIfIndex)
        {
            return false;
        }

        if (!msgWdev.HasValue || msgWdev.Value != expectedWdev)
        {
            return false; // Invariant: WDEV must strictly match requested device
        }

        if (mac == null || !mac.AsSpan().SequenceEqual(expectedPeerMac))
        {
            return false; // Invariant 257: peer MAC must match requested peer
        }

        if (!generation.HasValue || staInfoBytes == null || staInfoBytes.Length == 0)
        {
            return false;
        }

        if (!LinuxGenlProtocol.TryEnumerateAttributesStrict(staInfoBytes, out var staAttrs))
        {
            return false;
        }

        sbyte? signal = null;
        sbyte? signalAvg = null;
        ulong? rxBytes = null;
        ulong? txBytes = null;
        uint? rxPackets = null;
        uint? txPackets = null;
        uint? txRetries = null;
        uint? txFailed = null;
        uint? connectedTime = null;
        LinuxNl80211RateInfo? txRate = null;
        LinuxNl80211RateInfo? rxRate = null;
        uint? expectedThroughput = null;
        ulong? rxDuration = null;
        ulong? txDuration = null;
        ulong? assocBootTime = null;

        uint? rxBytes32 = null;
        uint? txBytes32 = null;
        ulong? rxBytes64 = null;
        ulong? txBytes64 = null;

        foreach (var (stype, sval) in staAttrs)
        {
            switch (stype)
            {
                case NL80211_STA_INFO_SIGNAL:
                    if (sval.Length == 1) signal = unchecked((sbyte)sval[0]);
                    else return false;
                    break;

                case NL80211_STA_INFO_SIGNAL_AVG:
                    if (sval.Length == 1) signalAvg = unchecked((sbyte)sval[0]);
                    else return false;
                    break;

                case NL80211_STA_INFO_RX_BYTES:
                    if (sval.Length == 4) rxBytes32 = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_TX_BYTES:
                    if (sval.Length == 4) txBytes32 = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_RX_BYTES64:
                    if (sval.Length == 8) rxBytes64 = MemoryMarshal.Read<ulong>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_TX_BYTES64:
                    if (sval.Length == 8) txBytes64 = MemoryMarshal.Read<ulong>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_RX_PACKETS:
                    if (sval.Length == 4) rxPackets = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_TX_PACKETS:
                    if (sval.Length == 4) txPackets = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_TX_RETRIES:
                    if (sval.Length == 4) txRetries = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_TX_FAILED:
                    if (sval.Length == 4) txFailed = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_CONNECTED_TIME:
                    if (sval.Length == 4) connectedTime = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_EXPECTED_THROUGHPUT:
                    if (sval.Length == 4) expectedThroughput = MemoryMarshal.Read<uint>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_RX_DURATION:
                    if (sval.Length == 8) rxDuration = MemoryMarshal.Read<ulong>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_TX_DURATION:
                    if (sval.Length == 8) txDuration = MemoryMarshal.Read<ulong>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_ASSOC_AT_BOOTTIME:
                    if (sval.Length == 8) assocBootTime = MemoryMarshal.Read<ulong>(sval);
                    else return false;
                    break;

                case NL80211_STA_INFO_TX_BITRATE:
                    if (!TryParseRateInfo(sval, out txRate)) return false;
                    break;

                case NL80211_STA_INFO_RX_BITRATE:
                    if (!TryParseRateInfo(sval, out rxRate)) return false;
                    break;
            }
        }

        rxBytes = rxBytes64 ?? rxBytes32;
        txBytes = txBytes64 ?? txBytes32;
        string? mldStr = mldAddr != null ? FormatMacAddress(mldAddr) : null;

        stationInfo = new LinuxNl80211StationInfo(
            IfIndex: msgIfIndex.Value,
            PeerMac: mac,
            PeerMacString: FormatMacAddress(mac),
            Generation: generation.Value,
            SignalDbm: signal,
            SignalAverageDbm: signalAvg,
            RxBytes: rxBytes,
            TxBytes: txBytes,
            RxPackets: rxPackets,
            TxPackets: txPackets,
            TxRetries: txRetries,
            TxFailed: txFailed,
            ConnectedTimeSeconds: connectedTime,
            TxRate: txRate,
            RxRate: rxRate,
            ExpectedThroughputKbps: expectedThroughput,
            RxDurationUsec: rxDuration,
            TxDurationUsec: txDuration,
            AssociationBootTimeNs: assocBootTime,
            MloLinkId: mloLinkId,
            MldAddress: mldAddr,
            MldAddressString: mldStr,
            Links: Array.Empty<LinuxNl80211LinkStationInfo>());

        return true;
    }

    /// <summary>
    /// Parses nested nl80211_rate_info attributes.
    /// Prefers BITRATE32 (u32, 100 kbit/s) over legacy BITRATE (u16, 100 kbit/s).
    /// </summary>
    public static bool TryParseRateInfo(ReadOnlySpan<byte> rateAttrBytes, out LinuxNl80211RateInfo? rateInfo)
    {
        rateInfo = null;
        if (!LinuxGenlProtocol.TryEnumerateAttributesStrict(rateAttrBytes, out var attrs))
        {
            return false;
        }

        ushort? bitrate16 = null;
        uint? bitrate32 = null;
        byte? mcs = null;
        byte? vhtMcs = null;
        byte? vhtNss = null;
        byte? heMcs = null;
        byte? heNss = null;
        byte? heGi = null;
        byte? ehtMcs = null;
        byte? ehtNss = null;
        byte? ehtGi = null;
        byte? ehtRuAlloc = null;
        bool is40 = false;
        bool is80 = false;
        bool is80p80 = false;
        bool is160 = false;
        bool is320 = false;
        bool isShortGi = false;

        foreach (var (type, value) in attrs)
        {
            switch (type)
            {
                case NL80211_RATE_INFO_BITRATE:
                    if (value.Length == 2) bitrate16 = MemoryMarshal.Read<ushort>(value);
                    else return false;
                    break;

                case NL80211_RATE_INFO_BITRATE32:
                    if (value.Length == 4) bitrate32 = MemoryMarshal.Read<uint>(value);
                    else return false;
                    break;

                case NL80211_RATE_INFO_MCS:
                    if (value.Length == 1) mcs = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_VHT_MCS:
                    if (value.Length == 1) vhtMcs = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_VHT_NSS:
                    if (value.Length == 1) vhtNss = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_HE_MCS:
                    if (value.Length == 1) heMcs = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_HE_NSS:
                    if (value.Length == 1) heNss = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_HE_GI:
                    if (value.Length == 1) heGi = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_EHT_MCS:
                    if (value.Length == 1) ehtMcs = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_EHT_NSS:
                    if (value.Length == 1) ehtNss = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_EHT_GI:
                    if (value.Length == 1) ehtGi = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_EHT_RU_ALLOC:
                    if (value.Length == 1) ehtRuAlloc = value[0];
                    else return false;
                    break;

                case NL80211_RATE_INFO_40_MHZ_WIDTH:
                    if (value.Length != 0) return false;
                    is40 = true;
                    break;

                case NL80211_RATE_INFO_80_MHZ_WIDTH:
                    if (value.Length != 0) return false;
                    is80 = true;
                    break;

                case NL80211_RATE_INFO_80P80_MHZ_WIDTH:
                    if (value.Length != 0) return false;
                    is80p80 = true;
                    break;

                case NL80211_RATE_INFO_160_MHZ_WIDTH:
                    if (value.Length != 0) return false;
                    is160 = true;
                    break;

                case NL80211_RATE_INFO_320_MHZ_WIDTH:
                    if (value.Length != 0) return false;
                    is320 = true;
                    break;

                case NL80211_RATE_INFO_SHORT_GI:
                    if (value.Length != 0) return false;
                    isShortGi = true;
                    break;
            }
        }

        // Rule: Channel width flags are mutually exclusive
        int widthCount = (is40 ? 1 : 0) + (is80 ? 1 : 0) + (is80p80 ? 1 : 0) + (is160 ? 1 : 0) + (is320 ? 1 : 0);
        if (widthCount > 1)
        {
            return false; // Conflicting channel widths
        }

        // Rule: BITRATE32 is preferred authoritative bitrate (units: 100 kbit/s = 100,000 bit/s).
        // Fallback: BITRATE (u16, units 100 kbit/s). Never merge or add. Missing rate is null, not zero.
        ulong? bps = null;
        uint? raw100k = null;

        if (bitrate32.HasValue)
        {
            bps = (ulong)bitrate32.Value * 100_000UL;
            raw100k = bitrate32.Value;
        }
        else if (bitrate16.HasValue)
        {
            bps = (ulong)bitrate16.Value * 100_000UL;
            raw100k = bitrate16.Value;
        }

        rateInfo = new LinuxNl80211RateInfo(
            BitrateBps: bps,
            Bitrate100Kbps: raw100k,
            Mcs: mcs,
            VhtMcs: vhtMcs,
            VhtNss: vhtNss,
            HeMcs: heMcs,
            HeNss: heNss,
            HeGi: heGi,
            EhtMcs: ehtMcs,
            EhtNss: ehtNss,
            EhtGi: ehtGi,
            EhtRuAlloc: ehtRuAlloc,
            Is40Mhz: is40,
            Is80Mhz: is80 || is80p80,
            Is160Mhz: is160,
            Is320Mhz: is320,
            IsShortGi: isShortGi);

        return true;
    }

    private static bool TryParseBssPayload(ReadOnlySpan<byte> payload, int expectedIfIndex, ulong expectedWdev, out LinuxNl80211BssInfo? bssInfo)
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
                    if (value.Length == 4) msgIfIndex = MemoryMarshal.Read<int>(value);
                    else return false;
                    break;
                case NL80211_ATTR_WDEV:
                    if (value.Length == 8) wdev = MemoryMarshal.Read<ulong>(value);
                    else return false;
                    break;
                case NL80211_ATTR_GENERATION:
                    if (value.Length == 4) generation = MemoryMarshal.Read<uint>(value);
                    else return false;
                    break;
                case NL80211_ATTR_BSS:
                    bssAttrBytes = value;
                    break;
            }
        }

        // Exact attribution requirements
        if (!msgIfIndex.HasValue || msgIfIndex.Value != expectedIfIndex)
        {
            return false; // Mandatory IFINDEX missing or mismatched
        }

        if (!wdev.HasValue || wdev.Value != expectedWdev)
        {
            return false; // Mandatory WDEV missing or mismatched
        }

        if (!generation.HasValue)
        {
            return false; // Mandatory GENERATION missing
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
                    else return false; // Exact 6 bytes
                    break;
                case NL80211_BSS_FREQUENCY:
                    if (bval.Length == 4) freq = MemoryMarshal.Read<uint>(bval);
                    else return false; // Exact 4 bytes
                    break;
                case NL80211_BSS_STATUS:
                    if (bval.Length == 4) status = MemoryMarshal.Read<uint>(bval);
                    else return false; // Exact 4 bytes
                    break;
                case NL80211_BSS_SIGNAL_MBM:
                    if (bval.Length == 4) signalMbm = MemoryMarshal.Read<int>(bval); // signed s32
                    else return false; // Exact 4 bytes
                    break;
                case NL80211_BSS_SIGNAL_UNSPEC:
                    if (bval.Length == 1) signalUnspec = bval[0];
                    else return false; // Exact 1 byte
                    break;
                case NL80211_BSS_SEEN_MS_AGO:
                    if (bval.Length == 4) seenMsAgo = MemoryMarshal.Read<uint>(bval);
                    else return false; // Exact 4 bytes
                    break;
                case NL80211_BSS_MLO_LINK_ID:
                    if (bval.Length == 1) mloLinkId = bval[0];
                    else return false; // Exact 1 byte
                    break;
                case NL80211_BSS_MLD_ADDR:
                    if (bval.Length == 6) mldAddr = bval;
                    else return false; // Exact 6 bytes
                    break;
                case NL80211_BSS_LAST_SEEN_BOOTTIME:
                    if (bval.Length == 8) lastSeenBoottime = MemoryMarshal.Read<ulong>(bval);
                    else return false; // Exact 8 bytes
                    break;
                case NL80211_BSS_INFORMATION_ELEMENTS:
                    ies = bval;
                    break;
            }
        }

        if (bssid == null || !freq.HasValue)
        {
            return false; // BSSID and FREQUENCY are mandatory
        }

        string bssidStr = FormatMacAddress(bssid);
        string? mldStr = mldAddr != null ? FormatMacAddress(mldAddr) : null;

        var (ssidBytes, displaySsid, _) = ies != null
            ? ExtractSsidFromInformationElements(ies)
            : (null, null, true);

        bssInfo = new LinuxNl80211BssInfo(
            IfIndex: msgIfIndex.Value,
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

    public static string FormatMacAddress(byte[] mac)
    {
        if (mac == null || mac.Length != 6) return string.Empty;
        return $"{mac[0]:X2}:{mac[1]:X2}:{mac[2]:X2}:{mac[3]:X2}:{mac[4]:X2}:{mac[5]:X2}";
    }

    public static bool TryParseMacAddress(string? macString, out byte[]? macBytes)
    {
        macBytes = null;
        if (string.IsNullOrWhiteSpace(macString)) return false;

        var separator = macString.Contains(':') ? ':' : (macString.Contains('-') ? '-' : '\0');
        if (separator == '\0') return false;

        var parts = macString.Split(separator);
        if (parts.Length != 6) return false;

        var bytes = new byte[6];
        for (int i = 0; i < 6; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                return false;
            }
        }

        macBytes = bytes;
        return true;
    }
}
