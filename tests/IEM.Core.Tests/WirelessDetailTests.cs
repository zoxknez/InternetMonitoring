using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// The rules that turn wireless readings into a finding, tested without a radio.
/// <para>
/// These decide the most consequential judgement the tool makes: when a network vanishes,
/// was it the router that stopped broadcasting - which is a fault - or the customer walking
/// out of range, which is not. Until the platform calls sat behind an interface, the only
/// way to exercise them was to walk around a flat with a laptop and a stopwatch.
/// </para>
/// </summary>
public sealed class WirelessDetailTests
{
    private const string Adapter = "{1F2E3D4C-0000-0000-0000-000000000001}";

    // ---- What a connected adapter reports ----------------------------------------

    [Fact]
    public void A_connected_adapter_reports_the_access_point_it_is_actually_on()
    {
        var radio = new StubRadio
        {
            RadioOn = true,
            Association = new WirelessAssociation("KucaWiFi", "aa:bb:cc:dd:ee:01", 78),
            AccessPoints = { ["aa:bb:cc:dd:ee:01"] = new WirelessAccessPoint("aa:bb:cc:dd:ee:01", 36, -58) },
            Visible = true,
        };

        var wireless = new WirelessDetailReader(radio, new ManualClock()).Read(Adapter);

        Assert.NotNull(wireless);
        Assert.Equal("KucaWiFi", wireless.Ssid);
        Assert.Equal("aa:bb:cc:dd:ee:01", wireless.Bssid);
        Assert.Equal(36, wireless.Channel);
        Assert.Equal(-58, wireless.MeasuredRssiDbm);
        Assert.Equal(78, wireless.SignalQualityPercent);
        Assert.True(wireless.RadioOn);
        Assert.True(wireless.SsidVisibleInScan);
    }

    /// <summary>
    /// On a mesh or a dual-band router several access points share one name. Picking the
    /// loudest names the wrong one, and then invents a roaming event every time the strongest
    /// changes while the laptop sits still on the sofa.
    /// </summary>
    [Fact]
    public void The_signal_comes_from_the_associated_access_point_not_the_loudest_one()
    {
        var radio = new StubRadio
        {
            Association = new WirelessAssociation("KucaWiFi", "aa:bb:cc:dd:ee:02", 55),
            AccessPoints =
            {
                ["aa:bb:cc:dd:ee:01"] = new WirelessAccessPoint("aa:bb:cc:dd:ee:01", 1, -40),
                ["aa:bb:cc:dd:ee:02"] = new WirelessAccessPoint("aa:bb:cc:dd:ee:02", 44, -72),
            },
        };

        var wireless = new WirelessDetailReader(radio, new ManualClock()).Read(Adapter);

        Assert.NotNull(wireless);
        Assert.Equal("aa:bb:cc:dd:ee:02", wireless.Bssid);
        Assert.Equal(-72, wireless.MeasuredRssiDbm);
        Assert.Equal(44, wireless.Channel);
    }

    /// <summary>
    /// Scans lag: a mesh node can be carrying this machine's traffic while the scan that
    /// would list it is seconds old. The identity is still known; the radio detail is not,
    /// and must stay missing rather than be borrowed from a different access point.
    /// </summary>
    [Fact]
    public void An_association_missing_from_the_last_scan_keeps_its_identity_and_loses_only_the_detail()
    {
        var radio = new StubRadio
        {
            Association = new WirelessAssociation("KucaWiFi", "aa:bb:cc:dd:ee:09", 60),
        };

        var wireless = new WirelessDetailReader(radio, new ManualClock()).Read(Adapter);

        Assert.NotNull(wireless);
        Assert.Equal("aa:bb:cc:dd:ee:09", wireless.Bssid);
        Assert.Null(wireless.Channel);
        Assert.Null(wireless.MeasuredRssiDbm);
    }

    [Fact]
    public void With_no_wireless_detail_at_all_there_is_no_snapshot_rather_than_an_empty_one()
    {
        Assert.Null(new WirelessDetailReader(new StubRadio(), new ManualClock()).Read(Adapter));
    }

    // ---- Bridging the drop --------------------------------------------------------

