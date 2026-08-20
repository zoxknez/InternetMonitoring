using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IEM.Linux.Time;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

public class LinuxWifiRadioTests
{
    private sealed class StubNativeClock : ILinuxNativeClock
    {
        public long CurrentBootTimeSec { get; set; } = 1000;
        public long CurrentBootTimeNsec { get; set; } = 0;

        public void GetTime(int clkId, out LinuxTimeSpec ts)
        {
            ts = new LinuxTimeSpec { TvSec = CurrentBootTimeSec, TvNsec = CurrentBootTimeNsec };
        }
    }
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, activeSeq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(bss, seq, expectedFamilyId: 28, expectedIfIndex: 3, expectedWdev: 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(ms.ToArray(), seq, expectedFamilyId: 28, expectedIfIndex: 3, expectedWdev: 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(bss, seq, expectedFamilyId: 28, expectedIfIndex: 3, expectedWdev: 0x1000UL);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status); // Invariant 261
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Complete_Zero_Entries_Yields_Complete_Zero_Count()
    {
        uint seq = 407;
        var done = BuildMockDoneMessage(seq, error: 0);

        var res = LinuxNl80211Protocol.ParseBssDump(done, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(bss1, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(bss, seq, 28, 3, 0x1000UL);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Malformed_Nlattr_Anywhere_Yields_Malformed()
    {
        uint seq = 414;
        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        var bssCorrupted = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, corruptNlattr: true);

        var res = LinuxNl80211Protocol.ParseBssDump(bssCorrupted, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

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
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

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
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

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
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid1 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssid2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid1, "00:11:22:33:44:01", null, "MloNet", 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, null, 0, null, null, null, null, null, null));
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid2, "00:11:22:33:44:02", null, "MloNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -5500, 90, null, 1, null, null, null, null, null, null));

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        Assert.Equal(LinuxWirelessAssociationState.Associated, obs.State);
        Assert.Equal(2, obs.Links.Count);

        // Phase 3.1-7B-5: Core projection preserves common SSID on MLO, but strictly sets Bssid=null, SignalQuality=null
        var assoc = radio.ReadAssociation("wlan0");
        Assert.NotNull(assoc);
        Assert.Equal("MloNet", assoc.Ssid);
        Assert.Null(assoc.Bssid);
        Assert.Null(assoc.SignalQuality);
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
    public async Task Nl80211Radio_ReadAssociationObservation_Null_Wdev_On_T0_Returns_Null()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        // Interface has no Wdev (Wdev == null)
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: null));

        using var radio = new LinuxNl80211Radio(mockSocket);

        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.Null(obs); // Insufficient evidence to proceed with BSS dump / association correlation

        var assoc = radio.ReadAssociation("wlan0");
        Assert.Null(assoc);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void Nl80211Protocol_ParseBssDump_Matching_Family_Short_Data_Frame_Yields_Malformed(int shortLen)
    {
        uint seq = 509;
        const ushort familyId = 28;
        const int ifindex = 3;
        const ulong wdev = 0x1000UL;

        // Construct short packet with nlmsgType == familyId, seq == expectedSeq, but length < 20 (no full genlmsghdr)
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(shortLen);
            bw.Write(familyId);
            bw.Write((ushort)0);
            bw.Write(seq);
            bw.Write((uint)0);
            // Write padding/partial genl header up to shortLen
            for (int i = 16; i < shortLen; i++)
            {
                bw.Write((byte)0);
            }
            WritePadding(bw, shortLen);
        }

        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(ms.ToArray(), done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, familyId, ifindex, wdev);
        Assert.False(res.IsComplete);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status); // Invariant 261: never silently ignored
    }

    [Fact]
    public void Nl80211Protocol_ParseBssDump_Valid_20_Byte_Header_Frame_Passes()
    {
        uint seq = 510;
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = BuildMockBssRecord(seq, 28, 3, bssid, 2412, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED);
        var done = BuildMockDoneMessage(seq, error: 0);
        var stream = CombineBuffers(bss, done);

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
        Assert.True(res.IsComplete);
        Assert.Single(res.Items);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

        var res = LinuxNl80211Protocol.ParseBssDump(stream, seq, 28, 3, 0x1000UL);
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

    [Fact]
    public void Nl80211Protocol_BuildGetStationRequest_Encodes_Exact_IfIndex_And_Mac_Attributes()
    {
        ushort familyId = 28;
        int ifindex = 3;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        uint seq = 601;

        byte[] req = LinuxNl80211Protocol.BuildGetStationRequest(familyId, ifindex, peerMac, seq);

        Assert.True(req.Length >= 20);
        int totalLen = MemoryMarshal.Read<int>(req.AsSpan(0, 4));
        Assert.Equal(req.Length, totalLen);

        ushort type = MemoryMarshal.Read<ushort>(req.AsSpan(4, 2));
        Assert.Equal(familyId, type);

        ushort flags = MemoryMarshal.Read<ushort>(req.AsSpan(6, 2));
        Assert.Equal(LinuxGenlProtocol.NLM_F_REQUEST | LinuxGenlProtocol.NLM_F_ACK, flags);
        Assert.Equal(0, flags & LinuxGenlProtocol.NLM_F_DUMP); // Never dump when exact peer known

        uint actualSeq = MemoryMarshal.Read<uint>(req.AsSpan(8, 4));
        Assert.Equal(seq, actualSeq);

        byte cmd = req[16];
        Assert.Equal(LinuxNl80211Protocol.NL80211_CMD_GET_STATION, cmd);

        var payload = req.AsSpan(20);
        Assert.True(LinuxGenlProtocol.TryEnumerateAttributesStrict(payload, out var attrs));
        Assert.Equal(2, attrs.Count);

        var ifAttr = attrs.First(a => a.Type == LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
        Assert.Equal(4, ifAttr.Value.Length);
        Assert.Equal(ifindex, MemoryMarshal.Read<int>(ifAttr.Value));

        var macAttr = attrs.First(a => a.Type == LinuxNl80211Protocol.NL80211_ATTR_MAC);
        Assert.Equal(6, macAttr.Value.Length);
        Assert.Equal(peerMac, macAttr.Value);
    }

    [Fact]
    public void Nl80211Protocol_BuildGetStationRequest_Rejects_Invalid_Mac_Length()
    {
        Assert.Throws<ArgumentException>(() =>
            LinuxNl80211Protocol.BuildGetStationRequest(28, 3, new byte[] { 1, 2, 3 }, 602));
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Valid_Response_Decodes_All_Fields()
    {
        uint seq = 603;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(
            seq, familyId, ifindex, peerMac,
            wdev: wdev,
            generation: 100u,
            signal: -65,
            signalAvg: -68,
            rxBytes: 1024000UL,
            txBytes: 512000UL,
            rxPackets: 1500u,
            txPackets: 800u,
            txRetries: 12u,
            txFailed: 2u,
            connectedTime: 3600u);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);

        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.Equal(ifindex, res.Item.IfIndex);
        Assert.Equal("00:11:22:33:44:55", res.Item.PeerMacString);
        Assert.Equal(100u, res.Item.Generation);
        Assert.Equal((sbyte)-65, res.Item.SignalDbm);
        Assert.Equal((sbyte)-68, res.Item.SignalAverageDbm);
        Assert.Equal(1024000UL, res.Item.RxBytes);
        Assert.Equal(512000UL, res.Item.TxBytes);
        Assert.Equal(1500u, res.Item.RxPackets);
        Assert.Equal(800u, res.Item.TxPackets);
        Assert.Equal(12u, res.Item.TxRetries);
        Assert.Equal(2u, res.Item.TxFailed);
        Assert.Equal(3600u, res.Item.ConnectedTimeSeconds);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Ack_Followed_By_Data_Succeeds()
    {
        uint seq = 604;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var ack = BuildMockNlmsgError(seq, errorCode: 0);
        var data = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, signal: -70);
        var stream = CombineBuffers(ack, data);

        var res = LinuxNl80211Protocol.ParseStationResponse(stream, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.True(res.SawAck);
        Assert.NotNull(res.Item);
        Assert.Equal((sbyte)-70, res.Item.SignalDbm);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Ack_Only_Yields_Incomplete()
    {
        uint seq = 605;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var ack = BuildMockNlmsgError(seq, errorCode: 0);

        var res = LinuxNl80211Protocol.ParseStationResponse(ack, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Incomplete, res.Status);
        Assert.True(res.SawAck);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Negative_NlmsgError_Yields_KernelError()
    {
        uint seq = 606;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var err = BuildMockNlmsgError(seq, errorCode: -2); // ENOENT

        var res = LinuxNl80211Protocol.ParseStationResponse(err, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.KernelError, res.Status);
        Assert.Equal(-2, res.ErrorCode);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void Nl80211Protocol_ParseStationResponse_Matching_Family_Short_Frame_Yields_Malformed(int shortLen)
    {
        uint seq = 607;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(shortLen);
            bw.Write(familyId);
            bw.Write((ushort)0);
            bw.Write(seq);
            bw.Write((uint)0);
            for (int i = 16; i < shortLen; i++) bw.Write((byte)0);
            WritePadding(bw, shortLen);
        }

        var res = LinuxNl80211Protocol.ParseStationResponse(ms.ToArray(), seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Wrong_IfIndex_Rejected()
    {
        uint seq = 608;
        ushort familyId = 28;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex: 4, peerMac, wdev: wdev); // expected 3, got 4

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, expectedIfIndex: 3, expectedWdev: wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Missing_IfIndex_Rejected()
    {
        uint seq = 609;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, omitIfindex: true);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Matching_Wdev_Accepted()
    {
        uint seq = 610;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0xABCD1234UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, expectedWdev: wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Mismatched_Wdev_Rejected()
    {
        uint seq = 611;
        ushort familyId = 28;
        int ifindex = 3;
        ulong requestedWdev = 0x1000UL;
        ulong responseWdev = 0x2000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: responseWdev);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, expectedWdev: requestedWdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Missing_Wdev_Rejected()
    {
        uint seq = 612;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, omitWdev: true);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Malformed_Wdev_Length_Rejected()
    {
        uint seq = 613;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, customWdevBytes: new byte[] { 1, 2, 3, 4 }); // 4 bytes instead of 8

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Wrong_Mac_Rejected()
    {
        uint seq = 614;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] expectedMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        byte[] returnedMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0xAA };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, returnedMac, wdev: wdev);

        // Invariant 257: Reply MAC must strictly match requested peer MAC
        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, expectedMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Missing_Mac_Rejected()
    {
        uint seq = 615;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, omitMac: true);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Malformed_Mac_Length_Rejected()
    {
        uint seq = 616;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] invalidMac = new byte[] { 0x00, 0x11, 0x22, 0x33 }; // 4 bytes instead of 6

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, invalidMac, wdev: wdev);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, new byte[] { 0, 1, 2, 3, 4, 5 });
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Missing_Generation_Rejected()
    {
        uint seq = 617;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, omitGeneration: true);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Malformed_StaInfo_Nla_Rejected()
    {
        uint seq = 618;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        byte[] badNla = new byte[] { 0x0A, 0x00, 0x01, 0x00, 0x01 }; // nla_len = 10, but buffer is 5 bytes
        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, customStaInfoBytes: badNla);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_TopLevel_Mlo_LinkId_And_MldAddr_Preserved()
    {
        uint seq = 619;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        byte[] mldAddr = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x00 };

        var staMsg = BuildMockStationRecord(
            seq, familyId, ifindex, peerMac,
            wdev: wdev,
            mloLinkId: 2,
            mldAddr: mldAddr);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.Equal((byte)2, res.Item.MloLinkId);
        Assert.Equal(mldAddr, res.Item.MldAddress);
        Assert.Equal("AA:BB:CC:DD:EE:00", res.Item.MldAddressString);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Malformed_MldAddr_Length_Rejected()
    {
        uint seq = 620;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var staMsg = BuildMockStationRecord(
            seq, familyId, ifindex, peerMac,
            wdev: wdev,
            mldAddr: new byte[] { 1, 2, 3 }); // 3 bytes instead of 6

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Signal_Signed_Byte_0xBC_Decodes_As_Minus68Dbm()
    {
        uint seq = 621;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        // 0xBC = 188 unsigned => (sbyte)0xBC == -68
        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, signal: unchecked((sbyte)0xBC));

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.Equal((sbyte)-68, res.Item.SignalDbm);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_SignalAvg_Signed_Byte_0x9C_Decodes_As_Minus100Dbm()
    {
        uint seq = 622;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        // 0x9C = 156 unsigned => (sbyte)0x9C == -100
        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, signalAvg: unchecked((sbyte)0x9C));

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.Equal((sbyte)-100, res.Item.SignalAverageDbm);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Counters_And_Bytes64_Exact_Widths()
    {
        uint seq = 623;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        ulong largeRx = 0x123456789ABCDEF0UL;
        ulong largeTx = 0x0FEDCBA987654321UL;

        var staMsg = BuildMockStationRecord(
            seq, familyId, ifindex, peerMac,
            wdev: wdev,
            rxBytes: largeRx,
            txBytes: largeTx,
            rxPackets: 999999u,
            txPackets: 888888u,
            txRetries: 777u,
            txFailed: 66u,
            connectedTime: 7200u);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.Equal(largeRx, res.Item.RxBytes);
        Assert.Equal(largeTx, res.Item.TxBytes);
        Assert.Equal(999999u, res.Item.RxPackets);
        Assert.Equal(888888u, res.Item.TxPackets);
        Assert.Equal(777u, res.Item.TxRetries);
        Assert.Equal(66u, res.Item.TxFailed);
        Assert.Equal(7200u, res.Item.ConnectedTimeSeconds);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Rate_Bitrate32_Preferred_Over_Bitrate16()
    {
        uint seq = 624;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        // Rate info containing BOTH BITRATE (16-bit = 540 => 54 Mbps) and BITRATE32 (32-bit = 12000 => 1200 Mbps)
        var rateMs = new MemoryStream();
        using (var rbw = new BinaryWriter(rateMs))
        {
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 2));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_BITRATE);
            rbw.Write((ushort)540);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 2);

            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_BITRATE32);
            rbw.Write(12000u);
        }

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, txRateBytes: rateMs.ToArray());

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.NotNull(res.Item.TxRate);

        // BITRATE32 preferred: 12000 * 100,000 = 1,200,000,000 bps (1.2 Gbps), NOT 54 Mbps, NOT added
        Assert.Equal(1_200_000_000UL, res.Item.TxRate.BitrateBps);
        Assert.Equal(12000u, res.Item.TxRate.Bitrate100Kbps);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Rate_Bitrate16_Fallback_When_Bitrate32_Missing()
    {
        uint seq = 625;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        // Rate info containing ONLY BITRATE (16-bit = 540 => 54 Mbps)
        var rateMs = new MemoryStream();
        using (var rbw = new BinaryWriter(rateMs))
        {
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 2));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_BITRATE);
            rbw.Write((ushort)540);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 2);
        }

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, txRateBytes: rateMs.ToArray());

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.NotNull(res.Item.TxRate);

        // Fallback to 16-bit BITRATE: 540 * 100,000 = 54,000,000 bps
        Assert.Equal(54_000_000UL, res.Item.TxRate.BitrateBps);
        Assert.Equal(540u, res.Item.TxRate.Bitrate100Kbps);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Rate_Without_Bitrate_Yields_Null_BitrateBps()
    {
        uint seq = 626;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        // Rate info with MCS=7 and VHT_NSS=2, but NO BITRATE or BITRATE32
        var rateMs = new MemoryStream();
        using (var rbw = new BinaryWriter(rateMs))
        {
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_MCS);
            rbw.Write((byte)7);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 1);

            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_VHT_NSS);
            rbw.Write((byte)2);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 1);
        }

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, txRateBytes: rateMs.ToArray());

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.NotNull(res.Item.TxRate);

        // Missing rate attributes must produce null BitrateBps, not synthetic 0
        Assert.Null(res.Item.TxRate.BitrateBps);
        Assert.Null(res.Item.TxRate.Bitrate100Kbps);
        Assert.Equal((byte)7, res.Item.TxRate.Mcs);
        Assert.Equal((byte)2, res.Item.TxRate.VhtNss);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Rate_Width_Flag_With_Payload_Rejected()
    {
        uint seq = 627;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        // 320_MHZ_WIDTH flag with unexpected 1-byte payload -> must be rejected (NLA flag must be 0 payload)
        var rateMs = new MemoryStream();
        using (var rbw = new BinaryWriter(rateMs))
        {
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_320_MHZ_WIDTH);
            rbw.Write((byte)1);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 1);
        }

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, txRateBytes: rateMs.ToArray());

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Rate_Conflicting_Width_Flags_Rejected()
    {
        uint seq = 628;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        // Both 80_MHZ_WIDTH and 160_MHZ_WIDTH set simultaneously -> conflicting, reject as Malformed
        var rateMs = new MemoryStream();
        using (var rbw = new BinaryWriter(rateMs))
        {
            rbw.Write((ushort)LinuxGenlProtocol.NlaHeaderSize);
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_80_MHZ_WIDTH);

            rbw.Write((ushort)LinuxGenlProtocol.NlaHeaderSize);
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_160_MHZ_WIDTH);
        }

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, txRateBytes: rateMs.ToArray());

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Rate_Malformed_Rate_Nla_Rejects_Station()
    {
        uint seq = 629;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        byte[] malformedRate = new byte[] { 0x0A, 0x00, 0x01, 0x00, 0x01 }; // nla_len 10 in 5 bytes buffer
        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, txRateBytes: malformedRate);

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.False(res.IsSuccess);
        Assert.Equal(LinuxNl80211DumpStatus.Malformed, res.Status);
    }

    [Fact]
    public void Nl80211Protocol_ParseStationResponse_Rate_Preserves_He_And_Eht_Fields()
    {
        uint seq = 630;
        ushort familyId = 28;
        int ifindex = 3;
        ulong wdev = 0x1000UL;
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };

        var rateMs = new MemoryStream();
        using (var rbw = new BinaryWriter(rateMs))
        {
            // BITRATE32
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_BITRATE32);
            rbw.Write(24000u);

            // EHT_MCS = 11
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_EHT_MCS);
            rbw.Write((byte)11);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 1);

            // EHT_NSS = 2
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_EHT_NSS);
            rbw.Write((byte)2);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 1);

            // 320_MHZ_WIDTH (flag)
            rbw.Write((ushort)LinuxGenlProtocol.NlaHeaderSize);
            rbw.Write(LinuxNl80211Protocol.NL80211_RATE_INFO_320_MHZ_WIDTH);
        }

        var staMsg = BuildMockStationRecord(seq, familyId, ifindex, peerMac, wdev: wdev, txRateBytes: rateMs.ToArray());

        var res = LinuxNl80211Protocol.ParseStationResponse(staMsg, seq, familyId, ifindex, wdev, peerMac);
        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.NotNull(res.Item.TxRate);

        Assert.Equal(2_400_000_000UL, res.Item.TxRate.BitrateBps);
        Assert.Equal((byte)11, res.Item.TxRate.EhtMcs);
        Assert.Equal((byte)2, res.Item.TxRate.EhtNss);
        Assert.True(res.Item.TxRate.Is320Mhz);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociationObservation_Station_Enoent_Leaves_Association_Associated()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid, "AA:BB:CC:DD:EE:FF", Encoding.UTF8.GetBytes("HomeNet"), "HomeNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, 80, null, null, null, null, null, null, 0x1000UL, 100u));

        // GET_STATION returns ENOENT (-2)
        mockSocket.StationStatus = LinuxNl80211DumpStatus.KernelError;

        using var radio = new LinuxNl80211Radio(mockSocket);

        // 1. Association observation remains Associated (BSS is the association authority)
        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        Assert.Equal(LinuxWirelessAssociationState.Associated, obs.State);
        Assert.Single(obs.Links);

        // 2. Station metadata returns null (Unknown) without breaking association
        var sta = await radio.ReadStationInfoAsync(3, 0x1000UL, bssid);
        Assert.Null(sta);
    }

    [Fact]
    public async Task Nl80211Radio_ReadStationInfo_Station_Mac_Mismatch_Returns_Null()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };

        // Mock has station info for B, caller requests station info for A
        mockSocket.AddStation(3, 0x1000UL, bssidB, new LinuxNl80211StationInfo(3, bssidB, "00:11:22:33:44:02", 100u, -60, -62, 100, 200, 10, 20, 0, 0, 100, null, null, null, null, null, null, Links: Array.Empty<LinuxNl80211LinkStationInfo>()));

        using var radio = new LinuxNl80211Radio(mockSocket);

        var sta = await radio.ReadStationInfoAsync(3, 0x1000UL, bssidA);
        Assert.Null(sta); // Invariant 257: MAC mismatch rejected
    }

    [Fact]
    public async Task Nl80211Radio_ReadStationInfo_CorrelationToken_EndToEnd_Match_Succeeds()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var staExpected = new LinuxNl80211StationInfo(3, bssid, "00:11:22:33:44:55", 100u, -55, -58, 2048, 1024, 20, 10, 0, 0, 300, null, null, null, null, null, null, Links: Array.Empty<LinuxNl80211LinkStationInfo>());
        mockSocket.AddStation(3, 0x1000UL, bssid, staExpected);

        var token = new LinuxNl80211StationCorrelationToken(
            IfIndex: 3,
            Wdev: 0x1000UL,
            WiphyIndex: 0,
            PeerMac: bssid,
            PeerMacString: "00:11:22:33:44:55",
            BssGeneration: 50u);

        using var radio = new LinuxNl80211Radio(mockSocket);

        var sta = await radio.ReadStationInfoAsync(token);
        Assert.NotNull(sta);
        Assert.Equal((sbyte)-55, sta.SignalDbm);
        Assert.Equal(3, sta.IfIndex);
        Assert.Equal("00:11:22:33:44:55", sta.PeerMacString);
    }

    [Fact]
    public async Task Nl80211Radio_ReadStationInfo_Wdev_Mismatch_Leaves_Association_Intact_And_Station_Null()
    {
        var mockSocket = new MockLinuxNl80211Socket();
        mockSocket.AddFamily("nl80211", 28);
        mockSocket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
        mockSocket.AddBss(3, new LinuxNl80211BssInfo(3, bssid, "AA:BB:CC:DD:EE:FF", Encoding.UTF8.GetBytes("HomeNet"), "HomeNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, 80, null, null, null, null, null, null, 0x1000UL, 100u));

        // Station is registered with WDEV 0x2000UL in mock, but token requests 0x1000UL
        mockSocket.AddStation(3, 0x2000UL, bssid, new LinuxNl80211StationInfo(3, bssid, "AA:BB:CC:DD:EE:FF", 100u, -50, -50, 100, 100, 1, 1, 0, 0, 10, null, null, null, null, null, null, Links: Array.Empty<LinuxNl80211LinkStationInfo>()));

        using var radio = new LinuxNl80211Radio(mockSocket);

        // Association remains Associated
        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);
        Assert.Equal(LinuxWirelessAssociationState.Associated, obs.State);

        // Station query with token (Wdev 0x1000UL) fails to match 0x2000UL and returns null
        var token = new LinuxNl80211StationCorrelationToken(3, 0x1000UL, 0, bssid, "AA:BB:CC:DD:EE:FF", 100u);
        var sta = await radio.ReadStationInfoAsync(token);
        Assert.Null(sta);
    }

    [Fact]
    public void Nl80211Protocol_StationCorrelationToken_Preserves_Context_Tuple()
    {
        byte[] peerMac = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var token = new LinuxNl80211StationCorrelationToken(
            IfIndex: 3,
            Wdev: 0x1000UL,
            WiphyIndex: 0,
            PeerMac: peerMac,
            PeerMacString: "00:11:22:33:44:55",
            BssGeneration: 100u);

        Assert.Equal(3, token.IfIndex);
        Assert.Equal(0x1000UL, token.Wdev);
        Assert.Equal(0u, token.WiphyIndex);
        Assert.Equal(peerMac, token.PeerMac);
        Assert.Equal("00:11:22:33:44:55", token.PeerMacString);
        Assert.Equal(100u, token.BssGeneration);
    }

    [Fact]
    public void Nl80211Protocol_GoldenWire_Literal_StaInfo_And_RateInfo_Attributes()
    {
        uint seq = 631;
        const ushort wireFamilyId = 28;
        const int wireIfIndex = 3;
        const ulong wireWdev = 0x1000UL;
        byte[] wireMac = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
        const uint wireGeneration = 100u;

        // Top-level wire literals
        const ushort WireAttrIfIndex = 3;
        const ushort WireAttrMac = 6;
        const ushort WireAttrStaInfo = 21;
        const ushort WireAttrGeneration = 46;
        const ushort WireAttrWdev = 153;

        // StaInfo wire literals
        const ushort WireStaInfoSignal = 7;
        const ushort WireStaInfoTxBitrate = 8;
        const ushort WireStaInfoSignalAvg = 13;
        const ushort WireStaInfoConnectedTime = 16;
        const ushort WireStaInfoRxBytes64 = 23;
        const ushort WireStaInfoTxBytes64 = 24;

        // RateInfo wire literals
        const ushort WireRateInfoBitrate32 = 5;
        const ushort WireRateInfoHeMcs = 13;
        const ushort WireRateInfoHeNss = 14;
        const ushort WireRateInfo160Mhz = 10;

        // Build rate bytes
        var rateMs = new MemoryStream();
        using (var rbw = new BinaryWriter(rateMs))
        {
            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            rbw.Write(WireRateInfoBitrate32);
            rbw.Write(16000u); // 1.6 Gbps

            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            rbw.Write(WireRateInfoHeMcs);
            rbw.Write((byte)10);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 1);

            rbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            rbw.Write(WireRateInfoHeNss);
            rbw.Write((byte)2);
            WritePadding(rbw, LinuxGenlProtocol.NlaHeaderSize + 1);

            rbw.Write((ushort)LinuxGenlProtocol.NlaHeaderSize);
            rbw.Write(WireRateInfo160Mhz);
        }
        byte[] rateBytes = rateMs.ToArray();

        // Build sta info bytes
        var staMs = new MemoryStream();
        using (var sbw = new BinaryWriter(staMs))
        {
            // Signal = 0xBC (-68)
            sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            sbw.Write(WireStaInfoSignal);
            sbw.Write((byte)0xBC);
            WritePadding(sbw, LinuxGenlProtocol.NlaHeaderSize + 1);

            // SignalAvg = 0xBA (-70)
            sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
            sbw.Write(WireStaInfoSignalAvg);
            sbw.Write((byte)0xBA);
            WritePadding(sbw, LinuxGenlProtocol.NlaHeaderSize + 1);

            // RxBytes64
            sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            sbw.Write(WireStaInfoRxBytes64);
            sbw.Write(8888888888UL);

            // TxBytes64
            sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            sbw.Write(WireStaInfoTxBytes64);
            sbw.Write(4444444444UL);

            // ConnectedTime = 1200
            sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            sbw.Write(WireStaInfoConnectedTime);
            sbw.Write(1200u);

            // TxBitrate (nested)
            sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + rateBytes.Length));
            sbw.Write(WireStaInfoTxBitrate);
            sbw.Write(rateBytes);
            WritePadding(sbw, LinuxGenlProtocol.NlaHeaderSize + rateBytes.Length);
        }
        byte[] staBytes = staMs.ToArray();

        // Build top-level message
        var msgMs = new MemoryStream();
        using (var bw = new BinaryWriter(msgMs))
        {
            int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 8) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 6) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4) +
                           LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + staBytes.Length);

            bw.Write(totalLen);
            bw.Write(wireFamilyId);
            bw.Write((ushort)0);
            bw.Write(seq);
            bw.Write((uint)0);

            bw.Write((byte)19); // NL80211_CMD_NEW_STATION = 19
            bw.Write((byte)1);
            bw.Write((ushort)0);

            // IFINDEX
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrIfIndex);
            bw.Write(wireIfIndex);

            // WDEV
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
            bw.Write(WireAttrWdev);
            bw.Write(wireWdev);

            // MAC
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 6));
            bw.Write(WireAttrMac);
            bw.Write(wireMac);
            WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + 6);

            // GENERATION
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
            bw.Write(WireAttrGeneration);
            bw.Write(wireGeneration);

            // STA_INFO
            bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + staBytes.Length));
            bw.Write(WireAttrStaInfo);
            bw.Write(staBytes);
            WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + staBytes.Length);
        }

        var res = LinuxNl80211Protocol.ParseStationResponse(msgMs.ToArray(), seq, wireFamilyId, wireIfIndex, wireWdev, wireMac);

        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Item);
        Assert.Equal((sbyte)-68, res.Item.SignalDbm);
        Assert.Equal((sbyte)-70, res.Item.SignalAverageDbm);
        Assert.Equal(8888888888UL, res.Item.RxBytes);
        Assert.Equal(4444444444UL, res.Item.TxBytes);
        Assert.Equal(1200u, res.Item.ConnectedTimeSeconds);
        Assert.NotNull(res.Item.TxRate);
        Assert.Equal(1_600_000_000UL, res.Item.TxRate.BitrateBps);
        Assert.Equal((byte)10, res.Item.TxRate.HeMcs);
        Assert.Equal((byte)2, res.Item.TxRate.HeNss);
        Assert.True(res.Item.TxRate.Is160Mhz);
    }

    #region Phase 3.1-7B-3: Association Composition Tests

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Associated_Station_Valid_Continuity_Valid()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        var sta = new LinuxNl80211StationInfo(3, bssid, "00:11:22:33:44:55", 100u, -65, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        socket.AddStation(3, 0x1000UL, bssid, sta);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Single(res.Links);
        Assert.Equal("00:11:22:33:44:55", res.Links[0].Bssid);
        Assert.NotNull(res.StationInfo);
        Assert.Equal((sbyte)-65, res.StationInfo.SignalDbm);
        Assert.Equal(1, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Associated_Station_Enoent()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);
        // No station added -> GetStationAsync will return ENOENT

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Single(res.Links);
        Assert.Null(res.StationInfo); // Station Unavailable never changes BSS Associated
        Assert.Equal(1, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Associated_Station_TimedOut()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);
        socket.QueueStationResponse(null, LinuxNl80211DumpStatus.TimedOut, -110);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Null(res.StationInfo);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Associated_Station_Malformed()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);
        socket.QueueStationResponse(null, LinuxNl80211DumpStatus.Malformed, -22);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Null(res.StationInfo);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Roam_To_New_AP_Attempt2_Stabilizes()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MyWiFi", 5200, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, null, null, null, null, null, 0x1000UL, 101u);

        var staA = new LinuxNl80211StationInfo(3, bssidA, "00:11:22:33:44:01", 100u, -65, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        var staB = new LinuxNl80211StationInfo(3, bssidB, "00:11:22:33:44:02", 101u, -60, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // Sequence:
        // Attempt 1:
        //   t0: bssA
        //   t1: staA
        //   t2: bssB (drift!)
        // Attempt 2:
        //   t0: bssB
        //   t1: staB
        //   t2: bssB (stable!)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA });
        socket.QueueStationResponse(staA);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB });

        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB });
        socket.QueueStationResponse(staB);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Single(res.Links);
        Assert.Equal("00:11:22:33:44:02", res.Links[0].Bssid);
        Assert.NotNull(res.StationInfo);
        Assert.Equal("00:11:22:33:44:02", res.StationInfo.PeerMacString);
        Assert.Equal((sbyte)-60, res.StationInfo.SignalDbm);
        Assert.Equal(2, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Interface_Wdev_Change_Triggers_Drift_Stabilizes_On_Second_Attempt()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);

        var if1 = new List<LinuxNl80211InterfaceInfo> { new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL) };
        var if2 = new List<LinuxNl80211InterfaceInfo> { new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x2000UL) };

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss1 = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        var bss2 = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x2000UL, 101u);

        var sta1 = new LinuxNl80211StationInfo(3, bssid, "00:11:22:33:44:55", 100u, -65, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        var sta2 = new LinuxNl80211StationInfo(3, bssid, "00:11:22:33:44:55", 101u, -60, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // Attempt 1:
        //   t0: interface wdev 0x1000, bss1 (wdev 0x1000, gen 100) -> sta1
        //   t2: interface wdev 0x2000, bss2 (wdev 0x2000, gen 101) => drift!
        socket.QueueInterfaceDump(if1);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss1 });
        socket.QueueStationResponse(sta1);
        socket.QueueInterfaceDump(if2);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });

        // Attempt 2:
        //   t0: interface wdev 0x2000, bss2 (wdev 0x2000, gen 101) -> sta2
        //   t2: interface wdev 0x2000, bss2 (wdev 0x2000, gen 101) => stable!
        socket.QueueInterfaceDump(if2);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });
        socket.QueueStationResponse(sta2);
        socket.QueueInterfaceDump(if2);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(0x2000UL, res.Wdev);
        Assert.NotNull(res.StationInfo);
        Assert.Equal((sbyte)-60, res.StationInfo.SignalDbm);
        Assert.Equal(2, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Interface_Wdev_Continuous_Drift_Returns_Freshest_Bss()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);

        var if1 = new List<LinuxNl80211InterfaceInfo> { new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL) };
        var if2 = new List<LinuxNl80211InterfaceInfo> { new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x2000UL) };
        var if3 = new List<LinuxNl80211InterfaceInfo> { new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x3000UL) };

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss1 = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        var bss2 = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x2000UL, 101u);
        var bss3 = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x3000UL, 102u);

        // Attempt 1: wdev 0x1000 -> wdev 0x2000
        socket.QueueInterfaceDump(if1);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss1 });
        socket.QueueStationResponse(null, LinuxNl80211DumpStatus.KernelError, -2);
        socket.QueueInterfaceDump(if2);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });

        // Attempt 2: wdev 0x2000 -> wdev 0x3000
        socket.QueueInterfaceDump(if2);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });
        socket.QueueStationResponse(null, LinuxNl80211DumpStatus.KernelError, -2);
        socket.QueueInterfaceDump(if3);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss3 });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.False(res.ContinuityVerified);
        Assert.Equal(0x3000UL, res.Wdev); // Freshest authoritative state returned
        Assert.Null(res.StationInfo);
        Assert.Equal(2, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Associated_Missing_Generation_No_Attribution()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        // Missing BSS generation (null)
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, null);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.False(res.ContinuityVerified);
        Assert.Null(res.Generation);
        Assert.Null(res.StationInfo);
        Assert.Equal(0, socket.GetStationCallCount); // Never synthesizes generation 0 or invokes station query without proven generation
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Bss_Incomplete_Yields_Unknown()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Incomplete, -11);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Unknown, res.State);
        Assert.False(res.ContinuityVerified);
        Assert.Null(res.StationInfo);
        Assert.Equal(0, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_NotAssociated_GetStation_Call_Count_Zero()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        // Complete BSS dump with 0 associated links
        socket.AddBss(3, new LinuxNl80211BssInfo(3, new byte[] { 1, 2, 3, 4, 5, 6 }, "01:02:03:04:05:06", null, "OtherWiFi", 2412, null, -7000, null, null, null, null, null, null, null, 0x1000UL, 100u));

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.NotAssociated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Null(res.StationInfo);
        Assert.Equal(0, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Mlo_MultiLink_Preserves_Links_And_Attaches_Station()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);

        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        socket.AddStation(3, 0x1000UL, mldAddr, sta);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(2, res.Links.Count);
        Assert.NotNull(res.StationInfo);
        Assert.Equal("00:11:22:33:44:00", res.StationInfo.PeerMacString);
        Assert.Equal(mldAddr, socket.LastRequestedPeerMac);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_BoundedRetry_Exactly_Two_Attempts_On_Continuous_Drift()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid1 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssid2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] bssid3 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x03 };
        byte[] bssid4 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x04 };

        var bss1 = new LinuxNl80211BssInfo(3, bssid1, "00:11:22:33:44:01", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        var bss2 = new LinuxNl80211BssInfo(3, bssid2, "00:11:22:33:44:02", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 101u);
        var bss3 = new LinuxNl80211BssInfo(3, bssid3, "00:11:22:33:44:03", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 102u);
        var bss4 = new LinuxNl80211BssInfo(3, bssid4, "00:11:22:33:44:04", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 103u);

        // Attempt 1: t0=bss1 -> sta1 -> t2=bss2 (drift!)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss1 });
        socket.QueueStationResponse(null, LinuxNl80211DumpStatus.KernelError, -2);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });

        // Attempt 2: t0=bss3 -> sta3 -> t2=bss4 (drift!)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss3 });
        socket.QueueStationResponse(null, LinuxNl80211DumpStatus.KernelError, -2);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss4 });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.False(res.ContinuityVerified);
        Assert.Null(res.StationInfo);
        Assert.Equal(2, socket.GetStationCallCount); // Exactly 2 attempts
        Assert.Equal("00:11:22:33:44:04", res.Links.Single().Bssid); // Terminal state is freshest snapshot
        Assert.Equal(103u, res.Generation);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Mlo_Unordered_Link_Equivalence_Passes_Continuity()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);

        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // t0: [bssA, bssB]
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssB });
        socket.QueueStationResponse(sta);
        // t2: [bssB, bssA] (reversed enumeration order)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB, bssA });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(2, res.Links.Count);
        Assert.NotNull(res.StationInfo);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Mlo_Link_Set_Change_Triggers_Drift()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] bssidC = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x03 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssC = new LinuxNl80211BssInfo(3, bssidC, "00:11:22:33:44:03", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);

        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // Attempt 1: t0: [bssA, bssB] -> station -> t2: [bssA, bssC] (drift!)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssB });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssC });

        // Attempt 2: t0: [bssA, bssC] -> station -> t2: [bssA, bssC] (stable!)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssC });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssC });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(2, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Mlo_Queries_Mld_Address_Never_Link_Bssid()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);

        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        using var radio = new LinuxNl80211Radio(socket);
        await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.Equal(mldAddr, socket.LastRequestedPeerMac);
        Assert.NotEqual(bssidA, socket.LastRequestedPeerMac);
        Assert.NotEqual(bssidB, socket.LastRequestedPeerMac);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Mlo_Inconsistent_Mld_Does_Not_Guess_Station()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr1 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] mldAddr2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };

        // Two links with conflicting MLD addresses
        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr1, "00:11:22:33:44:01", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr2, "00:11:22:33:44:02", null, null, 0x1000UL, 100u);

        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.False(res.ContinuityVerified); // No t2 snapshot was compared, never fabricate ContinuityVerified = true
        Assert.Null(res.StationInfo);
        Assert.Equal(0, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Differing_Raw_Bytes_Triggers_Drift()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] rawBssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] rawBssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };

        var bssA = new LinuxNl80211BssInfo(3, rawBssidA, "same_display_mac", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, rawBssidB, "same_display_mac", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);

        var sta = new LinuxNl80211StationInfo(3, rawBssidA, "same_display_mac", 100u, -65, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // Attempt 1: t0 (raw A) -> sta -> t2 (raw B) => drift based on raw byte mismatch!
        // Attempt 2: t0 (raw B) -> sta -> t2 (raw B) => stable!
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB });

        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(2, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_NotAssociated_And_Unknown_Call_Count_Zero()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.NotAssociated, res.State);
        Assert.Equal(0, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Generation_Change_Triggers_Drift()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss1 = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        var bss2 = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 101u);

        var sta = new LinuxNl80211StationInfo(3, bssid, "00:11:22:33:44:55", 100u, -65, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // Attempt 1: t0 (gen 100) -> sta -> t2 (gen 101) => drift!
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss1 });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });

        // Attempt 2: t0 (gen 101) -> sta -> t2 (gen 101) => stable!
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss2 });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(2, socket.GetStationCallCount);
    }

    [Fact]
    public async Task Nl80211Radio_ReadComposedAssociation_Wiphy_Change_Triggers_Drift()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);

        var if0 = new List<LinuxNl80211InterfaceInfo> { new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL) };
        var if1 = new List<LinuxNl80211InterfaceInfo> { new LinuxNl80211InterfaceInfo(3, "wlan0", 1, "phy1", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL) };

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "MyWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        // Attempt 1: t0 (wiphy 0) -> sta -> t2 (wiphy 1) => drift!
        // Attempt 2: t0 (wiphy 1) -> sta -> t2 (wiphy 0) => drift!
        socket.QueueInterfaceDump(if0);
        socket.QueueInterfaceDump(if1);
        socket.QueueInterfaceDump(if1);
        socket.QueueInterfaceDump(if0);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.False(res.ContinuityVerified);
        Assert.Null(res.StationInfo);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociation_Remains_Pure_Bss_Authority_Without_Station_Query()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "PureBssWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket);
        var assoc = await radio.ReadAssociationAsync("wlan0");

        Assert.NotNull(assoc);
        Assert.Equal("PureBssWiFi", assoc.Ssid);
        Assert.Equal("00:11:22:33:44:55", assoc.Bssid);
        Assert.Equal(0, socket.GetStationCallCount); // Pure BSS authority never calls GetStationAsync
    }

    #endregion

    #region Phase 3.1-7B-4: Access Point Evidence Tests

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Exact_Ssid_And_Bssid_Returns_AccessPoint()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "HomeWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.NotNull(ap);
        Assert.Equal("00:11:22:33:44:55", ap.Bssid);
        Assert.Equal(36, ap.Channel);
        Assert.Equal(-65, ap.Rssi);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Same_Ssid_Stronger_Different_Bssid_Returns_Requested_Bssid()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MeshWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MeshWiFi", 5180, null, -3500, null, null, null, null, null, null, null, 0x1000UL, 100u);

        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("MeshWiFi", "00:11:22:33:44:01");

        Assert.NotNull(ap);
        Assert.Equal("00:11:22:33:44:01", ap.Bssid);
        Assert.Equal(-65, ap.Rssi); // Strictly requested BSSID, never loudest AP B (-35 dBm)
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Exact_Bssid_Different_Ssid_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "OtherWiFi", 5180, null, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("MyWiFi", "00:11:22:33:44:55");

        Assert.Null(ap);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Bssid_Absent_From_Complete_Cached_Dump_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:01", null, "MyWiFi", 5180, null, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("MyWiFi", "00:11:22:33:44:99");

        Assert.Null(ap);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Incomplete_Dump_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "HomeWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Incomplete, -11);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.Null(ap);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Malformed_Dump_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        socket.QueueBssDump(new List<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Malformed, -22);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.Null(ap);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_MultiAdapter_Never_Borrows_Other_Adapter()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));
        socket.AddInterface(new LinuxNl80211InterfaceInfo(4, "wlan1", 1, "phy1", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x2000UL));

        byte[] bssid2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        var bss2 = new LinuxNl80211BssInfo(4, bssid2, "00:11:22:33:44:02", null, "OtherWiFi", 5180, null, -6000, null, null, null, null, null, null, null, 0x2000UL, 100u);
        socket.AddBss(4, bss2);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("OtherWiFi", "00:11:22:33:44:02");

        Assert.Null(ap);
        Assert.Equal(3, socket.LastDumpBssIfIndex);
        Assert.Equal(0x1000UL, socket.LastDumpBssWdev);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Missing_Deterministic_Adapter_Scope_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "HomeWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        // Construct radio with null boundInterfaceId
        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: null);
        var ap = radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.Null(ap);
        Assert.Equal(0, socket.DumpInterfacesCallCount);
        Assert.Equal(0, socket.DumpBssCallCount);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_SignalMbm_Preserves_Real_Dbm()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "HomeWiFi", 5180, null, -6750, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.NotNull(ap);
        Assert.Equal(-67, ap.Rssi);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_SignalUnspec_Only_Yields_Rssi_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        // SignalMbm = null, SignalQuality = 80 (unspecified)
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "HomeWiFi", 5180, null, null, 80, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.NotNull(ap);
        Assert.Null(ap.Rssi); // Never fabricate dBm from unspecified quality
    }

    [Theory]
    [InlineData(2412u, 1)]
    [InlineData(2437u, 6)]
    [InlineData(2462u, 11)]
    [InlineData(2472u, 13)]
    [InlineData(2484u, 14)]
    public void Nl80211Radio_FrequencyMhzToChannel_24Ghz_Maps_Correctly(uint freq, int expectedChannel)
    {
        Assert.Equal(expectedChannel, LinuxNl80211Radio.FrequencyMhzToChannel(freq));
    }

    [Theory]
    [InlineData(5180u, 36)]
    [InlineData(5240u, 48)]
    [InlineData(5500u, 100)]
    [InlineData(5745u, 149)]
    public void Nl80211Radio_FrequencyMhzToChannel_5Ghz_Maps_Correctly(uint freq, int expectedChannel)
    {
        Assert.Equal(expectedChannel, LinuxNl80211Radio.FrequencyMhzToChannel(freq));
    }

    [Theory]
    [InlineData(5935u, 2)]
    [InlineData(5955u, 1)]
    [InlineData(5975u, 5)]
    [InlineData(7115u, 233)]
    public void Nl80211Radio_FrequencyMhzToChannel_6Ghz_Maps_Correctly(uint freq, int expectedChannel)
    {
        Assert.Equal(expectedChannel, LinuxNl80211Radio.FrequencyMhzToChannel(freq));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(9999u)]
    [InlineData(3000u)]
    public void Nl80211Radio_FrequencyMhzToChannel_Unknown_Frequencies_Return_Null(uint freq)
    {
        Assert.Null(LinuxNl80211Radio.FrequencyMhzToChannel(freq));
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Never_Triggers_Active_Scan()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        radio.ReadAccessPoint("MyWiFi", "00:11:22:33:44:55");

        Assert.Equal(1, socket.DumpBssCallCount); // Uses GET_SCAN cached dump only
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Never_Calls_GetStation()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "HomeWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.Equal(0, socket.GetStationCallCount); // Station telemetry is never invoked by AP lookup
    }

    [Theory]
    [InlineData(4910u, 182)]
    [InlineData(4980u, 196)]
    public void Nl80211Radio_FrequencyMhzToChannel_49Ghz_PublicSafety_Maps_Correctly(uint freq, int expectedChannel)
    {
        Assert.Equal(expectedChannel, LinuxNl80211Radio.FrequencyMhzToChannel(freq));
    }

    [Theory]
    [InlineData(2413u)]
    [InlineData(5181u)]
    [InlineData(5956u)]
    [InlineData(4911u)]
    public void Nl80211Radio_FrequencyMhzToChannel_OffGrid_Frequencies_Return_Null(uint freq)
    {
        Assert.Null(LinuxNl80211Radio.FrequencyMhzToChannel(freq));
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Invalid_Requested_Bssid_Returns_Null_Without_Bss_Dump()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");
        var ap = radio.ReadAccessPoint("HomeWiFi", "not_a_valid_mac");

        Assert.Null(ap);
        Assert.Equal(0, socket.DumpBssCallCount);
    }

    [Fact]
    public void Nl80211Radio_ReadAccessPoint_Missing_Bound_Interface_Does_Not_Invoke_Sockets()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);

        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "");
        var ap = radio.ReadAccessPoint("HomeWiFi", "00:11:22:33:44:55");

        Assert.Null(ap);
        Assert.Equal(0, socket.DumpInterfacesCallCount);
        Assert.Equal(0, socket.DumpBssCallCount);
    }

    #endregion

    #region Phase 3.1-7B-5: MLO Composition Tests

    [Fact]
    public async Task Nl80211Radio_MloComposition_SingleLink_NonMlo_Yields_NotMlo_And_Core_Exact_Bssid()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 };
        var bss = new LinuxNl80211BssInfo(3, bssid, "00:11:22:33:44:55", null, "SingleWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, null, null, null, null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss);

        using var radio = new LinuxNl80211Radio(socket);
        var obs = await radio.ReadAssociationObservationAsync("wlan0");
        Assert.NotNull(obs);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(obs.Links);
        Assert.Equal(LinuxMloCompositionState.NotMlo, mlo.State);

        var coreAssoc = await radio.ReadAssociationAsync("wlan0");
        Assert.NotNull(coreAssoc);
        Assert.Equal("SingleWiFi", coreAssoc.Ssid);
        Assert.Equal("00:11:22:33:44:55", coreAssoc.Bssid);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_SingleActiveLink_With_MldAddr_And_LinkId_Is_Valid_Mlo()
    {
        byte[] bssid = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        byte[] ssidBytes = System.Text.Encoding.UTF8.GetBytes("MloWiFi");

        var link = new LinuxAssociatedBssLink(
            Bssid: "00:11:22:33:44:01",
            BssidBytes: bssid,
            MloLinkId: 0,
            MldAddress: "00:11:22:33:44:00",
            MldAddressBytes: mldAddr,
            SsidBytes: ssidBytes,
            DisplaySsid: "MloWiFi",
            FrequencyMhz: 5180,
            SignalMbm: -6500,
            SignalUnspec: null,
            SeenMsAgo: null,
            LastSeenBootTimeNs: null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { link });

        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
        Assert.Equal(mldAddr, mlo.MldAddressBytes);
        Assert.Equal("00:11:22:33:44:00", mlo.MldAddress);
        Assert.Equal("MloWiFi", mlo.DisplaySsid);
        Assert.Single(mlo.Links);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_TwoLinks_CommonMld_UniqueLinkIds_Is_Valid()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        byte[] ssidBytes = System.Text.Encoding.UTF8.GetBytes("MloWiFi");

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, ssidBytes, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:00", mldAddr, ssidBytes, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
        Assert.Equal(mldAddr, mlo.MldAddressBytes);
        Assert.Equal(2, mlo.Links.Count);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_ThreeLinks_Different_Enumeration_Order_Yields_Identical_Canonical_Sequence()
    {
        byte[] bssid0 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        byte[] bssid1 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssid2 = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0xFF };
        byte[] ssidBytes = System.Text.Encoding.UTF8.GetBytes("MloWiFi");

        var link0 = new LinuxAssociatedBssLink("00:11:22:33:44:00", bssid0, 0, "00:11:22:33:44:FF", mldAddr, ssidBytes, "MloWiFi", 2412, -6500, null, null, null);
        var link1 = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssid1, 1, "00:11:22:33:44:FF", mldAddr, ssidBytes, "MloWiFi", 5180, -6000, null, null, null);
        var link2 = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssid2, 2, "00:11:22:33:44:FF", mldAddr, ssidBytes, "MloWiFi", 5975, -5500, null, null, null);

        // Order 1: link2, link0, link1
        var mlo1 = LinuxNl80211Radio.ComposeMloAssociation(new[] { link2, link0, link1 });
        // Order 2: link1, link2, link0
        var mlo2 = LinuxNl80211Radio.ComposeMloAssociation(new[] { link1, link2, link0 });

        Assert.Equal(3, mlo1.Links.Count);
        Assert.Equal(3, mlo2.Links.Count);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(mlo1.Links[i].MloLinkId, mlo2.Links[i].MloLinkId);
            Assert.Equal(mlo1.Links[i].Bssid, mlo2.Links[i].Bssid);
        }

        // Canonical order is link0 (id 0), link1 (id 1), link2 (id 2)
        Assert.Equal((byte)0, mlo1.Links[0].MloLinkId);
        Assert.Equal((byte)1, mlo1.Links[1].MloLinkId);
        Assert.Equal((byte)2, mlo1.Links[2].MloLinkId);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_MultiLink_One_Missing_MldAddr_Is_Incomplete()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, null, null, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Incomplete, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Different_MldAddr_Values_Is_Conflicted()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddrA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x0A };
        byte[] mldAddrB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x0B };

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:0A", mldAddrA, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:0B", mldAddrB, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Conflicted, mlo.State);
        Assert.Null(mlo.MldAddressBytes);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Missing_LinkId_On_One_Link_Is_Incomplete()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, null, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Incomplete, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Duplicate_LinkId_Different_Bssid_Is_Conflicted()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        // Both links claim LinkId = 0
        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Conflicted, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Duplicate_Raw_Bssid_Different_LinkIds_Is_Conflicted()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        // Same BSSID on different LinkIds
        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 1, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Conflicted, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Same_Raw_Bssid_Different_Presentation_Strings_Is_Conflicted()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        // Same raw BSSID bytes, but differing presentation strings
        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00-11-22-33-44-01", bssidA, 1, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Conflicted, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Different_Raw_Bssid_Identical_Presentation_Strings_Does_Not_Conflict()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        // Different raw BSSID bytes, but synthetic identical presentation strings -> Raw byte authority wins, no conflict
        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:FF", bssidA, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:FF", bssidB, 1, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Invalid_BssidBytes_Length_Is_Incomplete()
    {
        byte[] bssidInvalid = new byte[] { 0x00, 0x11, 0x22 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var linkA = new LinuxAssociatedBssLink("00:11:22", bssidInvalid, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Incomplete, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Same_Raw_Ssid_Preserves_Common_Ssid()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        byte[] ssidBytes = System.Text.Encoding.UTF8.GetBytes("CampusNet");

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, ssidBytes, "CampusNet", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:00", mldAddr, ssidBytes, "CampusNet", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
        Assert.Equal(ssidBytes, mlo.SsidBytes);
        Assert.Equal("CampusNet", mlo.DisplaySsid);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Different_Raw_Ssid_Values_Is_Conflicted()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        byte[] ssidA = System.Text.Encoding.UTF8.GetBytes("NetA");
        byte[] ssidB = System.Text.Encoding.UTF8.GetBytes("NetB");

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, ssidA, "NetA", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:00", mldAddr, ssidB, "NetB", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Conflicted, mlo.State);
        Assert.Null(mlo.DisplaySsid);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Hidden_ZeroLength_Common_Ssid_Retains_Identity_With_Null_DisplaySsid()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        byte[] zeroSsid = Array.Empty<byte>();

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, zeroSsid, null, 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:00", mldAddr, zeroSsid, null, 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
        Assert.NotNull(mlo.SsidBytes);
        Assert.Empty(mlo.SsidBytes);
        Assert.Null(mlo.DisplaySsid);
        Assert.Equal(mldAddr, mlo.MldAddressBytes);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_Raw_ZeroLength_Ssid_Vs_Raw_NonEmpty_Ssid_Is_Conflicted()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        byte[] zeroSsid = Array.Empty<byte>();
        byte[] nonEmptySsid = System.Text.Encoding.UTF8.GetBytes("VisibleWiFi");

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, zeroSsid, null, 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:00", mldAddr, nonEmptySsid, "VisibleWiFi", 5975, -6000, null, null, null);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(new[] { linkA, linkB });

        Assert.Equal(LinuxMloCompositionState.Conflicted, mlo.State);
        Assert.Null(mlo.SsidBytes);
        Assert.Null(mlo.DisplaySsid);
    }

    [Fact]
    public async Task Nl80211Radio_MloComposition_Aggregate_StationInfo_Peer_Equals_Common_Mld()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        socket.AddStation(3, 0x1000UL, mldAddr, sta);

        using var radio = new LinuxNl80211Radio(socket);
        var composed = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(composed);
        Assert.NotNull(composed.StationInfo);
        Assert.Equal("00:11:22:33:44:00", composed.StationInfo.PeerMacString);
        Assert.Equal(mldAddr, socket.LastRequestedPeerMac);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_StationInfo_Absent_Mlo_Identity_Remains_Valid()
    {
        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var linkA = new LinuxAssociatedBssLink("00:11:22:33:44:01", bssidA, 0, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5180, -6500, null, null, null);
        var linkB = new LinuxAssociatedBssLink("00:11:22:33:44:02", bssidB, 1, "00:11:22:33:44:00", mldAddr, null, "MloWiFi", 5975, -6000, null, null, null);

        var composedObs = new LinuxComposedAssociationObservation(
            IfIndex: 3,
            IfName: "wlan0",
            WiphyIndex: 0,
            State: LinuxWirelessAssociationState.Associated,
            Links: new[] { linkA, linkB },
            Wdev: 0x1000UL,
            Generation: 100u,
            StationInfo: null, // Station query absent/failed
            ContinuityVerified: true,
            DumpStatus: LinuxNl80211DumpStatus.Complete);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(composedObs);

        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
        Assert.Equal(mldAddr, mlo.MldAddressBytes);
    }

    [Fact]
    public async Task Nl80211Radio_MloComposition_GetStation_Enoent_Associated_Mlo_Remains_Associated()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        // No station added to socket -> returns ENOENT
        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.Equal(LinuxWirelessAssociationState.Associated, res.State);
        Assert.Null(res.StationInfo);
        Assert.True(res.ContinuityVerified);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(res);
        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
    }

    [Fact]
    public void Nl80211Radio_MloComposition_StationInfo_Links_Empty_Is_Normal_Kernel_Result()
    {
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };
        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null, Links: null);

        Assert.Empty(sta.Links);
    }

    [Fact]
    public async Task Nl80211Radio_MloComposition_Aggregate_StationInfo_Never_Copied_Into_Individual_Bss_Links()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        socket.AddStation(3, 0x1000UL, mldAddr, sta);

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.NotNull(res.StationInfo);
        // Individual links retain their BSS-level facts and never copy the aggregate StationInfo signal
        Assert.Equal(-6500, res.Links[0].SignalMbm);
        Assert.Equal(-6000, res.Links[1].SignalMbm);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociation_Valid_Mlo_Core_Projection_Ssid_Common_Bssid_Null_Signal_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        using var radio = new LinuxNl80211Radio(socket);
        var assoc = await radio.ReadAssociationAsync("wlan0");

        Assert.NotNull(assoc);
        Assert.Equal("MloWiFi", assoc.Ssid);
        Assert.Null(assoc.Bssid); // Must be strictly null on MLO
        Assert.Null(assoc.SignalQuality); // Must be strictly null on MLO
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociation_Mlo_Projection_Never_Chooses_Strongest_Or_First_Bssid()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidWeak = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidStrong = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bss1 = new LinuxNl80211BssInfo(3, bssidWeak, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -7500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bss2 = new LinuxNl80211BssInfo(3, bssidStrong, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -3500, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        socket.AddBss(3, bss1);
        socket.AddBss(3, bss2);

        using var radio = new LinuxNl80211Radio(socket);
        var assoc = await radio.ReadAssociationAsync("wlan0");

        Assert.NotNull(assoc);
        Assert.Null(assoc.Bssid);
        Assert.NotEqual("00:11:22:33:44:01", assoc.Bssid);
        Assert.NotEqual("00:11:22:33:44:02", assoc.Bssid);
    }

    [Fact]
    public async Task Nl80211Radio_ReadAssociationAsync_Mlo_Path_Does_Not_Invoke_GetStation()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        socket.AddBss(3, bssA);
        socket.AddBss(3, bssB);

        using var radio = new LinuxNl80211Radio(socket);
        await radio.ReadAssociationAsync("wlan0");

        Assert.Equal(0, socket.GetStationCallCount); // Pure BSS projection never calls GET_STATION
    }

    [Fact]
    public async Task Nl80211Radio_MloComposition_Link_Set_Change_AB_To_AC_Triggers_Drift()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] bssidC = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x03 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssC = new LinuxNl80211BssInfo(3, bssidC, "00:11:22:33:44:03", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);

        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // Attempt 1: [bssA, bssB] -> sta -> [bssA, bssC] (drift!)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssB });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssC });

        // Attempt 2: [bssA, bssC] -> sta -> [bssA, bssC] (stable!)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssC });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssC });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(2, socket.GetStationCallCount);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(res);
        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
        Assert.Equal("00:11:22:33:44:03", mlo.Links[1].Bssid);
    }

    [Fact]
    public async Task Nl80211Radio_MloComposition_Enumeration_Order_Change_Maintains_Continuity()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        byte[] bssidA = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 };
        byte[] bssidB = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 };
        byte[] mldAddr = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 };

        var bssA = new LinuxNl80211BssInfo(3, bssidA, "00:11:22:33:44:01", null, "MloWiFi", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6500, null, null, 0, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);
        var bssB = new LinuxNl80211BssInfo(3, bssidB, "00:11:22:33:44:02", null, "MloWiFi", 5975, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, null, null, 1, mldAddr, "00:11:22:33:44:00", null, null, 0x1000UL, 100u);

        var sta = new LinuxNl80211StationInfo(3, mldAddr, "00:11:22:33:44:00", 100u, -62, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        // t0: [bssA, bssB] -> sta -> t2: [bssB, bssA] (reversed order)
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssA, bssB });
        socket.QueueStationResponse(sta);
        socket.QueueBssDump(new List<LinuxNl80211BssInfo> { bssB, bssA });

        using var radio = new LinuxNl80211Radio(socket);
        var res = await radio.ReadComposedAssociationObservationAsync("wlan0");

        Assert.NotNull(res);
        Assert.True(res.ContinuityVerified);
        Assert.Equal(1, socket.GetStationCallCount);

        var mlo = LinuxNl80211Radio.ComposeMloAssociation(res);
        Assert.Equal(LinuxMloCompositionState.Valid, mlo.State);
        Assert.Equal("00:11:22:33:44:01", mlo.Links[0].Bssid);
        Assert.Equal("00:11:22:33:44:02", mlo.Links[1].Bssid);
    }

    #endregion

    #region Phase 3.1-7C: Scan Visibility & Freshness Tests

    [Fact]
    public async Task Nl80211Radio_IsSsidVisibleAsync_Fresh_Complete_Present_Returns_True()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        var bss = new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "00:11:22:33:44:55", System.Text.Encoding.UTF8.GetBytes("HomeMesh"), "HomeMesh", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 5000, null, null, null, null, null, 0x1000UL, 1);
        socket.SetBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Complete);

        using var radio = new LinuxNl80211Radio(socket);
        var visible = await radio.IsSsidVisibleAsync("wlan0", "homemesh"); // Case-insensitive parity

        Assert.True(visible);
    }

    [Fact]
    public async Task Nl80211Radio_IsSsidVisibleAsync_Fresh_CompletedScan_Absent_Returns_False()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        var bss = new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "00:11:22:33:44:55", System.Text.Encoding.UTF8.GetBytes("OtherNet"), "OtherNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 5000, null, null, null, null, null, 0x1000UL, 1);
        socket.SetBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Complete);

        var clock = new StubNativeClock { CurrentBootTimeSec = 1000 };
        var tracker = new LinuxWifiScanCompletionTracker();
        tracker.RecordScanEvent(3, 0x1000UL, LinuxWifiScanEventStatus.Completed, 1000_000_000_000UL, LinuxWifiScanDomain.AllAllowedWildcard());

        using var radio = new LinuxNl80211Radio(socket, rfkillReader: null, boundInterfaceId: null, ownsSocket: null, scanCompletionTracker: tracker, clock: clock);
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");

        Assert.False(visible); // CompletedScan provenance permits false
    }

    [Fact]
    public async Task Nl80211Radio_IsSsidVisibleAsync_Opportunistic_Cache_Absent_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        var bss = new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "00:11:22:33:44:55", System.Text.Encoding.UTF8.GetBytes("OtherNet"), "OtherNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 5000, null, null, null, null, null, 0x1000UL, 1);
        socket.SetBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Complete);

        // No scan completion tracker -> Opportunistic cache only
        using var radio = new LinuxNl80211Radio(socket);
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");

        Assert.Null(visible); // Invariant 250: Opportunistic cache absence is unknown (null)
    }

    [Fact]
    public async Task Nl80211Radio_IsSsidVisibleAsync_Stale_Present_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        // Stale entry (> 3 min)
        var bss = new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "00:11:22:33:44:55", System.Text.Encoding.UTF8.GetBytes("HomeMesh"), "HomeMesh", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 240000, null, null, null, null, null, 0x1000UL, 1);
        socket.SetBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Complete);

        using var radio = new LinuxNl80211Radio(socket);
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");

        Assert.Null(visible); // Stale matching SSID is indeterminate (null), NEVER false
    }

    [Fact]
    public async Task Nl80211Radio_IsSsidVisibleAsync_Empty_Dump_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        socket.SetBssDump(new List<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Complete);

        using var radio = new LinuxNl80211Radio(socket);
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");

        Assert.Null(visible);
    }

    [Fact]
    public async Task Nl80211Radio_IsSsidVisibleAsync_Interrupted_Present_Returns_True()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        var bss = new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "00:11:22:33:44:55", System.Text.Encoding.UTF8.GetBytes("HomeMesh"), "HomeMesh", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 2000, null, null, null, null, null, 0x1000UL, 1);
        socket.SetBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Interrupted);

        using var radio = new LinuxNl80211Radio(socket);
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");

        Assert.True(visible);
    }

    [Fact]
    public async Task Nl80211Radio_IsSsidVisibleAsync_Interrupted_Absent_Returns_Null()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        var bss = new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "00:11:22:33:44:55", System.Text.Encoding.UTF8.GetBytes("OtherNet"), "OtherNet", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 2000, null, null, null, null, null, 0x1000UL, 1);
        socket.SetBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Interrupted);

        using var radio = new LinuxNl80211Radio(socket);
        var visible = await radio.IsSsidVisibleAsync("wlan0", "HomeMesh");

        Assert.Null(visible);
    }

    [Fact]
    public void Nl80211Radio_IsSsidVisible_Synchronous_Uses_Bound_Or_LastQueried_Interface()
    {
        var socket = new MockLinuxNl80211Socket();
        socket.AddFamily("nl80211", 28);
        socket.AddInterface(new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", null, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, null, Wdev: 0x1000UL));

        var bss = new LinuxNl80211BssInfo(3, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, "00:11:22:33:44:55", System.Text.Encoding.UTF8.GetBytes("HomeMesh"), "HomeMesh", 5180, LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED, -6000, 85, 2000, null, null, null, null, null, 0x1000UL, 1);
        socket.SetBssDump(new List<LinuxNl80211BssInfo> { bss }, LinuxNl80211DumpStatus.Complete);

        // 1. Bound interface
        using (var boundRadio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0"))
        {
            var visible = boundRadio.IsSsidVisible("HomeMesh");
            Assert.True(visible);
        }

        // 2. Unbound radio without prior queries -> null
        using (var unboundRadio = new LinuxNl80211Radio(socket))
        {
            var visible = unboundRadio.IsSsidVisible("HomeMesh");
            Assert.Null(visible);

            // After querying association on wlan0, last-queried fallback activates
            unboundRadio.ReadAssociation("wlan0");
            var visibleAfterQuery = unboundRadio.IsSsidVisible("HomeMesh");
            Assert.True(visibleAfterQuery);
        }
    }

    [Fact]
    public void Nl80211Radio_RequestUrgentScan_Is_NoOp()
    {
        var socket = new MockLinuxNl80211Socket();
        using var radio = new LinuxNl80211Radio(socket, boundInterfaceId: "wlan0");

        // Must not throw and must have no active scan side-effects
        radio.RequestUrgentScan();
    }

    #endregion

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

    private static byte[] BuildMockStationRecord(
        uint seq,
        ushort familyId,
        int ifindex,
        byte[] peerMac,
        ulong wdev = 0x1000UL,
        uint generation = 100u,
        sbyte? signal = -60,
        sbyte? signalAvg = null,
        ulong? rxBytes = null,
        ulong? txBytes = null,
        uint? rxPackets = null,
        uint? txPackets = null,
        uint? txRetries = null,
        uint? txFailed = null,
        uint? connectedTime = null,
        byte[]? customStaInfoBytes = null,
        byte[]? txRateBytes = null,
        byte[]? rxRateBytes = null,
        byte? mloLinkId = null,
        byte[]? mldAddr = null,
        bool omitIfindex = false,
        bool omitWdev = false,
        byte[]? customWdevBytes = null,
        bool omitMac = false,
        bool omitGeneration = false)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] staBytes;
        if (customStaInfoBytes != null)
        {
            staBytes = customStaInfoBytes;
        }
        else
        {
            var staMs = new MemoryStream();
            using var sbw = new BinaryWriter(staMs);

            if (signal.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_SIGNAL);
                sbw.Write(unchecked((byte)signal.Value));
                WritePadding(sbw, LinuxGenlProtocol.NlaHeaderSize + 1);
            }

            if (signalAvg.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_SIGNAL_AVG);
                sbw.Write(unchecked((byte)signalAvg.Value));
                WritePadding(sbw, LinuxGenlProtocol.NlaHeaderSize + 1);
            }

            if (rxBytes.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_RX_BYTES64);
                sbw.Write(rxBytes.Value);
            }

            if (txBytes.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_TX_BYTES64);
                sbw.Write(txBytes.Value);
            }

            if (rxPackets.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_RX_PACKETS);
                sbw.Write(rxPackets.Value);
            }

            if (txPackets.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_TX_PACKETS);
                sbw.Write(txPackets.Value);
            }

            if (txRetries.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_TX_RETRIES);
                sbw.Write(txRetries.Value);
            }

            if (txFailed.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_TX_FAILED);
                sbw.Write(txFailed.Value);
            }

            if (connectedTime.HasValue)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_CONNECTED_TIME);
                sbw.Write(connectedTime.Value);
            }

            if (txRateBytes != null)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + txRateBytes.Length));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_TX_BITRATE);
                sbw.Write(txRateBytes);
                WritePadding(sbw, LinuxGenlProtocol.NlaHeaderSize + txRateBytes.Length);
            }

            if (rxRateBytes != null)
            {
                sbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + rxRateBytes.Length));
                sbw.Write(LinuxNl80211Protocol.NL80211_STA_INFO_RX_BITRATE);
                sbw.Write(rxRateBytes);
                WritePadding(sbw, LinuxGenlProtocol.NlaHeaderSize + rxRateBytes.Length);
            }

            staBytes = staMs.ToArray();
        }

        // Top level attrs
        var topMs = new MemoryStream();
        using (var tbw = new BinaryWriter(topMs))
        {
            if (!omitIfindex)
            {
                tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
                tbw.Write(ifindex);
            }

            if (customWdevBytes != null)
            {
                tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + customWdevBytes.Length));
                tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
                tbw.Write(customWdevBytes);
                WritePadding(tbw, LinuxGenlProtocol.NlaHeaderSize + customWdevBytes.Length);
            }
            else if (!omitWdev)
            {
                tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 8));
                tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_WDEV);
                tbw.Write(wdev);
            }

            if (!omitMac && peerMac != null)
            {
                tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + peerMac.Length));
                tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_MAC);
                tbw.Write(peerMac);
                WritePadding(tbw, LinuxGenlProtocol.NlaHeaderSize + peerMac.Length);
            }

            if (!omitGeneration)
            {
                tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
                tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_GENERATION);
                tbw.Write(generation);
            }

            if (mloLinkId.HasValue)
            {
                tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 1));
                tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_MLO_LINK_ID);
                tbw.Write(mloLinkId.Value);
                WritePadding(tbw, LinuxGenlProtocol.NlaHeaderSize + 1);
            }

            if (mldAddr != null)
            {
                tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + mldAddr.Length));
                tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_MLD_ADDR);
                tbw.Write(mldAddr);
                WritePadding(tbw, LinuxGenlProtocol.NlaHeaderSize + mldAddr.Length);
            }

            tbw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + staBytes.Length));
            tbw.Write(LinuxNl80211Protocol.NL80211_ATTR_STA_INFO);
            tbw.Write(staBytes);
            WritePadding(tbw, LinuxGenlProtocol.NlaHeaderSize + staBytes.Length);
        }

        byte[] topPayload = topMs.ToArray();
        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize + topPayload.Length;

        bw.Write(totalLen);
        bw.Write(familyId);
        bw.Write((ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);

        bw.Write(LinuxNl80211Protocol.NL80211_CMD_NEW_STATION);
        bw.Write((byte)1);
        bw.Write((ushort)0);

        bw.Write(topPayload);

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
        private readonly Dictionary<(int IfIndex, ulong Wdev, string Mac), LinuxNl80211StationInfo> _stations = new();
        private readonly Queue<LinuxNl80211DumpResult<LinuxNl80211BssInfo>> _bssDumpQueue = new();
        private readonly Queue<LinuxNl80211SingleResult<LinuxNl80211StationInfo>> _stationQueue = new();
        private readonly Queue<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> _interfaceDumpQueue = new();
        private int _dumpInterfacesCallCount = 0;

        public int GetStationCallCount { get; private set; }
        public int DumpBssCallCount { get; private set; }
        public int DumpInterfacesCallCount => _dumpInterfacesCallCount;
        public int? LastDumpBssIfIndex { get; private set; }
        public ulong? LastDumpBssWdev { get; private set; }
        public byte[]? LastRequestedPeerMac { get; private set; }

        public LinuxNl80211DumpStatus InterfaceDumpStatus { get; set; } = LinuxNl80211DumpStatus.Complete;
        public LinuxNl80211DumpStatus WiphyDumpStatus { get; set; } = LinuxNl80211DumpStatus.Complete;
        public LinuxNl80211DumpStatus BssDumpStatus { get; set; } = LinuxNl80211DumpStatus.Complete;
        public LinuxNl80211DumpStatus StationStatus { get; set; } = LinuxNl80211DumpStatus.Complete;

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

        public void SetBssDump(List<LinuxNl80211BssInfo> items, LinuxNl80211DumpStatus status = LinuxNl80211DumpStatus.Complete)
        {
            _bssRecords.Clear();
            if (items != null)
            {
                foreach (var bss in items)
                {
                    AddBss(bss.IfIndex, bss);
                }
            }
            BssDumpStatus = status;
        }

        public void AddStation(int ifindex, ulong wdev, byte[] mac, LinuxNl80211StationInfo sta)
        {
            _stations[(ifindex, wdev, LinuxNl80211Protocol.FormatMacAddress(mac))] = sta;
        }

        public void QueueBssDump(List<LinuxNl80211BssInfo> items, LinuxNl80211DumpStatus status = LinuxNl80211DumpStatus.Complete, int errorCode = 0)
        {
            _bssDumpQueue.Enqueue(new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(items, status, errorCode, SawDone: status == LinuxNl80211DumpStatus.Complete));
        }

        public void QueueStationResponse(LinuxNl80211StationInfo? sta, LinuxNl80211DumpStatus status = LinuxNl80211DumpStatus.Complete, int errorCode = 0)
        {
            _stationQueue.Enqueue(new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(sta, status, errorCode));
        }

        public void QueueInterfaceDump(List<LinuxNl80211InterfaceInfo> items, LinuxNl80211DumpStatus status = LinuxNl80211DumpStatus.Complete)
        {
            _interfaceDumpQueue.Enqueue(new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(items, status, 0, SawDone: status == LinuxNl80211DumpStatus.Complete));
        }

        public Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default)
        {
            _families.TryGetValue(familyName, out var fam);
            return Task.FromResult(fam);
        }

        public Task<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> DumpInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default)
        {
            _dumpInterfacesCallCount++;
            if (_interfaceDumpQueue.Count > 0)
            {
                return Task.FromResult(_interfaceDumpQueue.Dequeue());
            }

            if (_dumpInterfacesCallCount > 1 && ContinuityInterfaceOverride != null)
            {
                var overridenList = new List<LinuxNl80211InterfaceInfo> { ContinuityInterfaceOverride };
                return Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(overridenList, InterfaceDumpStatus, 0, SawDone: true));
            }

            var list = ifindex.HasValue ? _interfaces.FindAll(i => i.IfIndex == ifindex.Value) : new List<LinuxNl80211InterfaceInfo>(_interfaces);
            var res = new LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>(list, InterfaceDumpStatus, InterfaceDumpStatus == LinuxNl80211DumpStatus.Complete ? 0 : -11, SawDone: InterfaceDumpStatus == LinuxNl80211DumpStatus.Complete);
            return Task.FromResult(res);
        }

        private readonly Queue<List<LinuxNl80211InterfaceInfo>> _getInterfaceQueue = new();

        public void QueueGetInterfaces(List<LinuxNl80211InterfaceInfo> list) => _getInterfaceQueue.Enqueue(list);

        public Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default)
        {
            if (_getInterfaceQueue.Count > 0)
            {
                return Task.FromResult(_getInterfaceQueue.Dequeue());
            }
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

        public Task<LinuxNl80211DumpResult<LinuxNl80211BssInfo>> DumpBssAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, CancellationToken cancellationToken = default)
        {
            DumpBssCallCount++;
            LastDumpBssIfIndex = ifindex;
            LastDumpBssWdev = expectedWdev;
            if (_bssDumpQueue.Count > 0)
            {
                return Task.FromResult(_bssDumpQueue.Dequeue());
            }

            _bssRecords.TryGetValue(ifindex, out var list);
            var items = list ?? new List<LinuxNl80211BssInfo>();
            var res = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(items, BssDumpStatus, BssDumpStatus == LinuxNl80211DumpStatus.Complete ? 0 : -11, SawDone: BssDumpStatus == LinuxNl80211DumpStatus.Complete);
            return Task.FromResult(res);
        }

        public Task<LinuxNl80211SingleResult<LinuxNl80211StationInfo>> GetStationAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, byte[] peerMac, CancellationToken cancellationToken = default)
        {
            GetStationCallCount++;
            LastRequestedPeerMac = peerMac;

            if (_stationQueue.Count > 0)
            {
                return Task.FromResult(_stationQueue.Dequeue());
            }

            if (StationStatus != LinuxNl80211DumpStatus.Complete)
            {
                return Task.FromResult(new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, StationStatus, StationStatus == LinuxNl80211DumpStatus.KernelError ? -2 : -11));
            }
            _stations.TryGetValue((ifindex, expectedWdev, LinuxNl80211Protocol.FormatMacAddress(peerMac)), out var sta);
            if (sta == null)
            {
                // ENOENT (-2) when station not found in kernel table
                return Task.FromResult(new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(null, LinuxNl80211DumpStatus.KernelError, -2));
            }
            return Task.FromResult(new LinuxNl80211SingleResult<LinuxNl80211StationInfo>(sta, LinuxNl80211DumpStatus.Complete, 0));
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
