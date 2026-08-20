using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

public class LinuxWifiRadioTests
{
    [Fact]
    public void GenlProtocol_Builds_And_Parses_GetFamilyRequest()
    {
        uint seq = 42;
        byte[] req = LinuxGenlProtocol.BuildGetFamilyRequest("nl80211", seq, 100);

        Assert.NotNull(req);
        Assert.True(req.Length >= LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize);

        // Check command
        Assert.Equal(LinuxGenlProtocol.CTRL_CMD_GETFAMILY, req[16]);
    }

    [Fact]
    public void GenlProtocol_Parses_Dynamic_Nl80211_Family_Response()
    {
        uint seq = 101;
        // Build mock GETFAMILY response with nl80211 ID=28, version=1, mcast groups
        byte[] response = BuildMockGetFamilyResponse(seq, familyId: 28, familyName: "nl80211", ("scan", 1), ("mlme", 2));

        int result = LinuxGenlProtocol.ParseGetFamilyResponse(response, seq, out var familyInfo);

        Assert.Equal(0, result);
        Assert.NotNull(familyInfo);
        Assert.Equal(28, familyInfo.FamilyId);
        Assert.Equal("nl80211", familyInfo.FamilyName);
        Assert.True(familyInfo.MulticastGroups.ContainsKey("scan"));
        Assert.Equal(1u, familyInfo.MulticastGroups["scan"]);
        Assert.True(familyInfo.MulticastGroups.ContainsKey("mlme"));
        Assert.Equal(2u, familyInfo.MulticastGroups["mlme"]);
    }

    [Fact]
    public void GenlProtocol_Rejects_Truncated_Or_Malformed_Payloads_Safely()
    {
        uint seq = 102;
        byte[] truncatedHeader = new byte[8];
        int res1 = LinuxGenlProtocol.ParseGetFamilyResponse(truncatedHeader, seq, out var fam1);
        Assert.NotEqual(0, res1);
        Assert.Null(fam1);

        byte[] truncatedPayload = new byte[16];
        int res2 = LinuxGenlProtocol.ParseGetFamilyResponse(truncatedPayload, seq, out var fam2);
        Assert.NotEqual(0, res2);
        Assert.Null(fam2);
    }

    [Fact]
    public void GenlProtocol_Handles_Ack_And_Errors_Correctly()
    {
        uint seq = 103;

        // ACK message: NLMSG_ERROR with error == 0
        byte[] ackMsg = BuildMockNlmsgError(seq, errorCode: 0);
        int ackRes = LinuxGenlProtocol.ParseGetFamilyResponse(ackMsg, seq, out var famAck);
        Assert.NotEqual(0, ackRes); // No family payload in pure ACK, returns ENOENT safely

        // Error message: NLMSG_ERROR with error == -EPERM (-1)
        byte[] errorMsg = BuildMockNlmsgError(seq, errorCode: -1);
        int errRes = LinuxGenlProtocol.ParseGetFamilyResponse(errorMsg, seq, out var famErr);
        Assert.Equal(-1, errRes);
        Assert.Null(famErr);
    }

    [Fact]
    public void Nl80211Protocol_Has_Correct_Authoritative_UAPI_Constants()
    {
        Assert.Equal(21, LinuxNl80211Protocol.NL80211_ATTR_STA_INFO);
        Assert.Equal(47, LinuxNl80211Protocol.NL80211_ATTR_BSS);
        Assert.Equal(174, LinuxNl80211Protocol.NL80211_ATTR_SPLIT_WIPHY_DUMP);
    }

