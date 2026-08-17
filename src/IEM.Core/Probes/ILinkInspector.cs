using System.Net.NetworkInformation;
using System.Net.Sockets;
using IEM.Core.Model;

namespace IEM.Core.Probes;

/// <summary>Reads the current state of the monitored network interface.</summary>
public interface ILinkInspector
{
    LinkSnapshot Inspect();
}

/// <summary>
/// Reads link state through <see cref="NetworkInterface"/>.
/// <para>
/// Deliberately platform-neutral. Wireless detail - signal strength, BSSID, and above all
/// whether the SSID is still being broadcast - needs the Windows native Wi-Fi API and
/// arrives through a decorator, so this core stays testable and portable.
/// </para>
/// </summary>
public sealed class SystemLinkInspector(string? preferredInterfaceName = null) : ILinkInspector
{
    private readonly string? _preferredInterfaceName = preferredInterfaceName;

    public LinkSnapshot Inspect()
    {
        var candidate = SelectInterface();
        if (candidate is null)
        {
            return LinkSnapshot.Unavailable(_preferredInterfaceName ?? string.Empty);
        }

        var properties = candidate.GetIPProperties();

        var gateway = properties.GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a is not null && !a.Equals(System.Net.IPAddress.Any) && !a.Equals(System.Net.IPAddress.IPv6Any));

        return new LinkSnapshot(
            candidate.Name,
            candidate.Id,
            candidate.OperationalStatus == OperationalStatus.Up ? LinkStatus.Up : LinkStatus.Down,
            MediumOf(candidate.NetworkInterfaceType))
        {
            LinkSpeedBitsPerSecond = candidate.Speed > 0 ? candidate.Speed : null,
            GatewayAddress = gateway?.ToString(),
            MacAddress = FormatMac(candidate.GetPhysicalAddress()),
            DnsServers = [.. properties.DnsAddresses
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Select(a => a.ToString())],
        };
    }

    /// <summary>Colon-separated upper case, the form a router's own status page uses.</summary>
    private static string? FormatMac(System.Net.NetworkInformation.PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();

        return bytes.Length == 0 ? null : string.Join(':', bytes.Select(b => b.ToString("X2", null)));
    }

    private NetworkInterface? SelectInterface()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        if (all.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_preferredInterfaceName))
        {
            var named = all.FirstOrDefault(n =>
                string.Equals(n.Name, _preferredInterfaceName, StringComparison.OrdinalIgnoreCase));

            // Honour the explicit choice even when it is down - that is very often the
            // whole point of monitoring it.
            if (named is not null)
            {
                return named;
            }
        }

        // Otherwise take the interface that currently carries a default route, since that
        // is the one whose failure the customer would actually notice. Ethernet wins a tie:
        // Windows gives wired links the lower route metric, so on a machine that is both
        // plugged in and on Wi-Fi, the cable is what traffic is really using.
        return all
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .OrderByDescending(n => MediumOf(n.NetworkInterfaceType) == LinkMedium.Ethernet)
            .ThenByDescending(n => n.GetIPProperties().DnsAddresses.Count > 0)
            .FirstOrDefault()
            ?? all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up)
            ?? all[0];
    }

    private static LinkMedium MediumOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => LinkMedium.Wireless,
        NetworkInterfaceType.Ethernet or
        NetworkInterfaceType.GigabitEthernet or
        NetworkInterfaceType.FastEthernetT or
        NetworkInterfaceType.FastEthernetFx or
        NetworkInterfaceType.Ethernet3Megabit => LinkMedium.Ethernet,
        NetworkInterfaceType.Unknown => LinkMedium.Unknown,
        _ => LinkMedium.Other,
    };
}
