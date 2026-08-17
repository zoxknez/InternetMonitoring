using IEM.Core.Model;

namespace IEM.Core.Tests;

/// <summary>
/// A forty-eight hour recording is evidence about one connection only if it was the same
/// connection throughout. Someone who carries a laptop from home Wi-Fi to a phone hotspot
/// has recorded two networks accurately; presenting that as one continuous measurement is
/// the thing that would be false - and an operator checking it against their own equipment
/// would find the discrepancy long before anyone else did.
/// </summary>
public sealed class NetworkEnvironmentTests
{
    private static NetworkEnvironment Home() => new()
    {
        InterfaceId = "{ETH}",
        InterfaceName = "Ethernet",
        Medium = LinkMedium.Ethernet,
        MacAddress = "AA:BB:CC:DD:EE:FF",
        GatewayAddress = "192.168.1.1",
        SourceAddresses = ["192.168.1.102"],
        DnsServers = ["192.168.1.1", "1.1.1.1"],
        LinkSpeedBitsPerSecond = 1_000_000_000,
    };

    [Fact]
    public void The_same_environment_produces_the_same_fingerprint()
    {
        Assert.Equal(Home().Fingerprint, Home().Fingerprint);
    }

    /// <summary>
    /// The digest has to survive being computed on a different machine, in a different
    /// locale, by a later build. Anything that varies with the reader would make every
    /// session appear to change environment on its very first sample.
    /// </summary>
    [Fact]
    public void The_order_addresses_arrive_in_does_not_change_the_fingerprint()
    {
        var reordered = Home() with { DnsServers = ["1.1.1.1", "192.168.1.1"] };

        Assert.Equal(Home().Fingerprint, reordered.Fingerprint);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("medium")]
    [InlineData("mac")]
    [InlineData("ssid")]
    [InlineData("dns")]
    [InlineData("source")]
    public void Anything_that_changes_what_is_being_measured_changes_the_fingerprint(string field)
    {
        var changed = field switch
        {
            "gateway" => Home() with { GatewayAddress = "192.168.0.1" },
            "medium" => Home() with { Medium = LinkMedium.Wireless },
            "mac" => Home() with { MacAddress = "11:22:33:44:55:66" },
            "ssid" => Home() with { Ssid = "MojaMreza" },
            "dns" => Home() with { DnsServers = ["8.8.8.8"] },
            _ => Home() with { SourceAddresses = ["10.0.0.5"] },
        };

        Assert.NotEqual(Home().Fingerprint, changed.Fingerprint);
    }

    [Fact]
    public void Differences_are_spelled_out_rather_than_left_to_a_hash()
    {
        var moved = Home() with
        {
            Medium = LinkMedium.Wireless,
            GatewayAddress = "192.168.0.1",
            Ssid = "Hotspot",
        };

        var differences = moved.DifferencesFrom(Home());

        Assert.Contains(differences, d => d.Contains("Medij", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("192.168.1.1", StringComparison.Ordinal) &&
                                          d.Contains("192.168.0.1", StringComparison.Ordinal));
        Assert.Contains(differences, d => d.Contains("Hotspot", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unchanged_environment_reports_no_differences()
    {
        Assert.Empty(Home().DifferencesFrom(Home()));
    }

    [Fact]
    public void A_vpn_appearing_is_reported_in_words()
    {
        var withVpn = Home() with { VirtualAdapterPresent = true };

        Assert.Contains(
            withVpn.DifferencesFrom(Home()),
            d => d.Contains("virtuelni adapter", StringComparison.OrdinalIgnoreCase));
    }
}
