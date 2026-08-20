using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Linux.Network;
using IEM.Linux.Wifi;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxWifiLinkInspectorTests
{
    private sealed class StubLinkInspector : ILinkInspector
    {
        public LinkSnapshot Snapshot { get; set; } = new(
            InterfaceName: "wlan0",
            InterfaceId: "wlan0",
            Status: LinkStatus.Up,
            Medium: LinkMedium.Wireless);

        public int InspectCalls { get; private set; }

        public LinkSnapshot Inspect()
        {
            InspectCalls++;
            return Snapshot;
        }
    }

    private sealed class TrackingNl80211Socket : ILinuxNl80211Socket
    {
        public int GetFamilyCalls { get; private set; }
        public int DumpInterfacesCalls { get; private set; }
        public int DumpBssCalls { get; private set; }
        public int GetStationCalls { get; private set; }
        public bool IsDisposed { get; private set; }

        public GenlFamilyInfo? FamilyToReturn { get; set; } = new(28, "nl80211", 1, 0, 32, new Dictionary<string, uint>());
        public LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo> InterfacesToReturn { get; set; } =
            new(new[] { new LinuxNl80211InterfaceInfo(3, "wlan0", 0, "phy0", new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 }, LinuxNl80211Protocol.NL80211_IFTYPE_STATION, null, 5180, 0x1000UL) }, LinuxNl80211DumpStatus.Complete);

        public LinuxNl80211DumpResult<LinuxNl80211BssInfo> BssToReturn { get; set; } =
            new(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Complete);

        public LinuxNl80211SingleResult<LinuxNl80211StationInfo> StationToReturn { get; set; } =
            new(null, LinuxNl80211DumpStatus.Incomplete);

        public Task<GenlFamilyInfo?> GetFamilyAsync(string familyName, CancellationToken cancellationToken = default)
        {
            GetFamilyCalls++;
            return Task.FromResult(FamilyToReturn);
        }

        public Task<LinuxNl80211DumpResult<LinuxNl80211InterfaceInfo>> DumpInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default)
        {
            DumpInterfacesCalls++;
            return Task.FromResult(InterfacesToReturn);
        }

        public Task<LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>> DumpWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinuxNl80211DumpResult<LinuxNl80211WiphyInfo>(Array.Empty<LinuxNl80211WiphyInfo>(), LinuxNl80211DumpStatus.Complete));

        public Task<LinuxNl80211DumpResult<LinuxNl80211BssInfo>> DumpBssAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, CancellationToken cancellationToken = default)
        {
            DumpBssCalls++;
            return Task.FromResult(BssToReturn);
        }

        public Task<List<LinuxNl80211InterfaceInfo>> GetInterfacesAsync(ushort nl80211FamilyId, int? ifindex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<LinuxNl80211InterfaceInfo>(InterfacesToReturn.Items));

        public Task<List<LinuxNl80211WiphyInfo>> GetWiphysAsync(ushort nl80211FamilyId, uint? wiphyIndex = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<LinuxNl80211WiphyInfo>());

        public Task<LinuxNl80211SingleResult<LinuxNl80211StationInfo>> GetStationAsync(ushort nl80211FamilyId, int ifindex, ulong expectedWdev, byte[] peerMac, CancellationToken cancellationToken = default)
        {
            GetStationCalls++;
            return Task.FromResult(StationToReturn);
        }

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubRfkillReader : ILinuxRfkillReader
    {
        public bool HardBlocked { get; set; }
        public bool SoftBlocked { get; set; }

        public LinuxRfkillObservation? ReadObservationForWiphy(uint wiphyIndex, string? ifname = null) =>
            new(0, wiphyIndex, HardBlocked, SoftBlocked, LinuxRfkillEvidenceBasis.DevRfkill);
    }

    [Fact]
    public void Inspect_NonWirelessMedium_PassesThrough_WithoutQueryingRadio()
    {
        var inner = new StubLinkInspector
        {
            Snapshot = new LinkSnapshot("eth0", "eth0", LinkStatus.Up, LinkMedium.Ethernet)
        };
        var socket = new TrackingNl80211Socket();
        var inspector = new LinuxWifiLinkInspector(inner, "eth0", socket);

        var snapshot = inspector.Inspect();

        Assert.Equal("eth0", snapshot.InterfaceName);
        Assert.Equal(LinkMedium.Ethernet, snapshot.Medium);
        Assert.Null(snapshot.Wireless);
        Assert.Equal(1, inner.InspectCalls);
        Assert.Equal(0, socket.GetFamilyCalls);
        Assert.Equal(0, socket.DumpInterfacesCalls);
    }

    [Fact]
    public void Inspect_WirelessMedium_SingleLinkAssociated_EnrichesSnapshot()
    {
        var inner = new StubLinkInspector
        {
            Snapshot = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless)
        };
        var socket = new TrackingNl80211Socket();
        var rfkill = new StubRfkillReader { HardBlocked = false, SoftBlocked = false };

        var bss = new LinuxNl80211BssInfo(
            IfIndex: 3,
            Bssid: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 },
            BssidString: "00:11:22:33:44:55",
            SsidBytes: System.Text.Encoding.UTF8.GetBytes("OfficeWiFi"),
            DisplaySsid: "OfficeWiFi",
            FrequencyMhz: 5180,
            Status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED,
            SignalMbm: -6500,
            SignalQuality: 85,
            SeenMsAgo: 10,
            MloLinkId: null,
            MldAddress: null,
            MldAddressString: null,
            LastSeenBootTimeNs: null,
            InformationElements: null,
            Wdev: 0x1000UL,
            Generation: 100);

        socket.BssToReturn = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { bss }, LinuxNl80211DumpStatus.Complete);

        var inspector = new LinuxWifiLinkInspector(inner, "wlan0", socket, rfkill);

        var snapshot = inspector.Inspect();

        Assert.Equal("wlan0", snapshot.InterfaceName);
        Assert.Equal(LinkMedium.Wireless, snapshot.Medium);
        Assert.NotNull(snapshot.Wireless);
        Assert.Equal("OfficeWiFi", snapshot.Wireless.Ssid);
        Assert.Equal("00:11:22:33:44:55", snapshot.Wireless.Bssid);
        Assert.Equal(85, snapshot.Wireless.SignalQualityPercent);
        Assert.Equal(36, snapshot.Wireless.Channel);
        Assert.Equal(-65, snapshot.Wireless.MeasuredRssiDbm);
        Assert.True(snapshot.Wireless.RadioOn);
    }

    [Fact]
    public void Inspect_WirelessMedium_MloAssociated_SafeCoreProjection()
    {
        var inner = new StubLinkInspector
        {
            Snapshot = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless)
        };
        var socket = new TrackingNl80211Socket();

        var link0 = new LinuxNl80211BssInfo(
            IfIndex: 3,
            Bssid: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x01 },
            BssidString: "00:11:22:33:44:01",
            SsidBytes: System.Text.Encoding.UTF8.GetBytes("MeshMLO"),
            DisplaySsid: "MeshMLO",
            FrequencyMhz: 5180,
            Status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED,
            SignalMbm: -6000,
            SignalQuality: 88,
            SeenMsAgo: 5,
            MloLinkId: 0,
            MldAddress: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 },
            MldAddressString: "00:11:22:33:44:00",
            LastSeenBootTimeNs: null,
            InformationElements: null,
            Wdev: 0x1000UL,
            Generation: 100);

        var link1 = new LinuxNl80211BssInfo(
            IfIndex: 3,
            Bssid: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x02 },
            BssidString: "00:11:22:33:44:02",
            SsidBytes: System.Text.Encoding.UTF8.GetBytes("MeshMLO"),
            DisplaySsid: "MeshMLO",
            FrequencyMhz: 5975,
            Status: LinuxNl80211Protocol.NL80211_BSS_STATUS_ASSOCIATED,
            SignalMbm: -6200,
            SignalQuality: 82,
            SeenMsAgo: 5,
            MloLinkId: 1,
            MldAddress: new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x00 },
            MldAddressString: "00:11:22:33:44:00",
            LastSeenBootTimeNs: null,
            InformationElements: null,
            Wdev: 0x1000UL,
            Generation: 100);

        socket.BssToReturn = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(new[] { link0, link1 }, LinuxNl80211DumpStatus.Complete);

        var inspector = new LinuxWifiLinkInspector(inner, "wlan0", socket);

        var snapshot = inspector.Inspect();

        Assert.NotNull(snapshot.Wireless);
        Assert.Equal("MeshMLO", snapshot.Wireless.Ssid);
        Assert.Null(snapshot.Wireless.Bssid); // strictly null on MLO
        Assert.Null(snapshot.Wireless.SignalQualityPercent); // strictly null on MLO
    }

    [Fact]
    public void Inspect_WirelessMedium_Unassociated_ReturnsNullWireless()
    {
        var inner = new StubLinkInspector
        {
            Snapshot = new LinkSnapshot("wlan0", "wlan0", LinkStatus.Up, LinkMedium.Wireless)
        };
        var socket = new TrackingNl80211Socket();
        socket.BssToReturn = new LinuxNl80211DumpResult<LinuxNl80211BssInfo>(Array.Empty<LinuxNl80211BssInfo>(), LinuxNl80211DumpStatus.Complete);

        var inspector = new LinuxWifiLinkInspector(inner, "wlan0", socket);

        var snapshot = inspector.Inspect();

        Assert.Null(snapshot.Wireless);
    }

    [Fact]
    public async Task LinuxProbeFactory_CreateLinkInspectionAsync_Returns_LinuxLinkInspectionScope_With_LinuxWifiLinkInspector()
    {
        var scope = await LinuxProbeFactory.Instance.CreateLinkInspectionAsync("wlan0");

        Assert.NotNull(scope);
        Assert.IsType<LinuxLinkInspectionScope>(scope);
        var linuxScope = (LinuxLinkInspectionScope)scope;
        Assert.NotNull(linuxScope.WifiInspector);
        Assert.Equal("wlan0", linuxScope.WifiInspector.Radio.BoundInterfaceId);
    }

    [Fact]
    public async Task ScopeDisposal_Disposes_Owned_Radio_And_Socket()
    {
        var inner = new StubLinkInspector();
        var socket = new TrackingNl80211Socket();
        var inspector = new LinuxWifiLinkInspector(inner, "wlan0", socket, ownsSocket: true);
        var scope = new LinuxLinkInspectionScope(inspector);

        await scope.DisposeAsync();

        Assert.True(socket.IsDisposed);
    }
}