    [Fact]
    public void Nl80211Protocol_Rejects_Interrupted_Dump_NLM_F_DUMP_INTR()
    {
        uint seq = 202;
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 3, ifname: "wlan0", wiphy: 0, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION, isInterrupted: true);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: true, out var list);
        Assert.Equal(-4, res); // -EINTR
        Assert.Empty(list);
    }

    [Fact]
    public void Nl80211Protocol_Rejects_Incomplete_Dump_Without_NLMSG_DONE()
    {
        uint seq = 203;
        // Interface payload received, but stream timed out before NLMSG_DONE
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 3, ifname: "wlan0", wiphy: 0, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION, includeDone: false);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: true, out var list);
        Assert.Equal(-11, res); // -EAGAIN / incomplete dump
        Assert.Empty(list);     // Must reject partial snapshot
    }

    [Fact]
    public void Nl80211Protocol_Rejects_Dump_With_Only_Pure_ACK()
    {
        uint seq = 204;
        // Kernel returns pure ACK (NLMSG_ERROR with error=0) for request receipt, but timeout before NLMSG_DONE
        byte[] response = BuildMockNlmsgError(seq, errorCode: 0);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: true, out var list);
        Assert.Equal(-11, res); // -EAGAIN / incomplete dump
        Assert.Empty(list);
    }

    [Fact]
    public void Nl80211Protocol_Accepts_Complete_Dump_With_NLMSG_DONE()
    {
        uint seq = 205;
        // Valid interface payload followed by NLMSG_DONE
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 3, ifname: "wlan0", wiphy: 0, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION, includeDone: true);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: true, out var list);
        Assert.Equal(0, res);
        Assert.Single(list);
        Assert.Equal(3, list[0].IfIndex);
        Assert.Equal("wlan0", list[0].IfName);
        Assert.Equal(0u, list[0].WiphyIndex);
    }

    [Fact]
    public void Nl80211Protocol_Rejects_Dump_When_NLMSG_DONE_Has_DUMP_INTR()
    {
        uint seq = 206;
        // Valid interface payload, but final NLMSG_DONE has NLM_F_DUMP_INTR set
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 3, ifname: "wlan0", wiphy: 0, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION, includeDone: true, isInterruptedDone: true);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: true, out var list);
        Assert.Equal(-4, res); // -EINTR
        Assert.Empty(list);
    }

    [Fact]
    public void Nl80211Protocol_Interface_Without_WIPHY_Yields_Null_WiphyIndex()
    {
        uint seq = 207;
        // Interface response lacking NL80211_ATTR_WIPHY
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 5, ifname: "wlan1", wiphy: null, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION, includeDone: true);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: true, out var list);
        Assert.Equal(0, res);
        Assert.Single(list);
        Assert.Equal(5, list[0].IfIndex);
        Assert.Equal("wlan1", list[0].IfName);
        Assert.Null(list[0].WiphyIndex); // Crucial: must be null, never default to 0 (phy0)!
    }

    [Fact]
    public void Nl80211Protocol_Interface_With_Explicit_WIPHY_Zero_Yields_WiphyIndex_Zero()
    {
        uint seq = 208;
        // Interface response with explicit NL80211_ATTR_WIPHY = 0
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 3, ifname: "wlan0", wiphy: 0, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION, includeDone: true);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: true, out var list);
        Assert.Equal(0, res);
        Assert.Single(list);
        Assert.Equal(3, list[0].IfIndex);
        Assert.Equal(0u, list[0].WiphyIndex); // Explicit phy0 preserved
    }

    [Fact]
    public void Nl80211Radio_Missing_WiphyIndex_And_Blocked_Phy0_Never_Yields_RadioOn_False()
    {
        // Interface with missing WiphyIndex (null)
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(5, "wlan1", null, null, null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        // phy0 is hard and soft blocked!
        var mockRfkill = new MockLinuxRfkillReader();
        mockRfkill.SetObservation(0, new LinuxRfkillObservation(0, 0, HardBlocked: true, SoftBlocked: true, LinuxRfkillEvidenceBasis.DevRfkill));

        using var radio = new LinuxNl80211Radio(mockSocket, mockRfkill);

        // Crucial Invariant 249 check: Unmapped interface must NEVER be falsely attributed to phy0 and must return null (unknown), NOT false!
        bool? isRadioOn = radio.IsRadioOn("wlan1");
        Assert.Null(isRadioOn);
    }

    [Fact]
    public void Nl80211Protocol_Builds_And_Parses_Single_Interface_Response()
    {
        uint seq = 201;
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 3, ifname: "wlan0", wiphy: 0, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, isDump: false, out var list);
        Assert.Equal(0, res);
        Assert.Single(list);
        Assert.Equal(3, list[0].IfIndex);
        Assert.Equal("wlan0", list[0].IfName);
        Assert.Equal(0u, list[0].WiphyIndex);
        Assert.Equal(LinuxNl80211Protocol.NL80211_IFTYPE_STATION, list[0].IfType);
    }

    [Fact]
    public void Nl80211Radio_Returns_RadioOn_True_When_Rfkill_Unblocked()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        var mockRfkill = new MockLinuxRfkillReader();
        mockRfkill.SetObservation(0, new LinuxRfkillObservation(0, 0, HardBlocked: false, SoftBlocked: false, LinuxRfkillEvidenceBasis.DevRfkill));

        using var radio = new LinuxNl80211Radio(mockSocket, mockRfkill);

        bool? isRadioOn = radio.IsRadioOn("3");
        Assert.True(isRadioOn);

        bool? isRadioOnByName = radio.IsRadioOn("wlan0");
        Assert.True(isRadioOnByName);
    }

    [Theory]
    [InlineData(true, false)]  // Hard blocked
    [InlineData(false, true)]  // Soft blocked
    [InlineData(true, true)]   // Both blocked
    public void Nl80211Radio_Returns_RadioOn_False_On_Positive_Block_Evidence(bool hard, bool soft)
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        var mockRfkill = new MockLinuxRfkillReader();
        mockRfkill.SetObservation(0, new LinuxRfkillObservation(0, 0, HardBlocked: hard, SoftBlocked: soft, LinuxRfkillEvidenceBasis.DevRfkill));

        using var radio = new LinuxNl80211Radio(mockSocket, mockRfkill);

        bool? isRadioOn = radio.IsRadioOn("wlan0");
        Assert.False(isRadioOn); // Invariant 249: Positive evidence of radio block
    }

    [Fact]
    public void Nl80211Radio_Returns_Null_When_Rfkill_Missing_Or_Ambiguous_Invariant_249()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        var mockRfkill = new MockLinuxRfkillReader();
        // No observation registered for wiphy 0

        using var radio = new LinuxNl80211Radio(mockSocket, mockRfkill);

        bool? isRadioOn = radio.IsRadioOn("wlan0");
        Assert.Null(isRadioOn); // Invariant 249: RADIO_ON_UNKNOWN_NEVER_BECOMES_FALSE
    }

    [Fact]
    public void Nl80211Radio_Never_Cross_Attributes_Rfkill_On_Multi_Radio_System()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(4, "wlan1", 1, "phy1", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        var mockRfkill = new MockLinuxRfkillReader();
        // wlan0 (phy0) is unblocked
        mockRfkill.SetObservation(0, new LinuxRfkillObservation(0, 0, HardBlocked: false, SoftBlocked: false, LinuxRfkillEvidenceBasis.DevRfkill));
        // wlan1 (phy1) is soft-blocked
        mockRfkill.SetObservation(1, new LinuxRfkillObservation(1, 1, HardBlocked: false, SoftBlocked: true, LinuxRfkillEvidenceBasis.DevRfkill));

        using var radio = new LinuxNl80211Radio(mockSocket, mockRfkill);

        Assert.True(radio.IsRadioOn("wlan0"));
        Assert.False(radio.IsRadioOn("wlan1"));
        Assert.Null(radio.IsRadioOn("eth0")); // Unmapped interface returns null
    }

    [Fact]
    public void Nl80211Radio_Placeholders_Return_Null_In_7A()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        var mockRfkill = new MockLinuxRfkillReader();
        using var radio = new LinuxNl80211Radio(mockSocket, mockRfkill);

        Assert.Null(radio.ReadAssociation("wlan0"));
        Assert.Null(radio.ReadAccessPoint("MySSID", "00:11:22:33:44:55"));
        Assert.Null(radio.IsSsidVisible("MySSID"));
        radio.RequestUrgentScan(); // No-op, should not throw
    }

    // --- Mock Helpers ---

    private static byte[] BuildMockGetFamilyResponse(uint seq, ushort familyId, string familyName, params (string, uint)[] mcastGroups)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] nameBytes = Encoding.UTF8.GetBytes(familyName);
        int nameAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);
        int idAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 2);

        int mcastTotalLen = 0;
        byte[]? mcastBytes = null;
        if (mcastGroups.Length > 0)
        {
            using var mcastStream = new MemoryStream();
            using (var mbw = new BinaryWriter(mcastStream, Encoding.UTF8, leaveOpen: true))
            {
                int idx = 1;
                foreach (var (gname, gid) in mcastGroups)
                {
                    byte[] gnameBytes = Encoding.UTF8.GetBytes(gname);
                    int gnLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + gnameBytes.Length + 1);
                    int giLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
                    int groupPayloadLen = gnLen + giLen;

                    mbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + groupPayloadLen));
                    mbw.Write((ushort)idx++); // nested attribute index

                    // Group Name
                    mbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + gnameBytes.Length + 1));
                    mbw.Write(LinuxGenlProtocol.CTRL_ATTR_MCAST_GRP_NAME);
                    mbw.Write(gnameBytes);
                    mbw.Write((byte)0);
                    WritePadding(mbw, LinuxGenlProtocol.NlaHeaderSize + gnameBytes.Length + 1);

                    // Group ID
                    mbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                    mbw.Write(LinuxGenlProtocol.CTRL_ATTR_MCAST_GRP_ID);
                    mbw.Write(gid);
                }
            }
            mcastBytes = mcastStream.ToArray();
            mcastTotalLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + mcastBytes.Length);
        }

        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize + nameAttrLen + idAttrLen + mcastTotalLen;

        // nlmsghdr
        bw.Write(totalLen);
        bw.Write(LinuxGenlProtocol.GENL_ID_CTRL);
        bw.Write((ushort)0); // flags
        bw.Write(seq);
        bw.Write((uint)0); // pid

        // genlmsghdr
        bw.Write(LinuxGenlProtocol.CTRL_CMD_NEWFAMILY);
        bw.Write((byte)1); // version
        bw.Write((ushort)0); // reserved

        // CTRL_ATTR_FAMILY_ID
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 2));
        bw.Write(LinuxGenlProtocol.CTRL_ATTR_FAMILY_ID);
        bw.Write(familyId);
        WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + 2);

        // CTRL_ATTR_FAMILY_NAME
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1));
        bw.Write(LinuxGenlProtocol.CTRL_ATTR_FAMILY_NAME);
        bw.Write(nameBytes);
        bw.Write((byte)0);
        WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);

        // CTRL_ATTR_MCAST_GROUPS
        if (mcastBytes != null && mcastBytes.Length > 0)
        {
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + mcastBytes.Length));
            bw.Write((ushort)(LinuxGenlProtocol.CTRL_ATTR_MCAST_GROUPS | 0x8000)); // NLA_F_NESTED
            bw.Write(mcastBytes);
            WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + mcastBytes.Length);
        }

        return ms.ToArray();
    }

    [Fact]
    public void Nl80211Protocol_Payload_Followed_By_Done_Yields_Complete()
    {
        uint seq = 301;
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(p1, done);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(stream, seq, isDump: true);
        Assert.True(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
        Assert.Single(res.Items);
        Assert.Equal("wlan0", res.Items[0].IfName);
    }

    [Fact]
    public void Nl80211Protocol_Zero_Payloads_Followed_By_Done_Yields_Complete_Zero_Count()
    {
        uint seq = 302;
        var done = BuildMockDoneMessage(seq, error: 0);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(done, seq, isDump: true);
        Assert.True(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Payload_Then_Timeout_Without_Done_Yields_Incomplete()
    {
        uint seq = 303;
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(p1, seq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Incomplete, res.Status);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Ack_Then_Timeout_Without_Done_Yields_Incomplete()
    {
        uint seq = 304;
        var ack = BuildMockNlmsgError(seq, errorCode: 0);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(ack, seq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Incomplete, res.Status);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Ack_Then_Payload_Then_Done_Yields_Complete()
    {
        uint seq = 305;
        var ack = BuildMockNlmsgError(seq, errorCode: 0);
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(ack, p1, done);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(stream, seq, isDump: true);
        Assert.True(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
        Assert.Single(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Payload_Then_Ack_Then_Done_Yields_Complete()
    {
        uint seq = 306;
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);
        var ack = BuildMockNlmsgError(seq, errorCode: 0);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(p1, ack, done);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(stream, seq, isDump: true);
        Assert.True(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
        Assert.Single(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_NlmsgError_Negative_Yields_KernelError_And_Propagates_Errno()
    {
        uint seq = 307;
        var err = BuildMockNlmsgError(seq, errorCode: -19); // -ENODEV

        var res = LinuxNl80211Protocol.ParseInterfaceDump(err, seq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.KernelError, res.Status);
        Assert.Equal(-19, res.ErrorCode);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Done_With_Negative_Error_Yields_KernelError()
    {
        uint seq = 308;
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);
        var doneErr = BuildMockDoneMessage(seq, error: -105); // -ENOBUFS
        var stream = CombineBuffers(p1, doneErr);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(stream, seq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.KernelError, res.Status);
        Assert.Equal(-105, res.ErrorCode);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Done_With_DumpIntr_Yields_Interrupted()
    {
        uint seq = 309;
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);
        var doneIntr = BuildMockDoneMessage(seq, error: 0, isInterrupted: true);
        var stream = CombineBuffers(p1, doneIntr);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(stream, seq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Interrupted, res.Status);
        Assert.Equal(-4, res.ErrorCode);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Malformed_Done_Length_Yields_Malformed()
    {
        uint seq = 310;
        // Construct DONE with illegal length of 18 bytes (between 16 and 20)
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(18); // invalid nlmsgLen
        bw.Write(LinuxGenlProtocol.NLMSG_DONE);
        bw.Write((ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);
        bw.Write((short)0); // 2 bytes instead of 4

        var res = LinuxNl80211Protocol.ParseInterfaceDump(ms.ToArray(), seq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_Done_HeaderOnly_16Bytes_Yields_Malformed_For_Dump()
    {
        uint seq = 312;
        // Construct standard 16-byte header without 4-byte return code
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(16); // 16 bytes header-only
        bw.Write(LinuxGenlProtocol.NLMSG_DONE);
        bw.Write((ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(ms.ToArray(), seq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_Stale_Seq_With_DumpIntr_Is_Ignored_From_Active_Dump()
    {
        uint staleSeq = 313;
        uint activeSeq = 314;

        // Stale message with DUMP_INTR and stale sequence
        var staleIntr = BuildMockInterfacePayload(staleSeq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, isInterrupted: true);
        var p1 = BuildMockInterfacePayload(activeSeq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, isInterrupted: false);
        var done = BuildMockDoneMessage(activeSeq, error: 0);
        var stream = CombineBuffers(staleIntr, p1, done);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(stream, activeSeq, isDump: true);
        Assert.True(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
        Assert.Single(res.Items);
        Assert.Equal("wlan0", res.Items[0].IfName);
    }

    [Fact]
    public void Nl80211Protocol_Matching_Seq_With_DumpIntr_Yields_Interrupted()
    {
        uint activeSeq = 315;
        var p1 = BuildMockInterfacePayload(activeSeq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, isInterrupted: true);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(p1, activeSeq, isDump: true);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Interrupted, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_Single_Query_Ack_Only_Yields_Incomplete()
    {
        uint seq = 316;
        var ack = BuildMockNlmsgError(seq, errorCode: 0);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(ack, seq, isDump: false);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Incomplete, res.Status);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Single_Query_Ack_Then_Data_Yields_Complete()
    {
        uint seq = 317;
        var ack = BuildMockNlmsgError(seq, errorCode: 0);
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);
        var stream = CombineBuffers(ack, p1);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(stream, seq, isDump: false);
        Assert.True(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
        Assert.Single(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_Single_Query_Data_Only_Yields_Complete()
    {
        uint seq = 318;
        var p1 = BuildMockInterfacePayload(seq, 3, "wlan0", 0, LinuxNl80211Protocol.NL80211_IFTYPE_STATION);

        var res = LinuxNl80211Protocol.ParseInterfaceDump(p1, seq, isDump: false);
        Assert.True(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Complete, res.Status);
        Assert.Single(res.Items);
    }

    [Fact]
    public void GenlProtocol_GetFamily_Ack_Then_Data_Yields_Success()
    {
        uint seq = 319;
        var ack = BuildMockNlmsgError(seq, errorCode: 0);
        var famData = BuildMockGetFamilyResponse(seq, familyId: 28, familyName: "nl80211");
        var stream = CombineBuffers(ack, famData);

        int ret = LinuxGenlProtocol.ParseGetFamilyResponse(stream, seq, "nl80211", out var famInfo);
        Assert.Equal(0, ret);
        Assert.NotNull(famInfo);
        Assert.Equal((ushort)28, famInfo.FamilyId);
        Assert.Equal("nl80211", famInfo.FamilyName);
    }

    [Fact]
    public void GenlProtocol_GetFamily_Ack_Only_Yields_Negative_Or_Null()
    {
        uint seq = 320;
        var ack = BuildMockNlmsgError(seq, errorCode: 0);

        int ret = LinuxGenlProtocol.ParseGetFamilyResponse(ack, seq, "nl80211", out var famInfo);
        Assert.NotEqual(0, ret);
        Assert.Null(famInfo);
    }

    [Fact]
    public void GenlProtocol_GetFamily_Seq_Zero_Unsolicited_Ignored()
    {
        uint activeSeq = 321;
        // Unsolicited notification with seq = 0
        var unrequested = BuildMockGetFamilyResponse(seq: 0, familyId: 28, familyName: "nl80211");

        int ret = LinuxGenlProtocol.ParseGetFamilyResponse(unrequested, activeSeq, "nl80211", out var famInfo);
        Assert.NotEqual(0, ret);
        Assert.Null(famInfo);
    }

    [Fact]
    public void GenlProtocol_GetFamily_Stale_NonZero_Seq_Ignored()
    {
        uint staleSeq = 322;
        uint activeSeq = 323;
        var staleFam = BuildMockGetFamilyResponse(staleSeq, familyId: 28, familyName: "nl80211");

        int ret = LinuxGenlProtocol.ParseGetFamilyResponse(staleFam, activeSeq, "nl80211", out var famInfo);
        Assert.NotEqual(0, ret);
        Assert.Null(famInfo);
    }

    [Fact]
    public void GenlProtocol_GetFamily_Wrong_NlmsgType_Rejected()
    {
        uint seq = 324;
        // Construct message with nlmsgType != GENL_ID_CTRL (e.g. nlmsgType = 28)
        var wrongTypeMsg = BuildCustomGetFamilyResponse(seq, nlmsgType: 28, genlCmd: LinuxGenlProtocol.CTRL_CMD_NEWFAMILY, familyId: 28, familyName: "nl80211");

        int ret = LinuxGenlProtocol.ParseGetFamilyResponse(wrongTypeMsg, seq, "nl80211", out var famInfo);
        Assert.NotEqual(0, ret);
        Assert.Null(famInfo);
    }

    [Fact]
    public void GenlProtocol_GetFamily_Wrong_GenlCmd_Rejected()
    {
        uint seq = 325;
        // Construct message with genlCmd != CTRL_CMD_NEWFAMILY (e.g. CTRL_CMD_DELFAMILY = 2)
        var wrongCmdMsg = BuildCustomGetFamilyResponse(seq, nlmsgType: LinuxGenlProtocol.GENL_ID_CTRL, genlCmd: 2, familyId: 28, familyName: "nl80211");

        int ret = LinuxGenlProtocol.ParseGetFamilyResponse(wrongCmdMsg, seq, "nl80211", out var famInfo);
        Assert.NotEqual(0, ret);
        Assert.Null(famInfo);
    }

    [Fact]
    public void GenlProtocol_GetFamily_Wrong_FamilyName_Rejected()
    {
        uint seq = 326;
        // Response contains "taskstats" instead of requested "nl80211"
        var wrongNameMsg = BuildMockGetFamilyResponse(seq, familyId: 19, familyName: "taskstats");

        int ret = LinuxGenlProtocol.ParseGetFamilyResponse(wrongNameMsg, seq, "nl80211", out var famInfo);
        Assert.NotEqual(0, ret);
        Assert.Null(famInfo);
    }

    private static byte[] BuildCustomGetFamilyResponse(uint seq, ushort nlmsgType, byte genlCmd, ushort familyId, string familyName)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] nameBytes = Encoding.UTF8.GetBytes(familyName);
        int nameAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);
        int idAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 2);

        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize + nameAttrLen + idAttrLen;

        // nlmsghdr
        bw.Write(totalLen);
        bw.Write(nlmsgType);
        bw.Write((ushort)0); // flags
        bw.Write(seq);
        bw.Write((uint)0); // pid

        // genlmsghdr
        bw.Write(genlCmd);
        bw.Write((byte)1); // version
        bw.Write((ushort)0); // reserved

        // CTRL_ATTR_FAMILY_ID
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 2));
        bw.Write(LinuxGenlProtocol.CTRL_ATTR_FAMILY_ID);
        bw.Write(familyId);
        WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + 2);

        // CTRL_ATTR_FAMILY_NAME
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1));
        bw.Write(LinuxGenlProtocol.CTRL_ATTR_FAMILY_NAME);
        bw.Write(nameBytes);
        bw.Write((byte)0);
        WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);

        return ms.ToArray();
    }

    [Fact]
    public void Nl80211Protocol_BuildGetScanRequest_Encodes_IfIndex_And_Dump_Flags_Never_TriggerScan()
    {
        uint seq = 401;
        ushort famId = 28;
        int ifindex = 3;

        byte[] req = LinuxNl80211Protocol.BuildGetScanRequest(famId, ifindex, seq);

        Assert.True(req.Length >= LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize);
        ushort flags = MemoryMarshal.Read<ushort>(req.AsSpan(6, 2));
        Assert.True((flags & LinuxGenlProtocol.NLM_F_DUMP) != 0);
        Assert.True((flags & LinuxGenlProtocol.NLM_F_REQUEST) != 0);

        byte cmd = req[16];
        Assert.Equal(LinuxNl80211Protocol.NL80211_CMD_GET_SCAN, cmd);
        Assert.NotEqual(LinuxNl80211Protocol.NL80211_CMD_TRIGGER_SCAN, cmd); // Invariant 259

        // Verify NL80211_ATTR_IFINDEX attribute
        ushort attrLen = MemoryMarshal.Read<ushort>(req.AsSpan(20, 2));
        ushort attrType = MemoryMarshal.Read<ushort>(req.AsSpan(22, 2));
        int attrVal = MemoryMarshal.Read<int>(req.AsSpan(24, 4));

        Assert.Equal(LinuxNl80211Protocol.NL80211_ATTR_IFINDEX, attrType);
        Assert.Equal(3, attrVal);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Ignores_Mismatched_Sequence()
    {
        uint staleSeq = 402;
        uint activeSeq = 403;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staleBss = BuildMockBssRecord(staleSeq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);
        var activeBss = BuildMockBssRecord(activeSeq, 28, 3, bssid, 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);
        var done = BuildMockDoneMessage(activeSeq, error: 0);
        var stream = CombineBuffers(staleBss, activeBss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, activeSeq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        Assert.Equal(5180u, res.Items[0].FrequencyMhz);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Rejects_Wrong_Family_Id()
    {
        uint seq = 404;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = BuildMockBssRecord(seq, familyId: 99, ifindex: 3, bssid: bssid, freq: 2412, status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);

        var res = LinuxNl80211Protocol.ParseBssDump(bss, seq, expectedFamilyId: 28, expectedIfIndex: 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Rejects_Wrong_Genl_Command()
    {
        uint seq = 405;
        // Construct message with cmd != NEW_SCAN_RESULTS
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(20);
        bw.Write((ushort)28);
        bw.Write((ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);
        bw.Write((byte)LinuxNl80211Protocol.NL80211_CMD_NEW_INTERFACE); // wrong command
        bw.Write((byte)1);
        bw.Write((ushort)0);

        var res = LinuxNl80211Protocol.ParseBssDump(ms.ToArray(), seq, expectedFamilyId: 28, expectedIfIndex: 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Rejects_Mismatched_IfIndex_Attribution()
    {
        uint seq = 406;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        // BSS returned for ifindex 4 when ifindex 3 was requested
        var bss = BuildMockBssRecord(seq, familyId: 28, ifindex: 4, bssid: bssid, freq: 2412, status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);

        var res = LinuxNl80211Protocol.ParseBssDump(bss, seq, expectedFamilyId: 28, expectedIfIndex: 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status); // Invariant 261
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Complete_Zero_Entries_Yields_Complete_Zero_Count()
    {
        uint seq = 407;
        var done = BuildMockDoneMessage(seq, error: 0);

        var res = LinuxNl80211Protocol.ParseBssDump(done, seq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Complete_Non_Associated_List_Yields_Complete()
    {
        uint seq = 408;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss1 = BuildMockBssRecord(seq, 28, 3, bssid, 2412, status: null); // unassociated
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss1, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        Assert.False(res.Items[0].IsAssociated);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Incomplete_Without_Done_Yields_Incomplete()
    {
        uint seq = 409;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss1 = BuildMockBssRecord(seq, 28, 3, bssid, 2412, status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);

        var res = LinuxNl80211Protocol.ParseBssDump(bss1, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Incomplete, res.Status);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Interrupted_Done_Yields_Interrupted()
    {
        uint seq = 410;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss1 = BuildMockBssRecord(seq, 28, 3, bssid, 2412, status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);
        var done = BuildMockDoneMessage(seq, error: 0, isInterrupted: true);
        var stream = CombineBuffers(bss1, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Interrupted, res.Status);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Done_Negative_Error_Yields_KernelError()
    {
        uint seq = 411;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss1 = BuildMockBssRecord(seq, 28, 3, bssid, 2412, status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);
        var done = BuildMockDoneMessage(seq, error: -19); // -ENODEV
        var stream = CombineBuffers(bss1, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.KernelError, res.Status);
        Assert.Equal(-19, res.ErrorCode);
        Assert.Empty(res.Items);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Associated_Status_With_Valid_Bssid_Yields_Associated()
    {
        uint seq = 412;
        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        byte[] ssidBytes = Encoding.UTF8.GetBytes("Corporate-WiFi");
        var bss1 = BuildMockBssRecord(seq, 28, 3, bssid, 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, signalMbm: -6700, signalUnspec: 75, ssid: ssidBytes);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss1, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        var item = res.Items[0];
        Assert.True(item.IsAssociated);
        Assert.Equal("AA:BB:CC:DD:EE:FF", item.BssidString);
        Assert.Equal("Corporate-WiFi", item.DisplaySsid);
        Assert.Equal(5180u, item.FrequencyMhz);
        Assert.Equal(-6700, item.SignalMbm);
        Assert.Equal(-67, item.SignalDbm);
        Assert.Equal((byte)75, item.SignalQuality);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Missing_Bssid_Or_Malformed_Length_Yields_Malformed()
    {
        uint seq = 413;
        byte[] invalidBssid = new byte[] { 0xAA, 0xBB, 0xCC }; // 3 bytes instead of 6
        var bss = BuildMockBssRecord(seq, 28, 3, invalidBssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);

        var res = LinuxNl80211Protocol.ParseBssDump(bss, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Malformed_Nlattr_Anywhere_Yields_Malformed()
    {
        uint seq = 414;
        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var bssCorrupted = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, corruptNlattr: true);

        var res = LinuxNl80211Protocol.ParseBssDump(bssCorrupted, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Malformed_Ssid_Ie_Only_Preserves_Association_With_Null_Ssid()
    {
        uint seq = 415;
        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        // Malformed IE payload only (Netlink framing is completely valid)
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, corruptSsidIe: true);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        var item = res.Items[0];
        Assert.True(item.IsAssociated);
        Assert.Equal("AA:BB:CC:DD:EE:FF", item.BssidString);
        Assert.Null(item.DisplaySsid);
        Assert.Null(item.SsidBytes);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Hidden_Zero_Length_Ssid_Preserved_Without_Synthetic_String()
    {
        uint seq = 416;
        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        byte[] hiddenSsid = Array.Empty<byte>(); // 0-length SSID
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, ssid: hiddenSsid);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        var item = res.Items[0];
        Assert.True(item.IsAssociated);
        Assert.NotNull(item.SsidBytes);
        Assert.Empty(item.SsidBytes);
        Assert.Null(item.DisplaySsid); // Never synthetic empty string ""
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Signal_Mbm_Parsed_Signed()
    {
        uint seq = 417;
        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, signalMbm: -8420);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        Assert.Equal(-8420, res.Items[0].SignalMbm);
        Assert.Equal(-84, res.Items[0].SignalDbm);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Two_Associated_Mlo_Bss_Entries_Both_Preserved()
    {
        uint seq = 418;
        byte[] bssid1 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssid2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0xFF };

        var bss1 = BuildMockBssRecord(seq, 28, 3, bssid1, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, mloLinkId: 0, mldAddr: mldAddr);
        var bss2 = BuildMockBssRecord(seq, 28, 3, bssid2, 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, mloLinkId: 1, mldAddr: mldAddr);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss1, bss2, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.True(res.IsComplete);
        Assert.Equal(2, res.Items.Count);

        // Invariant 262: Both links preserved with their distinct MLO Link IDs
        var link0 = res.Items.First(i => i.MloLinkId == 0);
        var link1 = res.Items.First(i => i.MloLinkId == 1);

        Assert.Equal("00:11:22:33:44:01", link0.BssidString);
        Assert.Equal("00:11:22:33:44:02", link1.BssidString);
        Assert.Equal("00:11:22:33:44:FF", link0.MldAddressString);
        Assert.Equal("00:11:22:33:44:FF", link1.MldAddressString);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociationObservation_Returns_NotAssociated_When_No_Associated_Bss()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        // Scan cache has BSS records, but none with STATUS_ASSOCIATED
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, null, 2412, null, null, null, null, null, null, null, null, null, null, null));

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        Assert.Equal(LinuxWirelessAssociationState.NotAssociated, obs.State);
        Assert.Empty(obs.Links);

        var assoc = radio.ReadAssociation("wlan0");
        Assert.Null(assoc); // Core projection is null when not associated
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociationObservation_Returns_Unknown_When_Bss_Dump_Incomplete()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        mockSocket.BssDumpStatus = LinuxNl80211DumpStatus.TimedOut;

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        // Invariant 256: Incomplete BSS dump NEVER becomes NotAssociated!
        Assert.Equal(LinuxWirelessAssociationState.Unknown, obs.State);

        var assoc = radio.ReadAssociation("wlan0");
        Assert.Null(assoc);
    }

    [Fact]
    public void Nl80211Radio_ReadAssociation_Single_Link_Projects_To_WirelessAssociation()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid, "AA:BB:CC:DD:EE:FF", Encoding.UTF8.GetBytes("HomeNet"), "HomeNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, 80, null, null, null, null, null, null, null, null));

        using var radio = new LinuxNl80211Radio(mockSocket);

        var assoc = radio.ReadAssociation("wlan0");
        Assert.NotNull(assoc);
        Assert.Equal("HomeNet", assoc.Ssid);
        Assert.Equal("AA:BB:CC:DD:EE:FF", assoc.Bssid);
        Assert.Equal(80, assoc.SignalQuality);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociation_Multi_Link_Mlo_Does_Not_Arbitrarily_Pick_First_Link()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null));

        byte[] bssid1 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssid2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid1, "00:11:22:33:44:01", null, "MloNet", 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, null, 0, null, null, null, null, null, null));
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid2, "00:11:22:33:44:02", null, "MloNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -5500, 90, null, 1, null, null, null, null, null, null));

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        Assert.Equal(LinuxWirelessAssociationState.Associated, obs.State);
        Assert.Equal(2, obs.Links.Count);

        // Invariant 262: Core projection returns null for MLO rather than guessing or picking first link
        var assoc = radio.ReadAssociation("wlan0");
        Assert.Null(assoc);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociation_Returns_Null_When_Interface_Resolution_Incomplete()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        // Interface dump fails
        mockSocket.InterfaceDumpStatus = LinuxNl80211DumpStatus.TimedOut;

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.Null(obs);

        var assoc = radio.ReadAssociation("wlan0");
        Assert.Null(assoc);
    }

    [Fact]
    public void Nl80211Protocol_GoldenWire_LiteralAttr10_Decodes_As_SeenMsAgo()
    {
        uint seq = 501;
        const ushort wireFamilyId = 28;
        const int wireIfIndex = 3;
        const ulong wireWdev = 0x1000UL;
        const uint wireGeneration = 100u;
        const ushort WireAttrIfIndex = 3;
        const ushort WireAttrWdev = 153;
        const ushort WireAttrGeneration = 46;
        const ushort WireAttrBss = 47;

        const ushort WireBssBssid = 1;
        const ushort WireBssFreq = 2;
        const ushort WireBssStatus = 9;
        const ushort WireBssSeenMsAgo = 10;

        var bssMs = new MemoryStream();
        using (var bbw = new BinaryWriter(bssMs))
        {
            // BSSID
            byte[] bssid = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 6));
            bbw.Write(WireBssBssid);
            bbw.Write(bssid);
            WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + 6);

            // FREQ
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bbw.Write(WireBssFreq);
            bbw.Write(5180u);

            // STATUS
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bbw.Write(WireBssStatus);
            bbw.Write(1u); // ASSOCIATED

            // Wire attr 10 = SEEN_MS_AGO (4 bytes uint)
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bbw.Write(WireBssSeenMsAgo);
            bbw.Write(2500u);
        }
        byte[] bssBytes = bssMs.ToArray();

        var msgMs = new MemoryStream();
        using (var bw = new BinaryWriter(msgMs))
        {
            int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 8) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);

            bw.Write(totalLen);
            bw.Write(wireFamilyId);
            bw.Write((ushort)0);
            bw.Write(seq);
            bw.Write((uint)0);

            bw.Write((byte)34); // NEW_SCAN_RESULTS
            bw.Write((byte)1);
            bw.Write((ushort)0);

            // IFINDEX
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrIfIndex);
            bw.Write(wireIfIndex);

            // WDEV (153)
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            bw.Write(WireAttrWdev);
            bw.Write(wireWdev);

            // GENERATION (46)
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrGeneration);
            bw.Write(wireGeneration);

            // BSS (47)
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length));
            bw.Write((ushort)(WireAttrBss | 0x8000));
            bw.Write(bssBytes);
            WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);
        }

        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(msgMs.ToArray(), done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, wireFamilyId, wireIfIndex, wireWdev);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        Assert.Equal(2500u, res.Items[0].SeenMsAgo);
    }

    [Fact]
    public void Nl80211Protocol_GoldenWire_LiteralAttr11_Decodes_As_BeaconIes_Never_SeenMsAgo()
    {
        uint seq = 502;
        const ushort wireFamilyId = 28;
        const int wireIfIndex = 3;
        const ulong wireWdev = 0x1000UL;
        const uint wireGeneration = 100u;
        const ushort WireAttrIfIndex = 3;
        const ushort WireAttrWdev = 153;
        const ushort WireAttrGeneration = 46;
        const ushort WireAttrBss = 47;

        const ushort WireBssBssid = 1;
        const ushort WireBssFreq = 2;
        const ushort WireBssStatus = 9;
        const ushort WireBssBeaconIes = 11;

        var bssMs = new MemoryStream();
        using (var bbw = new BinaryWriter(bssMs))
        {
            byte[] bssid = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 6));
            bbw.Write(WireBssBssid);
            bbw.Write(bssid);
            WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + 6);

            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bbw.Write(WireBssFreq);
            bbw.Write(5180u);

            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bbw.Write(WireBssStatus);
            bbw.Write(1u);

            // Wire attr 11 = BEACON_IES (arbitrary raw bytes)
            byte[] rawBeaconIes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + rawBeaconIes.Length));
            bbw.Write(WireBssBeaconIes);
            bbw.Write(rawBeaconIes);
            WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + rawBeaconIes.Length);
        }
        byte[] bssBytes = bssMs.ToArray();

        var msgMs = new MemoryStream();
        using (var bw = new BinaryWriter(msgMs))
        {
            int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 8) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);

            bw.Write(totalLen);
            bw.Write(wireFamilyId);
            bw.Write((ushort)0);
            bw.Write(seq);
            bw.Write((uint)0);

            bw.Write((byte)34);
            bw.Write((byte)1);
            bw.Write((ushort)0);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrIfIndex);
            bw.Write(wireIfIndex);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            bw.Write(WireAttrWdev);
            bw.Write(wireWdev);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrGeneration);
            bw.Write(wireGeneration);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length));
            bw.Write((ushort)(WireAttrBss | 0x8000));
            bw.Write(bssBytes);
            WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);
        }

        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(msgMs.ToArray(), done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, wireFamilyId, wireIfIndex, wireWdev);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        // Wire attr 11 must NEVER become SeenMsAgo!
        Assert.Null(res.Items[0].SeenMsAgo);
    }

    [Fact]
    public void Nl80211Protocol_GoldenWire_LiteralAttr15_Decodes_As_LastSeenBootTime()
    {
        uint seq = 503;
        const ushort wireFamilyId = 28;
        const int wireIfIndex = 3;
        const ulong wireWdev = 0x1000UL;
        const uint wireGeneration = 100u;
        const ushort WireAttrIfIndex = 3;
        const ushort WireAttrWdev = 153;
        const ushort WireAttrGeneration = 46;
        const ushort WireAttrBss = 47;

        const ushort WireBssBssid = 1;
        const ushort WireBssFreq = 2;
        const ushort WireBssLastSeenBootTime = 15;

        var bssMs = new MemoryStream();
        using (var bbw = new BinaryWriter(bssMs))
        {
            byte[] bssid = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 6));
            bbw.Write(WireBssBssid);
            bbw.Write(bssid);
            WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + 6);

            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bbw.Write(WireBssFreq);
            bbw.Write(5180u);

            // Wire attr 15 = LAST_SEEN_BOOTTIME (8 bytes ulong)
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            bbw.Write(WireBssLastSeenBootTime);
            bbw.Write(987654321000UL);
        }
        byte[] bssBytes = bssMs.ToArray();

        var msgMs = new MemoryStream();
        using (var bw = new BinaryWriter(msgMs))
        {
            int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 8) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);

            bw.Write(totalLen);
            bw.Write(wireFamilyId);
            bw.Write((ushort)0);
            bw.Write(seq);
            bw.Write((uint)0);

            bw.Write((byte)34);
            bw.Write((byte)1);
            bw.Write((ushort)0);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrIfIndex);
            bw.Write(wireIfIndex);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            bw.Write(WireAttrWdev);
            bw.Write(wireWdev);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrGeneration);
            bw.Write(wireGeneration);

            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length));
            bw.Write((ushort)(WireAttrBss | 0x8000));
            bw.Write(bssBytes);
            WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);
        }

        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(msgMs.ToArray(), done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, wireFamilyId, wireIfIndex, wireWdev);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
        Assert.Equal(987654321000UL, res.Items[0].LastSeenBootTimeNs);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Missing_IfIndex_Rejected()
    {
        uint seq = 504;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, omitIfindex: true);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Missing_Wdev_Rejected()
    {
        uint seq = 505;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, omitWdev: true);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Wdev_Mismatch_Rejected()
    {
        uint seq = 506;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, wdev: 0x2000UL);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, expectedWdev: 0x1000UL);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Missing_Generation_Rejected()
    {
        uint seq = 507;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, omitGeneration: true);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Generation_Shift_Across_Dump_Yields_Interrupted()
    {
        uint seq = 508;
        byte[] bssid1 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssid2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };

        var bss1 = BuildMockBssRecord(seq, 28, 3, bssid1, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, generation: 100u);
        var bss2 = BuildMockBssRecord(seq, 28, 3, bssid2, 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, generation: 101u); // shifted generation!
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss1, bss2, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Interrupted, res.Status);
    }

    [Fact]
    public void GenlProtocol_TryEnumerateAttributesStrict_Rejects_Aligned_Boundary_Overflow()
    {
        // Attribute with nla_len = 6 in a payload of length 6.
        // NlaAlign(6) = 8 > 6, so aligned boundary overflows payload.
        byte[] payload = new byte[6];
        MemoryMarshal.Write(payload.AsSpan(0, 2), (ushort)6);
        MemoryMarshal.Write(payload.AsSpan(2, 2), (ushort)1);
        payload[4] = 0xAA;
        payload[5] = 0xBB;

        bool success = LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var attrs);
        Assert.False(success);
        Assert.Empty(attrs);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociationObservation_Empty_Dump_Same_Interface_Identity_Yields_NotAssociated()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        Assert.Equal(LinuxWirelessAssociationState.NotAssociated, obs.State);
        Assert.Empty(obs.Links);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociationObservation_Empty_Dump_Changed_Interface_Identity_Yields_Unknown()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        // When continuity t1 query happens, interface has changed Wdev / replacement
        mockSocket.ContinuityInterfaceOverride = new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x2000UL);

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        Assert.Equal(LinuxWirelessAssociationState.Unknown, obs.State);
    }

    private static byte[] BuildMockBssRecord(
        uint seq,
        ushort familyId,
        int ifindex,
        byte[] bssid,
        uint freq,
        uint? status,
        int? signalMbm = null,
        byte? signalUnspec = null,
        byte[]? ssid = null,
        byte? mloLinkId = null,
        byte[]? mldAddr = null,
        ulong? wdev = 0x1000UL,
        uint? generation = 100u,
        bool corruptNlattr = false,
        bool corruptSsidIe = false,
        byte[]? customBssBytes = null,
        bool omitIfindex = false,
        bool omitWdev = false,
        bool omitGeneration = false)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // Build nested NL80211_ATTR_BSS
        byte[] bssBytes;
        if (customBssBytes != null)
        {
            bssBytes = customBssBytes;
        }
        else
        {
            var bssMs = new MemoryStream();
            using var bbw = new BinaryWriter(bssMs);

            // 1. NL80211_BSS_BSSID
            if (bssid != null)
            {
                bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + bssid.Length));
                bbw.Write(LinuxNl80211Protocol.NL80211_BSS_BSSID);
                bbw.Write(bssid);
                WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + bssid.Length);
            }

            // 2. NL80211_BSS_FREQUENCY
            bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bbw.Write(LinuxNl80211Protocol.NL80211_BSS_FREQUENCY);
            bbw.Write(freq);

            // 3. NL80211_BSS_STATUS
            if (status.HasValue)
            {
                bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                bbw.Write(LinuxNl80211Protocol.NL80211_BSS_STATUS);
                bbw.Write(status.Value);
            }

            // 4. NL80211_BSS_SIGNAL_MBM
            if (signalMbm.HasValue)
            {
                bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                bbw.Write(LinuxNl80211Protocol.NL80211_BSS_SIGNAL_MBM);
                bbw.Write(signalMbm.Value);
            }

            // 5. NL80211_BSS_SIGNAL_UNSPEC
            if (signalUnspec.HasValue)
            {
                bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
                bbw.Write(LinuxNl80211Protocol.NL80211_BSS_SIGNAL_UNSPEC);
                bbw.Write(signalUnspec.Value);
                WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + 1);
            }

            // 6. NL80211_BSS_MLO_LINK_ID
            if (mloLinkId.HasValue)
            {
                bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
                bbw.Write(LinuxNl80211Protocol.NL80211_BSS_MLO_LINK_ID);
                bbw.Write(mloLinkId.Value);
                WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + 1);
            }

            // 7. NL80211_BSS_MLD_ADDR
            if (mldAddr != null)
            {
                bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + mldAddr.Length));
                bbw.Write(LinuxNl80211Protocol.NL80211_BSS_MLD_ADDR);
                bbw.Write(mldAddr);
                WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + mldAddr.Length);
            }

            // 8. NL80211_BSS_INFORMATION_ELEMENTS (SSID IE)
            if (ssid != null || corruptSsidIe)
            {
                var ieMs = new MemoryStream();
                if (corruptSsidIe)
                {
                    ieMs.WriteByte(0); // EID 0 (SSID)
                    ieMs.WriteByte(20); // Length 20 but only 4 bytes follow
                    ieMs.Write(new byte[] { 1, 2, 3, 4 });
                }
                else
                {
                    ieMs.WriteByte(0); // EID 0 (SSID)
                    ieMs.WriteByte((byte)ssid!.Length);
                    ieMs.Write(ssid);
                }
                var ieBytes = ieMs.ToArray();

                bbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + ieBytes.Length));
                bbw.Write(LinuxNl80211Protocol.NL80211_BSS_INFORMATION_ELEMENTS);
                bbw.Write(ieBytes);
                WritePadding(bbw, LinuxGenlProtocol.NlaHeaderSize + ieBytes.Length);
            }

            if (corruptNlattr)
            {
                bbw.Write((ushort)2); // invalid nlaLen < 4
                bbw.Write((ushort)99);
            }

            bssBytes = bssMs.ToArray();
        }

        int ifindexAttrLen = (!omitIfindex) ? LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) : 0;
        int wdevAttrLen = (!omitWdev && wdev.HasValue) ? LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 8) : 0;
        int genAttrLen = (!omitGeneration && generation.HasValue) ? LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) : 0;
        int bssAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);

        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize +
                       ifindexAttrLen + wdevAttrLen + genAttrLen + bssAttrLen;

        // nlmsghdr
        bw.Write(totalLen);
        bw.Write(familyId);
        bw.Write((ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);

        // genlmsghdr
        bw.Write(LinuxNl80211Protocol.NL80211_CMD_NEW_SCAN_RESULTS);
        bw.Write((byte)1);
        bw.Write((ushort)0);

        // NL80211_ATTR_IFINDEX
        if (!omitIfindex)
        {
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
            bw.Write(ifindex);
        }

        // NL80211_ATTR_WDEV
        if (!omitWdev && wdev.HasValue)
        {
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            bw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
            bw.Write(wdev.Value);
        }

        // NL80211_ATTR_GENERATION
        if (!omitGeneration && generation.HasValue)
        {
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(LinuxNl80211Protocol.NL80211_ATTR_GENERATION);
            bw.Write(generation.Value);
        }

        // NL80211_ATTR_BSS (nested)
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length));
        bw.Write((ushort)(LinuxNl80211Protocol.NL80211_ATTR_BSS | 0x8000));
        bw.Write(bssBytes);
        WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + bssBytes.Length);

        return ms.ToArray();
    }

    private static byte[] CombineBuffers(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var part in parts)
        {
            ms.Write(part, 0, part.Length);
        }
        return ms.ToArray();
    }

    private static byte[] BuildMockInterfacePayload(uint seq, int ifindex, string ifname, uint? wiphy, int iftype, bool isInterrupted = false)
    {
        return BuildMockInterfaceResponse(seq, ifindex, ifname, wiphy, iftype, isInterrupted, includeDone: false);
    }

    private static byte[] BuildMockDoneMessage(uint seq, int error = 0, bool isInterrupted = false)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(20); // 16 header + 4 error
        bw.Write(LinuxGenlProtocol.NLMSG_DONE);
        bw.Write(isInterrupted ? LinuxGenlProtocol.NLM_F_DUMP_INTR : (ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);
        bw.Write(error);
        return ms.ToArray();
    }

    private static byte[] BuildMockInterfaceResponse(
        uint seq,
        int ifindex,
        string ifname,
        uint? wiphy,
        int iftype,
        bool isInterrupted = false,
        bool includeDone = false,
        bool isInterruptedDone = false,
        ulong? wdev = 0x1000UL)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] nameBytes = Encoding.UTF8.GetBytes(ifname);
        int nameAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);
        int ifindexAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
        int wiphyAttrLen = wiphy.HasValue ? LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) : 0;
        int iftypeAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
        int wdevAttrLen = wdev.HasValue ? LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 8) : 0;

        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize +
                       ifindexAttrLen + nameAttrLen + wiphyAttrLen + iftypeAttrLen + wdevAttrLen;

        ushort flags = isInterrupted ? LinuxGenlProtocol.NLM_F_DUMP_INTR : (ushort)0;

        // 1. nlmsghdr
        bw.Write(totalLen);
        bw.Write((ushort)28); // nl80211 family id
        bw.Write(flags);
        bw.Write(seq);
        bw.Write((uint)0);

        // 2. genlmsghdr
        bw.Write(LinuxNl80211Protocol.NL80211_CMD_NEW_INTERFACE);
        bw.Write((byte)1);
        bw.Write((ushort)0);

        // 3. NL80211_ATTR_IFINDEX
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
        bw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
        bw.Write(ifindex);

        // 4. NL80211_ATTR_IFNAME
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1));
        bw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFNAME);
        bw.Write(nameBytes);
        bw.Write((byte)0);
        WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);

        // 5. NL80211_ATTR_WIPHY (if present)
        if (wiphy.HasValue)
        {
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(LinuxNl80211Protocol.NL80211_ATTR_WIPHY);
            bw.Write(wiphy.Value);
        }

        // 6. NL80211_ATTR_IFTYPE
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
        bw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFTYPE);
        bw.Write(iftype);

        // 7. NL80211_ATTR_WDEV (if present)
        if (wdev.HasValue)
        {
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            bw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
            bw.Write(wdev.Value);
        }

        // Optional NLMSG_DONE
        if (includeDone)
        {
            bw.Write(20); // 16 header + 4 error
            bw.Write(LinuxGenlProtocol.NLMSG_DONE);
            bw.Write(isInterruptedDone ? LinuxGenlProtocol.NLM_F_DUMP_INTR : (ushort)0);
            bw.Write(seq);
            bw.Write((uint)0);
            bw.Write(0); // error == 0
        }

        return ms.ToArray();
    }

    private static byte[] BuildMockNlmsgError(uint seq, int errorCode)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + 4; // error int

        bw.Write(totalLen);
        bw.Write(LinuxGenlProtocol.NLMSG_ERROR);
        bw.Write((ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);

        bw.Write(errorCode);

        return ms.ToArray();
    }

    private static void WritePadding(BinaryWriter bw, int len)
    {
        int pad = LinuxGenlProtocol.NlaAlign(len) - len;
        for (int i = 0; i < pad; i++)
        {
            bw.Write((byte)0);
        }
    }

    private sealed class MockLinuxNl80211Socket : ILinuxNl80211Socket
    {
        private readonly Dictionary<string, GenlFamilyInfo> _families = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<LinuxNl80211InterfaceInfo> _interfaces = new();
        private readonly List<LinuxNl80211WiphyInfo> _wiphys = new();
        private readonly Dictionary<int, List<LinuxNl80211BssInfo>> _bssRecords = new();
        private int _dumpInterfacesCallCount = 0;

        public LinuxNl80211DumpStatus InterfaceDumpStatus { get; set; } = LinuxNl80211DumpStatus.Complete;
        public LinuxNl80211DumpStatus WiphyDumpStatus { get; set; } = LinuxNl80211DumpStatus.Complete;
        public LinuxNl80211DumpStatus BssDumpStatus { get; set; } = LinuxNl80211DumpStatus.Complete;

        public LinuxNl80211InterfaceInfo? ContinuityInterfaceOverride { get; set; }

        public void AddFamily(string name, ushort id) => _families[name] = new GenlFamilyInfo(id, name, 1, 0, 0, new Dictionary<string, uint>());
        public void AddInterface(LinuxNl80211InterfaceInfo ifinfo) => _interfaces.Add(ifinfo);
        public void AddWiphy(LinuxNl80211WiphyInfo winfo) => _wiphys.Add(winfo);

        public void AddBss(int ifindex, LinuxNl80211BssInfo bss)
        {
            if (!_bssRecords.TryGetValue(ifindex, out var list))
            {
                list = new List<LinuxNl80211BssInfo>();
                _bssRecords[ifindex] = list;
            }
            list.Add(bss);
        }

        public Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default)
        {
            _families.TryGetValue(familyName, out var fam);
            return Task.FromResult(fam);
        }

        public Task<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> DumpInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default)
        {
            _dumpInterfacesCallCount++;
            if (_dumpInterfacesCallCount > 1 && ContinuityInterfaceOverride != null)
            {
                var overridenList = new List<LinuxNl80211InterfaceInfo> { ContinuityInterfaceOverride };
                return Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(overridenList, InterfaceDumpStatus, 0, SawDone: true));
            }

            var list = ifindex.HasValue ? _interfaces.FindAll(i => i.IfIndex == ifindex.Value) : new List<LinuxNl80211InterfaceInfo>(_interfaces);
            var res = new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(list, InterfaceDumpStatus, InterfaceDumpStatus == LinuxNl80211DumpStatus.Complete ? 0 : -11, SawDone: InterfaceDumpStatus == LinuxNl80211DumpStatus.Complete);
            return Task.FromResult(res);
        }

        public Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default)
        {
            if (InterfaceDumpStatus != LinuxNl80211DumpStatus.Complete)
            {
                return Task.FromResult(new List<LinuxNl80211InterfaceInfo>());
            }
            if (ifindex.HasValue)
            {
                var match = _interfaces.FindAll(i => i.IfIndex == ifindex.Value);
                return Task.FromResult(match);
            }
            return Task.FromResult(new List<LinuxNl80211InterfaceInfo>(_interfaces));
        }

        public Task<LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>> DumpWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default)
        {
            var list = wiphyIndex.HasValue ? _wiphys.FindAll(w => w.WiphyIndex == wiphyIndex.Value) : new List<LinuxNl80211WiphyInfo>(_wiphys);
            var res = new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(list, WiphyDumpStatus, WiphyDumpStatus == LinuxNl80211DumpStatus.Complete ? 0 : -11, SawDone: WiphyDumpStatus == LinuxNl80211DumpStatus.Complete);
            return Task.FromResult(res);
        }

        public Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default)
        {
            if (WiphyDumpStatus != LinuxNl80211DumpStatus.Complete)
            {
                return Task.FromResult(new List<LinuxNl80211WiphyInfo>());
            }
            if (wiphyIndex.HasValue)
            {
                var match = _wiphys.FindAll(w => w.WiphyIndex == wiphyIndex.Value);
                return Task.FromResult(match);
            }
            return Task.FromResult(new List<LinuxNl80211WiphyInfo>(_wiphys));
        }

        public Task<LinuxNl80211DumpResult<LinuxNl80211BssInfo>> DumpBssAsync(ushort nl80211FamilyId, int ifindex, ulong? expectedWdev = null, CancellationToken cancellationToken = default)
        {
            _bssRecords.TryGetValue(ifindex, out var list);
            var items = list ?? new List<LinuxNl80211BssInfo>();
            var res = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(items, BssDumpStatus, BssDumpStatus == LinuxNl80211DumpStatus.Complete ? 0 : -11, SawDone: BssDumpStatus == LinuxNl80211DumpStatus.Complete);
            return Task.FromResult(res);
        }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MockLinuxRfkillReader : ILinuxRfkillReader
    {
        private readonly Dictionary<uint, LinuxRfkillObservation> _obs = new();

        public void SetObservation(uint wiphy, LinuxRfkillObservation obs) => _obs[wiphy] = obs;

        public LinuxRfkillObservation? ReadObservationForWiphy(uint wiphyIndex, string? ifname = null)
        {
            _obs.TryGetValue(wiphyIndex, out var res);
            return res;
        }
    }
}
