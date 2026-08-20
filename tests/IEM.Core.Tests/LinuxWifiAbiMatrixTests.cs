using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Probes;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Phase 3.1-7B-6: Deterministic ABI Matrix Tests.
/// Freezes the wire-level contract between IEM and Linux Generic Netlink / nl80211 UAPI ABI.
/// Uses independent hard-coded golden wire fixtures and verifies constants, request encoders,
/// response decoders, structural tolerances, correlation guards, and multipart state machines.
/// </summary>
public sealed class LinuxWifiAbiMatrixTests
{
    #region Helper: Pure Test-Only Hex Loader

    /// <summary>
    /// Parses a formatted hex string (with comments, whitespace, newlines) into raw byte array.
    /// Pure test helper with zero Netlink awareness.
    /// </summary>
    public static byte[] HexToBytes(string hexText)
    {
        if (string.IsNullOrWhiteSpace(hexText))
        {
            return Array.Empty<byte>();
        }

        var sb = new StringBuilder();
        using var reader = new StringReader(hexText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//"))
            {
                continue;
            }

            int commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }
            commentIdx = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }

            foreach (var ch in line)
            {
                if (Uri.IsHexDigit(ch))
                {
                    sb.Append(ch);
                }
            }
        }

        var clean = sb.ToString();
        if (clean.Length % 2 != 0)
        {
            throw new ArgumentException($"Invalid hex string length: {clean.Length}");
        }

        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(clean.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    #endregion

    #region Section A: ABI Constants Matrix

    [Fact]
    public void AbiMatrix_Commands_Match_Linux_Kernel_Uapi()
    {
        Assert.Equal(0, LinuxNl80211Protocol.NL80211_CMD_UNSPEC);
        Assert.Equal(1, LinuxNl80211Protocol.NL80211_CMD_GET_WIPHY);
        Assert.Equal(2, LinuxNl80211Protocol.NL80211_CMD_SET_WIPHY);
        Assert.Equal(3, LinuxNl80211Protocol.NL80211_CMD_NEW_WIPHY);
        Assert.Equal(4, LinuxNl80211Protocol.NL80211_CMD_DEL_WIPHY);
        Assert.Equal(5, LinuxNl80211Protocol.NL80211_CMD_GET_INTERFACE);
        Assert.Equal(6, LinuxNl80211Protocol.NL80211_CMD_SET_INTERFACE);
        Assert.Equal(7, LinuxNl80211Protocol.NL80211_CMD_NEW_INTERFACE);
        Assert.Equal(8, LinuxNl80211Protocol.NL80211_CMD_DEL_INTERFACE);
        Assert.Equal(17, LinuxNl80211Protocol.NL80211_CMD_GET_STATION);
        Assert.Equal(18, LinuxNl80211Protocol.NL80211_CMD_SET_STATION);
        Assert.Equal(19, LinuxNl80211Protocol.NL80211_CMD_NEW_STATION);
        Assert.Equal(20, LinuxNl80211Protocol.NL80211_CMD_DEL_STATION);
        Assert.Equal(32, LinuxNl80211Protocol.NL80211_CMD_GET_SCAN);
        Assert.Equal(33, LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN);
        Assert.Equal(34, LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS);
        Assert.Equal(35, LinuxNl80211Protocol.NL80211_CMD_SCAN_ABORTED);
    }

    [Fact]
    public void AbiMatrix_TopLevel_Attributes_Match_Linux_Kernel_Uapi()
    {
        Assert.Equal(0, LinuxNl80211Protocol.NL80211_ATTR_UNSPEC);
        Assert.Equal(1, LinuxNl80211Protocol.NL80211_ATTR_WIPHY);
        Assert.Equal(2, LinuxNl80211Protocol.NL80211_ATTR_WIPHY_NAME);
        Assert.Equal(3, LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
        Assert.Equal(4, LinuxNl80211Protocol.NL80211_ATTR_IFNAME);
        Assert.Equal(5, LinuxNl80211Protocol.NL80211_ATTR_IFTYPE);
        Assert.Equal(6, LinuxNl80211Protocol.NL80211_ATTR_MAC);
        Assert.Equal(21, LinuxNl80211Protocol.NL80211_ATTR_STA_INFO);
        Assert.Equal(38, LinuxNl80211Protocol.NL80211_ATTR_WIPHY_FREQ);
        Assert.Equal(46, LinuxNl80211Protocol.NL80211_ATTR_GENERATION);
        Assert.Equal(47, LinuxNl80211Protocol.NL80211_ATTR_BSS);
        Assert.Equal(52, LinuxNl80211Protocol.NL80211_ATTR_SSID);
        Assert.Equal(153, LinuxNl80211Protocol.NL80211_ATTR_WDEV);
        Assert.Equal(174, LinuxNl80211Protocol.NL80211_ATTR_SPLIT_WIPHY_DUMP);
        Assert.Equal(312, LinuxNl80211Protocol.NL80211_ATTR_MLO_LINKS);
        Assert.Equal(313, LinuxNl80211Protocol.NL80211_ATTR_MLO_LINK_ID);
        Assert.Equal(314, LinuxNl80211Protocol.NL80211_ATTR_MLD_ADDR);
    }

    [Fact]
    public void AbiMatrix_Bss_Attributes_Match_Linux_Kernel_Uapi()
    {
        Assert.Equal(0, LinuxNl80211Protocol.NL80211_BSS_INVALID);
        Assert.Equal(1, LinuxNl80211Protocol.NL80211_BSS_BSSID);
        Assert.Equal(2, LinuxNl80211Protocol.NL80211_BSS_FREQUENCY);
        Assert.Equal(3, LinuxNl80211Protocol.NL80211_BSS_TSF);
        Assert.Equal(4, LinuxNl80211Protocol.NL80211_BSS_BEACON_INTERVAL);
        Assert.Equal(5, LinuxNl80211Protocol.NL80211_BSS_CAPABILITY);
        Assert.Equal(6, LinuxNl80211Protocol.NL80211_BSS_INFORMATION_ELEMENTS);
        Assert.Equal(7, LinuxNl80211Protocol.NL80211_BSS_SIGNAL_MBM);
        Assert.Equal(8, LinuxNl80211Protocol.NL80211_BSS_SIGNAL_UNSPEC);
        Assert.Equal(9, LinuxNl80211Protocol.NL80211_BSS_STATUS);
        Assert.Equal(10, LinuxNl80211Protocol.NL80211_BSS_SEEN_MS_AGO);
        Assert.Equal(11, LinuxNl80211Protocol.NL80211_BSS_BEACON_IES);
        Assert.Equal(12, LinuxNl80211Protocol.NL80211_BSS_CHAN_WIDTH);
        Assert.Equal(13, LinuxNl80211Protocol.NL80211_BSS_BEACON_TSF);
        Assert.Equal(14, LinuxNl80211Protocol.NL80211_BSS_PRESP_DATA);
        Assert.Equal(15, LinuxNl80211Protocol.NL80211_BSS_LAST_SEEN_BOOTTIME);
        Assert.Equal(16, LinuxNl80211Protocol.NL80211_BSS_PAD);
        Assert.Equal(17, LinuxNl80211Protocol.NL80211_BSS_PARENT_TSF);
        Assert.Equal(18, LinuxNl80211Protocol.NL80211_BSS_PARENT_BSSID);
        Assert.Equal(19, LinuxNl80211Protocol.NL80211_BSS_CHAIN_SIGNAL);
        Assert.Equal(20, LinuxNl80211Protocol.NL80211_BSS_FREQUENCY_OFFSET);
        Assert.Equal(21, LinuxNl80211Protocol.NL80211_BSS_MLO_LINK_ID);
        Assert.Equal(22, LinuxNl80211Protocol.NL80211_BSS_MLD_ADDR);
        Assert.Equal(23, LinuxNl80211Protocol.NL80211_BSS_USE_FOR);
        Assert.Equal(24, LinuxNl80211Protocol.NL80211_BSS_CANNOT_USE_REASONS);
    }

    [Fact]
    public void AbiMatrix_Station_And_Rate_Attributes_Match_Linux_Kernel_Uapi()
    {
        // Station info
        Assert.Equal(0, LinuxNl80211Protocol.NL80211_STA_INFO_INVALID);
        Assert.Equal(1, LinuxNl80211Protocol.NL80211_STA_INFO_INACTIVE_TIME);
        Assert.Equal(2, LinuxNl80211Protocol.NL80211_STA_INFO_RX_BYTES);
        Assert.Equal(3, LinuxNl80211Protocol.NL80211_STA_INFO_TX_BYTES);
        Assert.Equal(7, LinuxNl80211Protocol.NL80211_STA_INFO_SIGNAL);
        Assert.Equal(8, LinuxNl80211Protocol.NL80211_STA_INFO_TX_BITRATE);
        Assert.Equal(9, LinuxNl80211Protocol.NL80211_STA_INFO_RX_PACKETS);
        Assert.Equal(10, LinuxNl80211Protocol.NL80211_STA_INFO_TX_PACKETS);
        Assert.Equal(11, LinuxNl80211Protocol.NL80211_STA_INFO_TX_RETRIES);
        Assert.Equal(12, LinuxNl80211Protocol.NL80211_STA_INFO_TX_FAILED);
        Assert.Equal(13, LinuxNl80211Protocol.NL80211_STA_INFO_SIGNAL_AVG);
        Assert.Equal(14, LinuxNl80211Protocol.NL80211_STA_INFO_RX_BITRATE);
        Assert.Equal(16, LinuxNl80211Protocol.NL80211_STA_INFO_CONNECTED_TIME);
        Assert.Equal(23, LinuxNl80211Protocol.NL80211_STA_INFO_RX_BYTES64);
        Assert.Equal(24, LinuxNl80211Protocol.NL80211_STA_INFO_TX_BYTES64);
        Assert.Equal(27, LinuxNl80211Protocol.NL80211_STA_INFO_EXPECTED_THROUGHPUT);
        Assert.Equal(29, LinuxNl80211Protocol.NL80211_STA_INFO_BEACON_RX);
        Assert.Equal(30, LinuxNl80211Protocol.NL80211_STA_INFO_BEACON_SIGNAL_AVG);
        Assert.Equal(32, LinuxNl80211Protocol.NL80211_STA_INFO_RX_DURATION);
        Assert.Equal(34, LinuxNl80211Protocol.NL80211_STA_INFO_ACK_SIGNAL);
        Assert.Equal(35, LinuxNl80211Protocol.NL80211_STA_INFO_ACK_SIGNAL_AVG);
        Assert.Equal(39, LinuxNl80211Protocol.NL80211_STA_INFO_TX_DURATION);
        Assert.Equal(42, LinuxNl80211Protocol.NL80211_STA_INFO_ASSOC_AT_BOOTTIME);

        // Rate info
        Assert.Equal(0, LinuxNl80211Protocol.NL80211_RATE_INFO_INVALID);
        Assert.Equal(1, LinuxNl80211Protocol.NL80211_RATE_INFO_BITRATE);
        Assert.Equal(2, LinuxNl80211Protocol.NL80211_RATE_INFO_MCS);
        Assert.Equal(3, LinuxNl80211Protocol.NL80211_RATE_INFO_40_MHZ_WIDTH);
        Assert.Equal(4, LinuxNl80211Protocol.NL80211_RATE_INFO_SHORT_GI);
        Assert.Equal(5, LinuxNl80211Protocol.NL80211_RATE_INFO_BITRATE32);
        Assert.Equal(6, LinuxNl80211Protocol.NL80211_RATE_INFO_VHT_MCS);
        Assert.Equal(7, LinuxNl80211Protocol.NL80211_RATE_INFO_VHT_NSS);
        Assert.Equal(8, LinuxNl80211Protocol.NL80211_RATE_INFO_80_MHZ_WIDTH);
        Assert.Equal(9, LinuxNl80211Protocol.NL80211_RATE_INFO_80P80_MHZ_WIDTH);
        Assert.Equal(10, LinuxNl80211Protocol.NL80211_RATE_INFO_160_MHZ_WIDTH);
        Assert.Equal(11, LinuxNl80211Protocol.NL80211_RATE_INFO_10_MHZ_WIDTH);
        Assert.Equal(12, LinuxNl80211Protocol.NL80211_RATE_INFO_5_MHZ_WIDTH);
        Assert.Equal(13, LinuxNl80211Protocol.NL80211_RATE_INFO_HE_MCS);
        Assert.Equal(14, LinuxNl80211Protocol.NL80211_RATE_INFO_HE_NSS);
        Assert.Equal(15, LinuxNl80211Protocol.NL80211_RATE_INFO_HE_GI);
        Assert.Equal(16, LinuxNl80211Protocol.NL80211_RATE_INFO_HE_DCM);
        Assert.Equal(17, LinuxNl80211Protocol.NL80211_RATE_INFO_HE_RU_ALLOC);
        Assert.Equal(18, LinuxNl80211Protocol.NL80211_RATE_INFO_320_MHZ_WIDTH);
        Assert.Equal(19, LinuxNl80211Protocol.NL80211_RATE_INFO_EHT_MCS);
        Assert.Equal(20, LinuxNl80211Protocol.NL80211_RATE_INFO_EHT_NSS);
        Assert.Equal(21, LinuxNl80211Protocol.NL80211_RATE_INFO_EHT_GI);
        Assert.Equal(22, LinuxNl80211Protocol.NL80211_RATE_INFO_EHT_RU_ALLOC);
    }

    [Fact]
    public void AbiMatrix_Generic_Netlink_Constants_Match_Linux_Kernel_Uapi()
    {
        Assert.Equal(16, LinuxGenlProtocol.NETLINK_GENERIC);
        Assert.Equal(16, LinuxGenlProtocol.GENL_ID_CTRL);
        Assert.Equal(16, LinuxGenlProtocol.NlmsgHeaderSize);
        Assert.Equal(4, LinuxGenlProtocol.GenlHeaderSize);
        Assert.Equal(4, LinuxGenlProtocol.NlaHeaderSize);

        Assert.Equal(1, LinuxGenlProtocol.NLMSG_NOOP);
        Assert.Equal(2, LinuxGenlProtocol.NLMSG_ERROR);
        Assert.Equal(3, LinuxGenlProtocol.NLMSG_DONE);
        Assert.Equal(4, LinuxGenlProtocol.NLMSG_OVERRUN);

        Assert.Equal(0x01, LinuxGenlProtocol.NLM_F_REQUEST);
        Assert.Equal(0x02, LinuxGenlProtocol.NLM_F_MULTI);
        Assert.Equal(0x04, LinuxGenlProtocol.NLM_F_ACK);
        Assert.Equal(0x08, LinuxGenlProtocol.NLM_F_ECHO);
        Assert.Equal(0x10, LinuxGenlProtocol.NLM_F_DUMP_INTR);
        Assert.Equal(0x20, LinuxGenlProtocol.NLM_F_DUMP_FILTERED);
        Assert.Equal(0x100, LinuxGenlProtocol.NLM_F_ROOT);
        Assert.Equal(0x200, LinuxGenlProtocol.NLM_F_MATCH);
        Assert.Equal(0x400, LinuxGenlProtocol.NLM_F_ATOMIC);
        Assert.Equal(0x300, LinuxGenlProtocol.NLM_F_DUMP);

        Assert.Equal(3, LinuxGenlProtocol.CTRL_CMD_GETFAMILY);
        Assert.Equal(1, LinuxGenlProtocol.CTRL_CMD_NEWFAMILY);
    }

    #endregion

    #region Section B: Exact Request Golden-Wire Matrix

    [Fact]
    public void AbiMatrix_BuildGetFamilyRequest_Matches_Golden_Wire_Bytes()
    {
        // nlmsghdr (16 bytes): len=32, type=16 (GENL_ID_CTRL), flags=NLM_F_REQUEST|NLM_F_ACK (0x0005), seq=100 (0x64), pid=0
        // genlmsghdr (4 bytes): cmd=3 (CTRL_CMD_GETFAMILY), version=1, reserved=0
        // nlattr (12 bytes): len=12, type=2 (CTRL_ATTR_FAMILY_NAME), payload="nl80211\0" (8 bytes including NUL)
        const string goldenHex = @"
            20 00 00 00   # nlmsg_len = 32 (0x20)
            10 00         # nlmsg_type = 16 (GENL_ID_CTRL)
            05 00         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK (0x0005)
            64 00 00 00   # nlmsg_seq = 100 (0x64)
            00 00 00 00   # nlmsg_pid = 0
            03 01 00 00   # genl: cmd=3 (CTRL_CMD_GETFAMILY), version=1, res=0
            0c 00 02 00   # nla: len=12 (0x0C), type=2 (CTRL_ATTR_FAMILY_NAME)
            6e 6c 38 30 32 31 31 00 # 'n','l','8','0','2','1','1','\0'
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxGenlProtocol.BuildGetFamilyRequest("nl80211", 100, 0);

        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public void AbiMatrix_BuildGetInterfaceRequest_Single_Matches_Golden_Wire_Bytes()
    {
        // family=28 (0x001C), ifindex=3, seq=101 (0x65), pid=0
        // nlmsghdr (16 bytes): len=28 (0x1C), type=28, flags=NLM_F_REQUEST|NLM_F_ACK (0x0005), seq=101, pid=0
        // genlmsghdr (4 bytes): cmd=5 (NL80211_CMD_GET_INTERFACE), version=1, reserved=0
        // nlattr (8 bytes): len=8, type=3 (NL80211_ATTR_IFINDEX), payload=3 (uint32)
        const string goldenHex = @"
            1c 00 00 00   # nlmsg_len = 28
            1c 00         # nlmsg_type = 28 (nl80211 family)
            05 00         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK (0x0005)
            65 00 00 00   # nlmsg_seq = 101
            00 00 00 00   # nlmsg_pid = 0
            05 01 00 00   # genl: cmd=5 (GET_INTERFACE), version=1, res=0
            08 00 03 00   # nla: len=8, type=3 (NL80211_ATTR_IFINDEX)
            03 00 00 00   # ifindex = 3
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxNl80211Protocol.BuildGetInterfaceRequest(28, 3, 101, 0);

        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public void AbiMatrix_BuildGetInterfaceRequest_Dump_Matches_Golden_Wire_Bytes()
    {
        // family=28, ifindex=null (dump), seq=102 (0x66)
        // nlmsghdr (16 bytes): len=20, type=28, flags=NLM_F_REQUEST|NLM_F_DUMP|NLM_F_ACK (0x0305), seq=102, pid=0
        // genlmsghdr (4 bytes): cmd=5 (NL80211_CMD_GET_INTERFACE), version=1, reserved=0
        const string goldenHex = @"
            14 00 00 00   # nlmsg_len = 20
            1c 00         # nlmsg_type = 28
            05 03         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK | NLM_F_DUMP (0x0305)
            66 00 00 00   # nlmsg_seq = 102
            00 00 00 00   # nlmsg_pid = 0
            05 01 00 00   # genl: cmd=5 (GET_INTERFACE), version=1, res=0
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxNl80211Protocol.BuildGetInterfaceRequest(28, null, 102, 0);

        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public void AbiMatrix_BuildGetWiphyRequest_Single_Matches_Golden_Wire_Bytes()
    {
        // family=28, wiphy=0, seq=103 (0x67)
        // nlmsghdr (16 bytes): len=28, type=28, flags=NLM_F_REQUEST|NLM_F_ACK (0x0005), seq=103, pid=0
        // genlmsghdr (4 bytes): cmd=1 (NL80211_CMD_GET_WIPHY), version=1, reserved=0
        // nlattr (8 bytes): len=8, type=1 (NL80211_ATTR_WIPHY), payload=0
        const string goldenHex = @"
            1c 00 00 00   # nlmsg_len = 28
            1c 00         # nlmsg_type = 28
            05 00         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK (0x0005)
            67 00 00 00   # nlmsg_seq = 103
            00 00 00 00   # nlmsg_pid = 0
            01 01 00 00   # genl: cmd=1 (GET_WIPHY), version=1, res=0
            08 00 01 00   # nla: len=8, type=1 (NL80211_ATTR_WIPHY)
            00 00 00 00   # wiphy = 0
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxNl80211Protocol.BuildGetWiphyRequest(28, 0, 103, 0);

        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public void AbiMatrix_BuildGetWiphyRequest_Dump_Matches_Golden_Wire_Bytes_With_SplitWiphyDump()
    {
        // family=28, wiphy=null (dump), seq=104 (0x68)
        // nlmsghdr (16 bytes): len=24, type=28, flags=NLM_F_REQUEST|NLM_F_DUMP|NLM_F_ACK (0x0305), seq=104, pid=0
        // genlmsghdr (4 bytes): cmd=1 (NL80211_CMD_GET_WIPHY), version=1, reserved=0
        // nlattr (4 bytes): len=4, type=174 (NL80211_ATTR_SPLIT_WIPHY_DUMP), payload empty
        const string goldenHex = @"
            18 00 00 00   # nlmsg_len = 24
            1c 00         # nlmsg_type = 28
            05 03         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK | NLM_F_DUMP (0x0305)
            68 00 00 00   # nlmsg_seq = 104
            00 00 00 00   # nlmsg_pid = 0
            01 01 00 00   # genl: cmd=1 (GET_WIPHY), version=1, res=0
            04 00 ae 00   # nla: len=4, type=174 (0x00AE = NL80211_ATTR_SPLIT_WIPHY_DUMP)
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxNl80211Protocol.BuildGetWiphyRequest(28, null, 104, 0);

        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public void AbiMatrix_BuildGetScanRequest_Matches_Golden_Wire_Bytes_And_Prohibits_TriggerScan()
    {
        // family=28, ifindex=3, seq=105 (0x69)
        // nlmsghdr (16 bytes): len=28, type=28, flags=NLM_F_REQUEST|NLM_F_DUMP|NLM_F_ACK (0x0305), seq=105, pid=0
        // genlmsghdr (4 bytes): cmd=32 (NL80211_CMD_GET_SCAN = 0x20), version=1, reserved=0
        // nlattr (8 bytes): len=8, type=3 (NL80211_ATTR_IFINDEX), payload=3
        const string goldenHex = @"
            1c 00 00 00   # nlmsg_len = 28
            1c 00         # nlmsg_type = 28
            05 03         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK | NLM_F_DUMP (0x0305)
            69 00 00 00   # nlmsg_seq = 105
            00 00 00 00   # nlmsg_pid = 0
            20 01 00 00   # genl: cmd=32 (0x20 = GET_SCAN), version=1, res=0
            08 00 03 00   # nla: len=8, type=3 (NL80211_ATTR_IFINDEX)
            03 00 00 00   # ifindex = 3
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxNl80211Protocol.BuildGetScanRequest(28, 3, 105, 0);

        Assert.Equal(expectedBytes, actualBytes);
        Assert.NotEqual((byte)LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN, actualBytes[16]);
    }

    [Fact]
    public void AbiMatrix_BuildGetStationRequest_Matches_Golden_Wire_Bytes()
    {
        // family=28, ifindex=3, peerMac=[00,11,22,33,44,55], seq=106 (0x6A)
        // nlmsghdr (16 bytes): len=40 (0x28), type=28, flags=NLM_F_REQUEST|NLM_F_ACK (0x0005), seq=106, pid=0
        // genlmsghdr (4 bytes): cmd=17 (NL80211_CMD_GET_STATION = 0x11), version=1, reserved=0
        // nla1 (8 bytes): len=8, type=3 (NL80211_ATTR_IFINDEX), payload=3
        // nla2 (12 bytes): len=10 (0x0A), type=6 (NL80211_ATTR_MAC), payload=[00,11,22,33,44,55] + 2 bytes padding -> aligned to 12 bytes
        const string goldenHex = @"
            28 00 00 00   # nlmsg_len = 40 (0x28)
            1c 00         # nlmsg_type = 28
            05 00         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK (0x0005)
            6a 00 00 00   # nlmsg_seq = 106
            00 00 00 00   # nlmsg_pid = 0
            11 01 00 00   # genl: cmd=17 (0x11 = GET_STATION), version=1, res=0
            08 00 03 00   # nla: len=8, type=3 (NL80211_ATTR_IFINDEX)
            03 00 00 00   # ifindex = 3
            0a 00 06 00   # nla: len=10 (0x0A), type=6 (NL80211_ATTR_MAC)
            00 11 22 33 44 55 00 00 # MAC=00:11:22:33:44:55 + 2 bytes pad
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxNl80211Protocol.BuildGetStationRequest(28, 3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, 106, 0);

        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public void AbiMatrix_BuildGetStationRequest_Mld_Peer_Matches_Golden_Wire_Bytes()
    {
        // family=28, ifindex=3, peerMac=[AA,BB,CC,DD,EE,00], seq=107 (0x6B)
        const string goldenHex = @"
            28 00 00 00   # nlmsg_len = 40 (0x28)
            1c 00         # nlmsg_type = 28
            05 00         # nlmsg_flags = NLM_F_REQUEST | NLM_F_ACK (0x0005)
            6b 00 00 00   # nlmsg_seq = 107
            00 00 00 00   # nlmsg_pid = 0
            11 01 00 00   # genl: cmd=17 (0x11 = GET_STATION), version=1, res=0
            08 00 03 00   # nla: len=8, type=3 (NL80211_ATTR_IFINDEX)
            03 00 00 00   # ifindex = 3
            0a 00 06 00   # nla: len=10 (0x0A), type=6 (NL80211_ATTR_MAC)
            aa bb cc dd ee 00 00 00 # MAC=AA:BB:CC:DD:EE:00 + 2 bytes pad
        ";

        var expectedBytes = HexToBytes(goldenHex);
        var actualBytes = LinuxNl80211Protocol.BuildGetStationRequest(28, 3, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x00 }, 107, 0);

        Assert.Equal(expectedBytes, actualBytes);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    public void AbiMatrix_Mac_Parser_Textual_Variants_Produce_Identical_Raw_Bytes(string macText)
    {
        bool ok = LinuxNl80211Protocol.TryParseMacAddress(macText, out var macBytes);
        Assert.True(ok);
        Assert.NotNull(macBytes);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, macBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:FF:00")]
    [InlineData("AA:BB:CC:DD:EE:GG")]
    [InlineData("AABBCCDDEEFF")]
    public void AbiMatrix_Mac_Parser_Malformed_Rejected(string? invalidMac)
    {
        bool ok = LinuxNl80211Protocol.TryParseMacAddress(invalidMac, out var macBytes);
        Assert.False(ok);
        Assert.Null(macBytes);
    }

    #endregion

    #region Section C: nlctrl / Family Discovery Response Matrix

    [Fact]
    public void AbiMatrix_Nlctrl_Valid_NewFamily_Parsed_Correctly()
    {
        // Valid response to GETFAMILY("nl80211"), seq=100
        const string responseHex = @"
            30 00 00 00   # nlmsg_len = 48 (0x30)
            10 00         # nlmsg_type = 16 (GENL_ID_CTRL)
            00 00         # nlmsg_flags = 0
            64 00 00 00   # nlmsg_seq = 100
            d2 04 00 00   # nlmsg_pid = 1234
            01 01 00 00   # genl: cmd=1 (NEWFAMILY), version=1, res=0
            06 00 01 00 1c 00 00 00 # nla: len=6, type=1 (FAMILY_ID), val=28 + pad
            0c 00 02 00 6e 6c 38 30 32 31 31 00 # nla: len=12, type=2 (FAMILY_NAME), 'nl80211\0'
            08 00 03 00 01 00 00 00 # nla: len=8, type=3 (VERSION), val=1
            08 00 04 00 00 00 00 00 # nla: len=8, type=4 (HDRSIZE), val=0
        ";

        var responseBytes = HexToBytes(responseHex);
        int err = LinuxGenlProtocol.ParseGetFamilyResponse(responseBytes, 100, "nl80211", out var family);

        Assert.Equal(0, err);
        Assert.NotNull(family);
        Assert.Equal((ushort)28, family.FamilyId);
        Assert.Equal("nl80211", family.FamilyName);
        Assert.Equal(1u, family.Version);
    }

    [Fact]
    public void AbiMatrix_Nlctrl_Unrelated_Sequence_Ignored_Valid_Accepted()
    {
        // Message 1: seq=999 (unrelated) -> len = 16 (nlmsghdr) + 4 (genlmsghdr) + 8 (ID) + 8 (NAME) = 36 (0x24)
        // Message 2: seq=100 (matching)  -> len = 48 (0x30)
        const string multiMsgHex = @"
            24 00 00 00 10 00 00 00 e7 03 00 00 00 00 00 00 # len=36, type=16, seq=999
            01 01 00 00 06 00 01 00 10 00 00 00 08 00 02 00 66 6f 6f 00 # family foo
            30 00 00 00 10 00 00 00 64 00 00 00 00 00 00 00 # len=48, type=16, seq=100
            01 01 00 00
            06 00 01 00 1c 00 00 00
            0c 00 02 00 6e 6c 38 30 32 31 31 00
            08 00 03 00 01 00 00 00
            08 00 04 00 00 00 00 00
        ";

        var bytes = HexToBytes(multiMsgHex);
        int err = LinuxGenlProtocol.ParseGetFamilyResponse(bytes, 100, "nl80211", out var family);

        Assert.Equal(0, err);
        Assert.NotNull(family);
        Assert.Equal((ushort)28, family.FamilyId);
    }

    [Fact]
    public void AbiMatrix_Nlctrl_Wrong_Family_Name_Rejected()
    {
        const string responseHex = @"
            30 00 00 00 10 00 00 00 64 00 00 00 00 00 00 00
            01 01 00 00
            06 00 01 00 1c 00 00 00
            0c 00 02 00 6f 74 68 65 72 30 30 00 # 'other00\0'
            08 00 03 00 01 00 00 00
            08 00 04 00 00 00 00 00
        ";

        var bytes = HexToBytes(responseHex);
        int err = LinuxGenlProtocol.ParseGetFamilyResponse(bytes, 100, "nl80211", out var family);

        Assert.Equal(-2, err); // ENOENT
        Assert.Null(family);
    }

    [Fact]
    public void AbiMatrix_Nlctrl_NlmsgError_Enoent_Returns_Null()
    {
        // NLMSG_ERROR with errno=-2 (-ENOENT = 0xFFFFFFFE)
        const string errorHex = @"
            24 00 00 00   # nlmsg_len = 36
            02 00         # nlmsg_type = 2 (NLMSG_ERROR)
            00 00         # nlmsg_flags = 0
            64 00 00 00   # nlmsg_seq = 100
            00 00 00 00   # nlmsg_pid = 0
            fe ff ff ff   # error = -2 (-ENOENT)
            20 00 00 00 10 00 05 00 64 00 00 00 00 00 00 00 # original header
        ";

        var bytes = HexToBytes(errorHex);
        int err = LinuxGenlProtocol.ParseGetFamilyResponse(bytes, 100, "nl80211", out var family);

        Assert.Equal(-2, err);
        Assert.Null(family);
    }

    [Fact]
    public void AbiMatrix_Nlctrl_Ack_Before_Payload_Succeeds()
    {
        // Packet 1: NLMSG_ERROR with error=0 (ACK), seq=100
        // Packet 2: NEWFAMILY payload, seq=100
        const string ackAndPayloadHex = @"
            24 00 00 00 02 00 00 00 64 00 00 00 00 00 00 00 # ACK (error=0)
            00 00 00 00
            20 00 00 00 10 00 05 00 64 00 00 00 00 00 00 00
            30 00 00 00 10 00 00 00 64 00 00 00 00 00 00 00 # Payload
            01 01 00 00
            06 00 01 00 1c 00 00 00
            0c 00 02 00 6e 6c 38 30 32 31 31 00
            08 00 03 00 01 00 00 00
            08 00 04 00 00 00 00 00
        ";

        var bytes = HexToBytes(ackAndPayloadHex);
        int err = LinuxGenlProtocol.ParseGetFamilyResponse(bytes, 100, "nl80211", out var family);

        Assert.Equal(0, err);
        Assert.NotNull(family);
        Assert.Equal((ushort)28, family.FamilyId);
    }

    [Theory]
    [InlineData("08 00 00 00 10 00 00 00")] // truncated nlmsghdr (< 16 bytes)
    [InlineData("12 00 00 00 10 00 00 00 64 00 00 00 00 00 00 00 01 01")] // truncated genlmsghdr (< 20 bytes)
    public void AbiMatrix_Nlctrl_Truncated_Header_Rejected(string truncatedHex)
    {
        var bytes = HexToBytes(truncatedHex);
        int err = LinuxGenlProtocol.ParseGetFamilyResponse(bytes, 100, "nl80211", out var family);
        Assert.Equal(-22, err); // EINVAL
        Assert.Null(family);
    }

    #endregion

    #region Section D: NLA Structural / Generic Netlink Framing Matrix

    [Fact]
    public void AbiMatrix_Nla_Len_Less_Than_4_Rejected_In_Strict_Mode()
    {
        byte[] payload = new byte[] { 0x02, 0x00, 0x01, 0x00 };
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AbiMatrix_Nla_Len_Zero_Rejected_In_Strict_Mode()
    {
        byte[] payload = new byte[] { 0x00, 0x00, 0x01, 0x00 };
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AbiMatrix_Nla_Length_Runs_Past_Payload_Rejected()
    {
        byte[] payload = new byte[] { 0x0A, 0x00, 0x01, 0x00, 0xAA, 0xBB };
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AbiMatrix_Nla_Aligned_Length_Runs_Past_Payload_Rejected()
    {
        byte[] payload = new byte[] { 0x05, 0x00, 0x01, 0x00, 0xAA, 0xBB };
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out _);
        Assert.False(ok);
    }

    [Fact]
    public void AbiMatrix_Nla_Padding_Bytes_Correctly_Skipped_And_Aligned()
    {
        const string nlaHex = @"
            05 00 01 00 41 00 00 00   # len=5, type=1, 'A', 3 pad
            06 00 02 00 42 43 00 00   # len=6, type=2, 'BC', 2 pad
            07 00 03 00 44 45 46 00   # len=7, type=3, 'DEF', 1 pad
        ";

        var payload = HexToBytes(nlaHex);
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var attrs);

        Assert.True(ok);
        Assert.Equal(3, attrs.Count);
        Assert.Equal(1, attrs[0].Type);
        Assert.Equal(new byte[] { 0x41 }, attrs[0].Value);
        Assert.Equal(2, attrs[1].Type);
        Assert.Equal(new byte[] { 0x42, 0x43 }, attrs[1].Value);
        Assert.Equal(3, attrs[2].Type);
        Assert.Equal(new byte[] { 0x44, 0x45, 0x46 }, attrs[2].Value);
    }

    [Fact]
    public void AbiMatrix_Nla_NonZero_Padding_Bytes_Do_Not_Affect_Attribute_Values()
    {
        const string nlaHex = @"
            05 00 01 00 41 ee ff aa
        ";

        var payload = HexToBytes(nlaHex);
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var attrs);

        Assert.True(ok);
        Assert.Single(attrs);
        Assert.Equal(new byte[] { 0x41 }, attrs[0].Value);
    }

    [Fact]
    public void AbiMatrix_Nla_Flags_Nested_And_NetByteOrder_Masked_Correctly()
    {
        const string nlaHex = @"
            08 00 03 80 01 02 03 04   # type=0x8003 (NESTED | 3)
            08 00 04 40 05 06 07 08   # type=0x4004 (NET_BYTEORDER | 4)
        ";

        var payload = HexToBytes(nlaHex);
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var attrs);

        Assert.True(ok);
        Assert.Equal(2, attrs.Count);
        Assert.Equal(3, attrs[0].Type);
        Assert.Equal(4, attrs[1].Type);
    }

    [Fact]
    public void AbiMatrix_Nla_Trailing_Garbage_Rejected_In_Strict_Mode()
    {
        const string nlaHex = @"
            08 00 01 00 01 02 03 04  ff ff
        ";

        var payload = HexToBytes(nlaHex);
        bool ok = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out _);

        Assert.False(ok);
    }

    #endregion

    #region Section E: GET_INTERFACE Response Matrix

    [Fact]
    public void AbiMatrix_GetInterface_Valid_Single_Response_Parsed_Correctly()
    {
        // Total len = 16 (nlmsghdr) + 4 (genlmsghdr) + 8 (IFINDEX) + 12 (IFNAME) + 8 (WIPHY) + 12 (WDEV) + 8 (IFTYPE) + 12 (MAC) = 80 bytes (0x50)
        const string ifaceHex = @"
            50 00 00 00   # nlmsg_len = 80 (0x50)
            1c 00         # nlmsg_type = 28
            00 00         # nlmsg_flags = 0
            65 00 00 00   # nlmsg_seq = 101
            00 00 00 00   # nlmsg_pid = 0
            07 01 00 00   # genl: cmd=7 (NL80211_CMD_NEW_INTERFACE), ver=1, res=0
            08 00 03 00 03 00 00 00 # IFINDEX = 3
            0a 00 04 00 77 6c 61 6e 30 00 00 00 # IFNAME = 'wlan0\0' (len=10, 2 pad -> 12 bytes)
            08 00 01 00 00 00 00 00 # WIPHY = 0
            0c 00 99 00 00 10 00 00 00 00 00 00 # WDEV = 0x1000UL (12 bytes)
            08 00 05 00 02 00 00 00 # IFTYPE = 2 (STATION)
            0a 00 06 00 00 11 22 33 44 55 00 00 # MAC = 00:11:22:33:44:55 (len=10, 2 pad -> 12 bytes)
        ";

        var responseBytes = HexToBytes(ifaceHex);
        var result = LinuxNl80211Protocol.ParseInterfaceDump(responseBytes, 101, false);

        Assert.True(result.IsComplete);
        Assert.Single(result.Items);
        var iface = result.Items[0];
        Assert.Equal(3, iface.IfIndex);
        Assert.Equal("wlan0", iface.IfName);
        Assert.Equal(0u, iface.WiphyIndex);
        Assert.Equal(0x1000UL, iface.Wdev);
        Assert.Equal(LinuxNl80211Protocol.NL80211_IFTYPE_STATION, iface.IfType);
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, iface.MacAddress);
    }

    [Fact]
    public void AbiMatrix_GetInterface_MultiMessage_Dump_With_Done_Parsed_Correctly()
    {
        const string dumpHex = @"
            50 00 00 00 1c 00 02 00 66 00 00 00 00 00 00 00 # Msg 1: len=80 (0x50), MULTI
            07 01 00 00
            08 00 03 00 03 00 00 00
            0a 00 04 00 77 6c 61 6e 30 00 00 00
            08 00 01 00 00 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 05 00 02 00 00 00
            0a 00 06 00 00 11 22 33 44 55 00 00
            50 00 00 00 1c 00 02 00 66 00 00 00 00 00 00 00 # Msg 2: len=80 (0x50), MULTI
            07 01 00 00
            08 00 03 00 04 00 00 00
            0a 00 04 00 77 6c 61 6e 31 00 00 00
            08 00 01 00 01 00 00 00
            0c 00 99 00 00 20 00 00 00 00 00 00
            08 00 05 00 02 00 00 00
            0a 00 06 00 00 11 22 33 44 66 00 00
            14 00 00 00 03 00 02 00 66 00 00 00 00 00 00 00 00 00 00 00 # Msg 3: NLMSG_DONE (type=3, len=20)
        ";

        var bytes = HexToBytes(dumpHex);
        var result = LinuxNl80211Protocol.ParseInterfaceDump(bytes, 102, true);

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(3, result.Items[0].IfIndex);
        Assert.Equal(4, result.Items[1].IfIndex);
    }

    [Fact]
    public void AbiMatrix_GetInterface_Unknown_Future_Attribute_Ignored_Forward_Compatibly()
    {
        // len = 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (IFNAME) + 8 (IFTYPE) + 8 (ATTR 999) + 12 (MAC) = 68 bytes (0x44)
        const string ifaceHex = @"
            44 00 00 00 1c 00 00 00 65 00 00 00 00 00 00 00
            07 01 00 00
            08 00 03 00 03 00 00 00 # IFINDEX
            0a 00 04 00 77 6c 61 6e 30 00 00 00 # IFNAME
            08 00 05 00 02 00 00 00 # IFTYPE
            08 00 e7 03 de ad be ef # UNKNOWN ATTR 999 = 0xDEADBEEF
            0a 00 06 00 00 11 22 33 44 55 00 00 # MAC
        ";

        var responseBytes = HexToBytes(ifaceHex);
        var result = LinuxNl80211Protocol.ParseInterfaceDump(responseBytes, 101, false);

        Assert.True(result.IsComplete);
        Assert.Single(result.Items);
        Assert.Equal(3, result.Items[0].IfIndex);
        Assert.Equal("wlan0", result.Items[0].IfName);
    }

    [Fact]
    public void AbiMatrix_GetInterface_Dump_With_DumpIntr_Yields_Interrupted()
    {
        const string dumpHex = @"
            50 00 00 00 1c 00 02 00 66 00 00 00 00 00 00 00
            07 01 00 00
            08 00 03 00 03 00 00 00
            0a 00 04 00 77 6c 61 6e 30 00 00 00
            08 00 01 00 00 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 05 00 02 00 00 00
            0a 00 06 00 00 11 22 33 44 55 00 00
            14 00 00 00 03 00 12 00 66 00 00 00 00 00 00 00 00 00 00 00 # type=3, flags=MULTI|DUMP_INTR (0x0012)
        ";

        var bytes = HexToBytes(dumpHex);
        var result = LinuxNl80211Protocol.ParseInterfaceDump(bytes, 102, true);

        Assert.Equal(LinuxNl80211DumpStatus.Interrupted, result.Status);
    }

    #endregion

    #region Section F: GET_WIPHY Response Matrix

    [Fact]
    public void AbiMatrix_GetWiphy_Valid_Single_Response_Parsed_Correctly()
    {
        const string wiphyHex = @"
            28 00 00 00   # nlmsg_len = 40 (0x28)
            1c 00         # nlmsg_type = 28
            00 00         # nlmsg_flags = 0
            67 00 00 00   # nlmsg_seq = 103
            00 00 00 00   # nlmsg_pid = 0
            03 01 00 00   # genl: cmd=3 (NL80211_CMD_NEW_WIPHY), ver=1, res=0
            08 00 01 00 00 00 00 00 # WIPHY = 0
            09 00 02 00 70 68 79 30 00 00 00 00 # WIPHY_NAME = 'phy0\0'
        ";

        var bytes = HexToBytes(wiphyHex);
        var result = LinuxNl80211Protocol.ParseWiphyDump(bytes, 103, false);

        Assert.True(result.IsComplete);
        Assert.Single(result.Items);
        Assert.Equal(0u, result.Items[0].WiphyIndex);
        Assert.Equal("phy0", result.Items[0].WiphyName);
    }

    [Fact]
    public void AbiMatrix_GetWiphy_Missing_WiphyName_Rejected()
    {
        const string wiphyHex = @"
            20 00 00 00 1c 00 00 00 67 00 00 00 00 00 00 00
            03 01 00 00
            08 00 01 00 00 00 00 00 # WIPHY index only, no WIPHY_NAME
        ";

        var bytes = HexToBytes(wiphyHex);
        var result = LinuxNl80211Protocol.ParseWiphyDump(bytes, 103, false);

        Assert.False(result.IsComplete);
        Assert.Empty(result.Items);
    }

    #endregion

    #region Section G: BSS / GET_SCAN Response Matrix

    [Fact]
    public void AbiMatrix_Bss_Associated_SingleLink_Parsed_Correctly()
    {
        // Msg 1 (NEW_SCAN_RESULTS): len = 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (WDEV) + 8 (GENERATION) + 64 (BSS) = 112 bytes (0x70)
        // Nested BSS: 12 (BSSID) + 8 (FREQ) + 8 (STATUS) + 8 (SIGNAL_MBM) + 8 (SIGNAL_UNSPEC) + 16 (IEs) = 60 payload + 4 hdr = 64 bytes (0x40 00 2f 80)
        // Msg 2 (NLMSG_DONE): len = 20 bytes (0x14)
        const string bssHex = @"
            70 00 00 00   # nlmsg_len = 112 (0x70)
            1c 00         # nlmsg_type = 28
            02 00         # nlmsg_flags = MULTI (0x0002)
            69 00 00 00   # nlmsg_seq = 105
            00 00 00 00   # nlmsg_pid = 0
            22 01 00 00   # genl: cmd=34 (NL80211_CMD_NEW_SCAN_RESULTS), ver=1, res=0
            08 00 03 00 03 00 00 00 # IFINDEX = 3
            0c 00 99 00 00 10 00 00 00 00 00 00 # WDEV = 0x1000UL (12 bytes)
            08 00 2e 00 64 00 00 00 # GENERATION = 100 (8 bytes)
            40 00 2f 80             # nested BSS (len=64, type=0x802F = NESTED | 47)
               0a 00 01 00 00 11 22 33 44 55 00 00 # BSSID (12 bytes)
               08 00 02 00 3c 14 00 00             # FREQUENCY = 5180 (8 bytes)
               08 00 09 00 01 00 00 00             # STATUS = 1 (ASSOCIATED) (8 bytes)
               08 00 07 00 9c e6 ff ff             # SIGNAL_MBM = -6500 (8 bytes)
               05 00 08 00 55 00 00 00             # SIGNAL_UNSPEC = 85 (8 bytes)
               0e 00 06 00 00 08 48 6f 6d 65 57 69 46 69 00 00 # IEs: tag=0, len=8, 'HomeWiFi' (16 bytes)
            14 00 00 00 03 00 02 00 69 00 00 00 00 00 00 00 00 00 00 00 # NLMSG_DONE
        ";

        var bytes = HexToBytes(bssHex);
        var result = LinuxNl80211Protocol.ParseBssDump(bytes, 105, 28, 3, 0x1000UL);

        Assert.True(result.IsComplete);
        Assert.Single(result.Items);
        var bss = result.Items[0];
        Assert.Equal(3, bss.IfIndex);
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, bss.Bssid);
        Assert.Equal("00:11:22:33:44:55", bss.BssidString);
        Assert.Equal(5180u, bss.FrequencyMhz);
        Assert.Equal(LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, bss.Status);
        Assert.Equal(-6500, bss.SignalMbm);
        Assert.Equal((byte)85, bss.SignalQuality);
        Assert.Equal("HomeWiFi", bss.DisplaySsid);
        Assert.Equal(100u, bss.Generation);
        Assert.Equal(0x1000UL, bss.Wdev);
    }

    [Fact]
    public void AbiMatrix_Bss_ZeroLength_Hidden_Ssid_Parsed_Correctly()
    {
        // Msg 1: len = 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (WDEV) + 8 (GENERATION) + 48 (BSS) = 96 bytes (0x60)
        // Nested BSS: 12 (BSSID) + 8 (FREQ) + 8 (STATUS) + 8 (SIGNAL_MBM) + 8 (IEs len=0) = 44 bytes payload + 4 hdr = 48 bytes (0x30 00 2f 80)
        const string bssHex = @"
            60 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00
            22 01 00 00
            08 00 03 00 03 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 64 00 00 00
            30 00 2f 80
               0a 00 01 00 00 11 22 33 44 55 00 00
               08 00 02 00 3c 14 00 00
               08 00 09 00 01 00 00 00
               08 00 07 00 9c e6 ff ff
               06 00 06 00 00 00 00 00 # IEs: tag=0, len=0 (hidden)
            14 00 00 00 03 00 02 00 69 00 00 00 00 00 00 00 00 00 00 00 # NLMSG_DONE
        ";

        var bytes = HexToBytes(bssHex);
        var result = LinuxNl80211Protocol.ParseBssDump(bytes, 105, 28, 3, 0x1000UL);

        Assert.True(result.IsComplete);
        Assert.Single(result.Items);
        var bss = result.Items[0];
        Assert.NotNull(bss.SsidBytes);
        Assert.Empty(bss.SsidBytes);
        Assert.Null(bss.DisplaySsid);
    }

    [Fact]
    public void AbiMatrix_Bss_Wrong_IfIndex_Rejected_FailClosed()
    {
        const string bssHex = @"
            44 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00
            22 01 00 00
            08 00 03 00 04 00 00 00 # IFINDEX = 4 (mismatch!)
            1c 00 2f 80
               0a 00 01 00 00 11 22 33 44 55 00 00
               08 00 02 00 3c 14 00 00
        ";

        var bytes = HexToBytes(bssHex);
        var result = LinuxNl80211Protocol.ParseBssDump(bytes, 105, 28, 3, 0x1000UL);

        Assert.Equal(LinuxNl80211DumpStatus.Malformed, result.Status);
    }

    [Fact]
    public void AbiMatrix_Bss_Wrong_Wdev_Rejected_FailClosed()
    {
        const string bssHex = @"
            50 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00
            22 01 00 00
            08 00 03 00 03 00 00 00
            0c 00 99 00 00 20 00 00 00 00 00 00 # WDEV = 0x2000 (mismatch!)
            1c 00 2f 80
               0a 00 01 00 00 11 22 33 44 55 00 00
               08 00 02 00 3c 14 00 00
        ";

        var bytes = HexToBytes(bssHex);
        var result = LinuxNl80211Protocol.ParseBssDump(bytes, 105, 28, 3, 0x1000UL);

        Assert.Equal(LinuxNl80211DumpStatus.Malformed, result.Status);
    }

    [Fact]
    public void AbiMatrix_Bss_Generation_Drift_Inside_Dump_Yields_Interrupted()
    {
        // Msg 1: len = 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (WDEV) + 8 (GEN=100) + 24 (BSS) = 72 bytes (0x48)
        // Nested BSS: 12 (BSSID) + 8 (FREQ) = 20 payload + 4 hdr = 24 bytes (0x18 00 2f 80)
        // Msg 2: len = 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (WDEV) + 8 (GEN=101) + 24 (BSS) = 72 bytes (0x48)
        // Msg 3: NLMSG_DONE = 20 bytes (0x14)
        const string dumpHex = @"
            48 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00
            22 01 00 00
            08 00 03 00 03 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 64 00 00 00 # Generation = 100
            18 00 2f 80
               0a 00 01 00 00 11 22 33 44 01 00 00
               08 00 02 00 3c 14 00 00
            48 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00
            22 01 00 00
            08 00 03 00 03 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 65 00 00 00 # Generation = 101 (drift!)
            18 00 2f 80
               0a 00 01 00 00 11 22 33 44 02 00 00
               08 00 02 00 3c 14 00 00
            14 00 00 00 03 00 02 00 69 00 00 00 00 00 00 00 00 00 00 00 # DONE
        ";

        var bytes = HexToBytes(dumpHex);
        var result = LinuxNl80211Protocol.ParseBssDump(bytes, 105, 28, 3, 0x1000UL);

        Assert.Equal(LinuxNl80211DumpStatus.Interrupted, result.Status);
    }

    [Fact]
    public void AbiMatrix_Bss_Attribute_Permutations_Yield_Identical_Result()
    {
        // len = 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (WDEV) + 8 (GEN) + 40 (BSS) = 88 bytes (0x58)
        const string seq1Hex = @"
            58 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00
            22 01 00 00
            08 00 03 00 03 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 64 00 00 00
            28 00 2f 80
               0a 00 01 00 00 11 22 33 44 55 00 00 # BSSID
               08 00 02 00 3c 14 00 00             # FREQ
               08 00 09 00 01 00 00 00             # STATUS
               08 00 07 00 9c e6 ff ff             # SIGNAL_MBM
            14 00 00 00 03 00 02 00 69 00 00 00 00 00 00 00 00 00 00 00 # NLMSG_DONE
        ";

        const string seq2Hex = @"
            58 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00
            22 01 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 64 00 00 00
            08 00 03 00 03 00 00 00
            28 00 2f 80
               08 00 07 00 9c e6 ff ff             # SIGNAL_MBM
               08 00 09 00 01 00 00 00             # STATUS
               08 00 02 00 3c 14 00 00             # FREQ
               0a 00 01 00 00 11 22 33 44 55 00 00 # BSSID
            14 00 00 00 03 00 02 00 69 00 00 00 00 00 00 00 00 00 00 00 # NLMSG_DONE
        ";

        var r1 = LinuxNl80211Protocol.ParseBssDump(HexToBytes(seq1Hex), 105, 28, 3, 0x1000UL);
        var r2 = LinuxNl80211Protocol.ParseBssDump(HexToBytes(seq2Hex), 105, 28, 3, 0x1000UL);

        Assert.True(r1.IsComplete);
        Assert.True(r2.IsComplete);
        Assert.Equal(r1.Items[0].Bssid, r2.Items[0].Bssid);
        Assert.Equal(r1.Items[0].FrequencyMhz, r2.Items[0].FrequencyMhz);
        Assert.Equal(r1.Items[0].Status, r2.Items[0].Status);
        Assert.Equal(r1.Items[0].SignalMbm, r2.Items[0].SignalMbm);
    }

    #endregion

    #region Section H: GET_STATION Response Matrix

    [Fact]
    public void AbiMatrix_GetStation_Numeric_Fidelity_And_Endianness_Verified()
    {
        // STA_INFO payload:
        // SIGNAL: 8
        // SIGNAL_AVG: 8
        // RX_BYTES64: 12
        // TX_BYTES64: 12
        // RX_PACKETS: 8
        // TX_PACKETS: 8
        // CONNECTED_TIME: 8
        // EXPECTED_THROUGHPUT: 8
        // ASSOC_AT_BOOTTIME: 12
        // Total STA_INFO payload = 84 bytes -> nested NLA header (4 bytes) + 84 = 88 bytes (0x58 00 15 80)
        // Top-level: 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (MAC) + 12 (WDEV) + 8 (GENERATION) + 88 (STA_INFO) = 148 bytes (0x94)
        const string stationHex = @"
            94 00 00 00   # nlmsg_len = 148 (0x94)
            1c 00         # nlmsg_type = 28
            00 00         # nlmsg_flags = 0
            6a 00 00 00   # nlmsg_seq = 106
            00 00 00 00   # nlmsg_pid = 0
            13 01 00 00   # genl: cmd=19 (NL80211_CMD_NEW_STATION), ver=1, res=0
            08 00 03 00 03 00 00 00 # IFINDEX = 3
            0a 00 06 00 00 11 22 33 44 55 00 00 # MAC = 00:11:22:33:44:55 (12 bytes)
            0c 00 99 00 00 10 00 00 00 00 00 00 # WDEV = 0x1000UL (12 bytes)
            08 00 2e 00 64 00 00 00             # GENERATION = 100 (8 bytes)
            58 00 15 80                         # STA_INFO (len=88, type=0x8015 = NESTED | 21)
               05 00 07 00 c2 00 00 00          # SIGNAL = -62 (0xC2 = 194 = -62 sbyte)
               05 00 0d 00 c0 00 00 00          # SIGNAL_AVG = -64 (13 = 0x0D, 0xC0 = 192 = -64 sbyte)
               0c 00 17 00 08 07 06 05 04 03 02 01 # RX_BYTES64 (23 = 0x17) = 0x0102030405060708UL
               0c 00 18 00 01 02 03 04 05 06 07 08 # TX_BYTES64 (24 = 0x18) = 0x0807060504030201UL
               08 00 09 00 78 56 34 12          # RX_PACKETS = 0x12345678U
               08 00 0a 00 21 43 65 87          # TX_PACKETS = 0x87654321U
               08 00 10 00 10 0e 00 00          # CONNECTED_TIME (16 = 0x10) = 3600
               08 00 1b 00 f0 d2 00 00          # EXPECTED_THROUGHPUT (27 = 0x1B) = 54000
               0c 00 2a 00 00 e8 76 48 17 00 00 00 # ASSOC_AT_BOOTTIME (42 = 0x2A) = 100_000_000_000UL
        ";

        var bytes = HexToBytes(stationHex);
        var res = LinuxNl80211Protocol.ParseStationResponse(bytes, 106, 28, 3, 0x1000UL, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

        Assert.True(res.IsSuccess);
        var sta = res.Item;
        Assert.NotNull(sta);
        Assert.Equal((sbyte)-62, sta.SignalDbm);
        Assert.Equal((sbyte)-64, sta.SignalAverageDbm);
        Assert.Equal(0x0102030405060708UL, sta.RxBytes);
        Assert.Equal(0x0807060504030201UL, sta.TxBytes);
        Assert.Equal(0x12345678U, sta.RxPackets);
        Assert.Equal(0x87654321U, sta.TxPackets);
        Assert.Equal(3600u, sta.ConnectedTimeSeconds);
        Assert.Equal(54000u, sta.ExpectedThroughputKbps);
        Assert.Equal(100_000_000_000UL, sta.AssociationBootTimeNs);
    }

    [Fact]
    public void AbiMatrix_GetStation_Wrong_Peer_Mac_Rejected()
    {
        const string stationHex = @"
            44 00 00 00 1c 00 00 00 6a 00 00 00 00 00 00 00
            13 01 00 00
            08 00 03 00 03 00 00 00
            0a 00 06 00 00 11 22 33 44 99 00 00 # MAC = 00:11:22:33:44:99 (mismatch!)
            0c 00 99 00 00 10 00 00 00 00 00 00
            14 00 15 80
               05 00 07 00 c2 00 00 00
               05 00 0d 00 c0 00 00 00
        ";

        var bytes = HexToBytes(stationHex);
        var res = LinuxNl80211Protocol.ParseStationResponse(bytes, 106, 28, 3, 0x1000UL, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    #endregion

    #region Section I: Rate-Info ABI Matrix

    [Fact]
    public void AbiMatrix_RateInfo_Bitrate32_Has_Precedence_Over_Bitrate16()
    {
        // Rate info nested inside TX_BITRATE (attr 8) inside STA_INFO (attr 21):
        // TX_BITRATE payload:
        // BITRATE (16-bit): 8
        // BITRATE32 (32-bit): 8
        // MCS: 8
        // 80_MHZ (flag): 4
        // Total TX_BITRATE payload = 28 bytes -> 0x20 00 08 80 (32 bytes)
        // STA_INFO payload = 32 bytes -> 0x24 00 15 80 (36 bytes)
        // Top-level: 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (MAC) + 12 (WDEV) + 8 (GENERATION) + 36 (STA_INFO) = 96 bytes (0x60)
        const string stationHex = @"
            60 00 00 00 1c 00 00 00 6a 00 00 00 00 00 00 00
            13 01 00 00
            08 00 03 00 03 00 00 00
            0a 00 06 00 00 11 22 33 44 55 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 64 00 00 00
            24 00 15 80                         # STA_INFO (len=36)
               20 00 08 80                      # TX_BITRATE (len=32, type=0x8008 = NESTED | 8)
                  06 00 01 00 e8 03 00 00       # BITRATE (16-bit) = 1000 (100 Mbps)
                  08 00 05 00 c0 5d 00 00       # BITRATE32 (32-bit) = 24000 (2.4 Gbps)
                  05 00 02 00 07 00 00 00       # MCS = 7
                  04 00 08 00                   # 80_MHZ flag (len=4)
        ";

        var bytes = HexToBytes(stationHex);
        var res = LinuxNl80211Protocol.ParseStationResponse(bytes, 106, 28, 3, 0x1000UL, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

        Assert.True(res.IsSuccess);
        var rate = res.Item?.TxRate;
        Assert.NotNull(rate);
        Assert.Equal(2_400_000_000UL, rate.BitrateBps);
        Assert.Equal((byte)7, rate.Mcs);
        Assert.True(rate.Is80Mhz);
    }

    [Fact]
    public void AbiMatrix_RateInfo_ZeroLength_Flag_Attributes_Enforced()
    {
        // TX_BITRATE payload:
        // 40_MHZ: 4
        // SHORT_GI: 4
        // BITRATE: 8
        // Total TX_BITRATE = 16 bytes payload + 4 hdr = 20 bytes (0x14 00 08 80)
        // STA_INFO = 20 bytes payload + 4 hdr = 24 bytes (0x18 00 15 80)
        // Top-level = 16 + 4 + 8 + 12 + 12 + 8 + 24 = 84 bytes (0x54)
        const string validFlagsHex = @"
            54 00 00 00 1c 00 00 00 6a 00 00 00 00 00 00 00
            13 01 00 00
            08 00 03 00 03 00 00 00
            0a 00 06 00 00 11 22 33 44 55 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 64 00 00 00
            18 00 15 80                         # STA_INFO (len=24)
               14 00 08 80                      # TX_BITRATE (len=20)
                  04 00 03 00                   # 40_MHZ (len=4)
                  04 00 04 00                   # SHORT_GI (len=4)
                  06 00 01 00 00 01 00 00       # BITRATE = 256
        ";

        var bytes = HexToBytes(validFlagsHex);
        var res = LinuxNl80211Protocol.ParseStationResponse(bytes, 106, 28, 3, 0x1000UL, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

        Assert.True(res.IsSuccess);
        var rate = res.Item?.TxRate;
        Assert.NotNull(rate);
        Assert.True(rate.Is40Mhz);
        Assert.True(rate.IsShortGi);
    }

    #endregion

    #region Section J: MLO Wire Response Matrix

    [Fact]
    public void AbiMatrix_Mlo_Two_Bss_Links_Parsed_With_Wire_Identity_Facts()
    {
        // Link 0 BSS: 12 (BSSID) + 8 (FREQ) + 8 (STATUS) + 8 (MLO_LINK_ID) + 12 (MLD_ADDR) = 48 bytes + 4 hdr = 52 bytes (0x34 00 2f 80)
        // Msg 1 (Link 0): 16 (hdr) + 4 (genl) + 8 (IFINDEX) + 12 (WDEV) + 8 (GEN) + 52 (BSS) = 100 bytes (0x64)
        // Msg 2 (Link 1): 100 bytes (0x64)
        // Msg 3 (NLMSG_DONE): 20 bytes (0x14)
        const string mloBssHex = @"
            64 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00 # Link 0 (len=100)
            22 01 00 00
            08 00 03 00 03 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00 # WDEV = 0x1000UL
            08 00 2e 00 64 00 00 00
            34 00 2f 80
               0a 00 01 00 00 11 22 33 44 01 00 00 # BSSID 01
               08 00 02 00 3c 14 00 00             # FREQ 5180
               08 00 09 00 01 00 00 00             # ASSOCIATED
               05 00 15 00 00 00 00 00             # MLO_LINK_ID = 0 (len=5, 3 pad -> 8 bytes)
               0a 00 16 00 00 11 22 33 44 00 00 00 # MLD_ADDR = 00:11:22:33:44:00 (12 bytes)
            64 00 00 00 1c 00 02 00 69 00 00 00 00 00 00 00 # Link 1 (len=100)
            22 01 00 00
            08 00 03 00 03 00 00 00
            0c 00 99 00 00 10 00 00 00 00 00 00 # WDEV = 0x1000UL
            08 00 2e 00 64 00 00 00
            34 00 2f 80
               0a 00 01 00 00 11 22 33 44 02 00 00 # BSSID 02
               08 00 02 00 57 17 00 00             # FREQ 5975
               08 00 09 00 01 00 00 00             # ASSOCIATED
               05 00 15 00 01 00 00 00             # MLO_LINK_ID = 1 (8 bytes)
               0a 00 16 00 00 11 22 33 44 00 00 00 # MLD_ADDR = 00:11:22:33:44:00 (12 bytes)
            14 00 00 00 03 00 02 00 69 00 00 00 00 00 00 00 00 00 00 00 # DONE
        ";

        var bytes = HexToBytes(mloBssHex);
        var result = LinuxNl80211Protocol.ParseBssDump(bytes, 105, 28, 3, 0x1000UL);

        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Items.Count);

        var link0 = result.Items[0];
        Assert.Equal((byte)0, link0.MloLinkId);
        Assert.Equal("00:11:22:33:44:00", link0.MldAddressString);
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 }, link0.MldAddress);

        var link1 = result.Items[1];
        Assert.Equal((byte)1, link1.MloLinkId);
        Assert.Equal("00:11:22:33:44:00", link1.MldAddressString);
    }

    [Fact]
    public void AbiMatrix_Mlo_Station_TopLevel_Mld_Attrs_Preserved()
    {
        // Top-level attrs:
        // IFINDEX: 8
        // MAC: 12
        // WDEV: 12
        // GENERATION: 8
        // MLO_LINK_ID (313 = 0x0139): 8
        // MLD_ADDR (314 = 0x013A): 12
        // STA_INFO: 20 (SIGNAL + SIGNAL_AVG)
        // Total top-level = 16 (hdr) + 4 (genl) + 8 + 12 + 12 + 8 + 8 + 12 + 20 = 100 bytes (0x64)
        const string mloStationHex = @"
            64 00 00 00 1c 00 00 00 6a 00 00 00 00 00 00 00
            13 01 00 00
            08 00 03 00 03 00 00 00
            0a 00 06 00 aa bb cc dd ee 00 00 00 # MAC = MLD
            0c 00 99 00 00 10 00 00 00 00 00 00
            08 00 2e 00 64 00 00 00
            05 00 39 01 02 00 00 00             # MLO_LINK_ID = 2 (type=313 = 0x0139)
            0a 00 3a 01 aa bb cc dd ee 00 00 00 # MLD_ADDR = AA:BB:CC:DD:EE:00 (type=314 = 0x013A)
            14 00 15 80                         # STA_INFO
               05 00 07 00 c2 00 00 00
               05 00 0d 00 c0 00 00 00
        ";

        var bytes = HexToBytes(mloStationHex);
        var res = LinuxNl80211Protocol.ParseStationResponse(bytes, 106, 28, 3, 0x1000UL, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x00 });

        Assert.True(res.IsSuccess);
        var sta = res.Item;
        Assert.NotNull(sta);
        Assert.Equal((byte)2, sta.MloLinkId);
        Assert.Equal("AA:BB:CC:DD:EE:00", sta.MldAddressString);
    }

    #endregion

    #region Section K: Multipart State Machine & Transport Invariants

    [Fact]
    public void AbiMatrix_Multipart_Data_And_Done_Yields_Complete()
    {
        const string dumpHex = @"
            28 00 00 00 1c 00 02 00 67 00 00 00 00 00 00 00
            03 01 00 00 08 00 01 00 00 00 00 00 09 00 02 00 70 68 79 30 00 00 00 00
            14 00 00 00 03 00 02 00 67 00 00 00 00 00 00 00 00 00 00 00
        ";

        var res = LinuxNl80211Protocol.ParseWiphyDump(HexToBytes(dumpHex), 103, true);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
    }

    [Fact]
    public void AbiMatrix_Multipart_Data_Without_Done_Yields_Incomplete()
    {
        const string dumpHex = @"
            28 00 00 00 1c 00 02 00 67 00 00 00 00 00 00 00
            03 01 00 00 08 00 01 00 00 00 00 00 09 00 02 00 70 68 79 30 00 00 00 00
        ";

        var res = LinuxNl80211Protocol.ParseWiphyDump(HexToBytes(dumpHex), 103, true);
        Assert.Equal(LinuxNl80211DumpStatus.Incomplete, res.Status);
    }

    [Fact]
    public void AbiMatrix_Multipart_Data_And_NlmsgError_Yields_KernelError()
    {
        const string errorDumpHex = @"
            28 00 00 00 1c 00 02 00 67 00 00 00 00 00 00 00
            03 01 00 00 08 00 01 00 00 00 00 00 09 00 02 00 70 68 79 30 00 00 00 00
            24 00 00 00 02 00 02 00 67 00 00 00 00 00 00 00
            f0 ff ff ff # errno = -16 (-EBUSY)
            18 00 00 00 1c 00 05 03 67 00 00 00 00 00 00 00
        ";

        var res = LinuxNl80211Protocol.ParseWiphyDump(HexToBytes(errorDumpHex), 103, true);
        Assert.Equal(LinuxNl80211DumpStatus.KernelError, res.Status);
        Assert.Equal(-16, res.ErrorCode);
    }

    [Fact]
    public void AbiMatrix_Multipart_Done_With_Negative_Errno_Yields_KernelError()
    {
        const string doneErrorHex = @"
            28 00 00 00 1c 00 02 00 67 00 00 00 00 00 00 00
            03 01 00 00 08 00 01 00 00 00 00 00 09 00 02 00 70 68 79 30 00 00 00 00
            18 00 00 00 03 00 02 00 67 00 00 00 00 00 00 00 # NLMSG_DONE
            ea ff ff ff # payload = -22 (-EINVAL)
            00 00 00 00 # padding
        ";

        var res = LinuxNl80211Protocol.ParseWiphyDump(HexToBytes(doneErrorHex), 103, true);
        Assert.Equal(LinuxNl80211DumpStatus.KernelError, res.Status);
        Assert.Equal(-22, res.ErrorCode);
    }

    #endregion
}