    /// <summary>
    /// Once the adapter drops, Windows no longer says which network it was on - and that is
    /// precisely when the question "is that network still on the air" has to be asked.
    /// </summary>
    [Fact]
    public void After_the_adapter_drops_the_network_is_still_asked_about_by_name()
    {
        var clock = new ManualClock();
        var radio = new StubRadio
        {
            Association = new WirelessAssociation("KucaWiFi", "aa:bb:cc:dd:ee:01", 70),
            Visible = true,
        };

        var reader = new WirelessDetailReader(radio, clock);
        reader.Read(Adapter);

        // The radio drops: no association at all, and the network has gone off the air.
        radio.Association = null;
        radio.Visible = false;
        clock.Advance(TimeSpan.FromMinutes(2));

        var wireless = reader.Read(Adapter);

        Assert.NotNull(wireless);
        Assert.Equal("KucaWiFi", wireless.Ssid);
        Assert.False(wireless.SsidVisibleInScan);
        Assert.Equal("KucaWiFi", radio.LastVisibilityQuestion);
    }

    /// <summary>
    /// The remembered name bridges the drop; it does not outlive it. Hours later it names a
    /// network the adapter has not seen since, and a visibility check built on it would
    /// answer about a connection that is no longer the one being measured.
    /// </summary>
    [Fact]
    public void A_remembered_name_stops_meaning_anything_once_it_is_old()
    {
        var clock = new ManualClock();
        var radio = new StubRadio
        {
            Association = new WirelessAssociation("KucaWiFi", "aa:bb:cc:dd:ee:01", 70),
        };

        var reader = new WirelessDetailReader(radio, clock);
        reader.Read(Adapter);

        radio.Association = null;
        clock.Advance(WirelessDetailReader.RememberedSsidLifetime + TimeSpan.FromMinutes(1));

        Assert.Null(reader.Read(Adapter));
    }

    [Fact]
    public void Reconnecting_to_another_network_replaces_the_remembered_name()
    {
        var clock = new ManualClock();
        var radio = new StubRadio
        {
            Association = new WirelessAssociation("KucaWiFi", "aa:bb:cc:dd:ee:01", 70),
        };

        var reader = new WirelessDetailReader(radio, clock);
        reader.Read(Adapter);

        radio.Association = new WirelessAssociation("KomsijaWiFi", "aa:bb:cc:dd:ee:77", 40);
        reader.Read(Adapter);

        radio.Association = null;
        clock.Advance(TimeSpan.FromMinutes(1));

        var wireless = reader.Read(Adapter);

        Assert.NotNull(wireless);
        Assert.Equal("KomsijaWiFi", wireless.Ssid);
    }

    // ---- Not knowing is its own answer -------------------------------------------

    /// <summary>
    /// A vanished SSID plus a radio believed to be on is read as the router having stopped
    /// broadcasting. Neither reading may be invented: with the radio state unknown, it stays
    /// unknown, and the classifier is left to say less rather than something wrong.
    /// </summary>
    [Fact]
    public void An_unknown_radio_state_is_carried_through_as_unknown()
    {
        var radio = new StubRadio
        {
            RadioOn = null,
            Association = new WirelessAssociation("KucaWiFi", "aa:bb:cc:dd:ee:01", 70),
            Visible = null,
        };

        var wireless = new WirelessDetailReader(radio, new ManualClock()).Read(Adapter);

        Assert.NotNull(wireless);
        Assert.Null(wireless.RadioOn);
        Assert.Null(wireless.SsidVisibleInScan);
    }

    [Fact]
    public void Trouble_on_the_link_asks_for_a_scan_sooner_than_the_healthy_interval()
    {
        var radio = new StubRadio();

        new WirelessDetailReader(radio, new ManualClock()).NoteTrouble();

        Assert.Equal(1, radio.UrgentScansRequested);
    }

    /// <summary>A radio that answers everything and shows nothing, driven by the test.</summary>
    private sealed class StubRadio : IWirelessRadio
    {
        public bool? RadioOn { get; set; }

        public WirelessAssociation? Association { get; set; }

        public Dictionary<string, WirelessAccessPoint> AccessPoints { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool? Visible { get; set; }

        public string? LastVisibilityQuestion { get; private set; }

        public int UrgentScansRequested { get; private set; }

        public bool? IsRadioOn(string interfaceId) => RadioOn;

        public WirelessAssociation? ReadAssociation(string interfaceId) => Association;

        public WirelessAccessPoint? ReadAccessPoint(string ssid, string bssid) =>
            AccessPoints.TryGetValue(bssid, out var found) ? found : null;

        public bool? IsSsidVisible(string ssid)
        {
            LastVisibilityQuestion = ssid;
            return Visible;
        }

        public void RequestUrgentScan() => UrgentScansRequested++;
    }
}
