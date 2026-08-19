using System;
using System.Collections.Generic;
using System.IO;
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
    public void Nl80211Protocol_Builds_And_Parses_Interface_Response()
    {
        uint seq = 201;
        byte[] response = BuildMockInterfaceResponse(seq, ifindex: 3, ifname: "wlan0", wiphy: 0, iftype: LinuxNl80211Protocol.NL80211_IFTYPE_STATION);

        int res = LinuxNl80211Protocol.ParseInterfaceResponse(response, seq, out var list);
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

    private static byte[] BuildMockInterfaceResponse(uint seq, int ifindex, string ifname, uint wiphy, int iftype)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        byte[] nameBytes = Encoding.UTF8.GetBytes(ifname);
        int nameAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);
        int ifindexAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
        int wiphyAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);
        int iftypeAttrLen = LinuxGenlProtocol.NlaAlign(LinuxGenlProtocol.NlaHeaderSize + 4);

        int totalLen = LinuxGenlProtocol.NlmsgHeaderSize + LinuxGenlProtocol.GenlHeaderSize +
                       ifindexAttrLen + nameAttrLen + wiphyAttrLen + iftypeAttrLen;

        // nlmsghdr
        bw.Write(totalLen);
        bw.Write((ushort)28); // nl80211 family id
        bw.Write((ushort)0);
        bw.Write(seq);
        bw.Write((uint)0);

        // genlmsghdr
        bw.Write(LinuxNl80211Protocol.NL80211_CMD_NEW_INTERFACE);
        bw.Write((byte)1);
        bw.Write((ushort)0);

        // NL80211_ATTR_IFINDEX
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
        bw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFINDEX);
        bw.Write(ifindex);

        // NL80211_ATTR_IFNAME
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1));
        bw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFNAME);
        bw.Write(nameBytes);
        bw.Write((byte)0);
        WritePadding(bw, LinuxGenlProtocol.NlaHeaderSize + nameBytes.Length + 1);

        // NL80211_ATTR_WIPHY
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
        bw.Write(LinuxNl80211Protocol.NL80211_ATTR_WIPHY);
        bw.Write(wiphy);

        // NL80211_ATTR_IFTYPE
        bw.Write((ushort)(LinuxGenlProtocol.NlaHeaderSize + 4));
        bw.Write(LinuxNl80211Protocol.NL80211_ATTR_IFTYPE);
        bw.Write(iftype);

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

        public void AddFamily(string name, ushort id) => _families[name] = new GenlFamilyInfo(id, name, 1, 0, 0, new Dictionary<string, uint>());
        public void AddInterface(LinuxNl80211InterfaceInfo ifinfo) => _interfaces.Add(ifinfo);
        public void AddWiphy(LinuxNl80211WiphyInfo winfo) => _wiphys.Add(winfo);

        public Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default)
        {
            _families.TryGetValue(familyName, out var fam);
            return Task.FromResult(fam);
        }

        public Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default)
        {
            if (ifindex.HasValue)
            {
                var match = _interfaces.FindAll(i => i.IfIndex == ifindex.Value);
                return Task.FromResult(match);
            }
            return Task.FromResult(new List<LinuxNl80211InterfaceInfo>(_interfaces));
        }

        public Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default)
        {
            if (wiphyIndex.HasValue)
            {
                var match = _wiphys.FindAll(w => w.WiphyIndex == wiphyIndex.Value);
                return Task.FromResult(match);
            }
            return Task.FromResult(new List<LinuxNl80211WiphyInfo>(_wiphys));
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
