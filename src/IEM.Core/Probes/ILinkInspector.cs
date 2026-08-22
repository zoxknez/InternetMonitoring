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
/// whether the SSID is still being broadcast - needs platform-specific Wi-Fi APIs and
/// arrives through decorators, so this core stays testable and portable.
/// </para>
/// <para>
/// Invariants:
/// WIN_SESSION_INTERFACE_IMMUTABLE: Pinned interface cannot change during session.
/// WIN_INTERFACE_FAILURE_NEVER_CAUSES_RESELECTION: Missing/Down interface records failure, never falls back.
/// </para>
/// </summary>
public sealed class SystemLinkInspector : ILinkInspector
{
    private readonly string? _preferredInterfaceName;
    private readonly string? _preferredInterfaceId;

    public SystemLinkInspector(string? preferredInterface = null)
    {
        _preferredInterfaceName = preferredInterface;
        _preferredInterfaceId = preferredInterface;
    }

    public SystemLinkInspector(string? preferredInterfaceId, string? preferredInterfaceName)
    {
        _preferredInterfaceId = preferredInterfaceId;
        _preferredInterfaceName = preferredInterfaceName;
    }

    public SystemLinkInspector(MonitoredInterfaceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _preferredInterfaceId = identity.InterfaceId;
        _preferredInterfaceName = identity.InterfaceName;
    }

    public LinkSnapshot Inspect()
    {
        var candidate = SelectInterface();
        if (candidate is null)
        {
            return LinkSnapshot.Unavailable(_preferredInterfaceName ?? string.Empty, _preferredInterfaceId ?? string.Empty);
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

        var hasPinnedTarget = !string.IsNullOrWhiteSpace(_preferredInterfaceId) || !string.IsNullOrWhiteSpace(_preferredInterfaceName);
        if (hasPinnedTarget)
        {
            // Invariant: WIN_INTERFACE_FAILURE_NEVER_CAUSES_RESELECTION
            // Match canonical Id first, then Name. If not found, return null (Inspect returns Missing).
            // NEVER fall through to auto-selection or another adapter.
            return all.FirstOrDefault(MatchesPinned);
        }

        // Auto mode (before session start): select physical default route carrier.
        return all
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .OrderByDescending(n => MediumOf(n.NetworkInterfaceType) == LinkMedium.Ethernet)
            .ThenByDescending(n => n.GetIPProperties().DnsAddresses.Count > 0)
            .FirstOrDefault()
            ?? all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up)
            ?? all[0];
    }

    private bool MatchesPinned(NetworkInterface n)
    {
        if (!string.IsNullOrWhiteSpace(_preferredInterfaceId))
        {
            if (string.Equals(n.Id, _preferredInterfaceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Normalise GUID comparison if applicable
            if (Guid.TryParse(_preferredInterfaceId.Trim().Trim('{', '}'), out var targetGuid) &&
                Guid.TryParse(n.Id.Trim().Trim('{', '}'), out var nicGuid) &&
                targetGuid == nicGuid)
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(_preferredInterfaceName) &&
            string.Equals(n.Name, _preferredInterfaceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
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
