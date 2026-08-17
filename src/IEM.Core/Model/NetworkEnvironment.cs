using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Model;

/// <summary>
/// What the connection under test actually consisted of, at one moment.
/// <para>
/// A two-day test is only evidence about one connection if it was the same connection
/// throughout. Someone who moves a laptop from home Wi-Fi to a phone hotspot has produced a
/// perfectly valid recording of two different networks, and a report that presents it as one
/// is wrong in a way an operator will spot immediately - it is, after all, their own
/// equipment they will be checking it against.
/// </para>
/// <para>
/// So the environment is canonicalised and hashed at the start, and re-checked as the
/// session runs. A change is not an error; it is a fact that has to appear in the record.
/// </para>
/// </summary>
public sealed record NetworkEnvironment
{
    public required string InterfaceId { get; init; }

    public required string InterfaceName { get; init; }

    public required LinkMedium Medium { get; init; }

    public string? MacAddress { get; init; }

    public string? GatewayAddress { get; init; }

    /// <summary>
    /// The gateway's own MAC. Changes when the router is swapped, which a customer may not
    /// think to mention and which changes what the evidence is about.
    /// </summary>
    public string? GatewayMac { get; init; }

    public IReadOnlyList<string> SourceAddresses { get; init; } = [];

    public IReadOnlyList<string> DnsServers { get; init; } = [];

    public long? LinkSpeedBitsPerSecond { get; init; }

    public string? Ssid { get; init; }

    public string? Bssid { get; init; }

    public string? DriverDescription { get; init; }

    /// <summary>A VPN or similar virtual adapter was up, which can carry traffic away from the link.</summary>
    public bool VirtualAdapterPresent { get; init; }

    /// <summary>Reads the environment off a link snapshot and the paths probes resolved.</summary>
    /// <param name="sourceAddresses">Source addresses probes actually left from.</param>
    /// <param name="virtualAdapterPresent">A VPN or similar adapter was up at the time.</param>
    public static NetworkEnvironment From(
        LinkSnapshot link,
        IReadOnlyList<string>? sourceAddresses = null,
        bool virtualAdapterPresent = false)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new NetworkEnvironment
        {
            InterfaceId = link.InterfaceId,
            InterfaceName = link.InterfaceName,
            Medium = link.Medium,
            MacAddress = link.MacAddress,
            GatewayAddress = link.GatewayAddress,
            SourceAddresses = sourceAddresses ?? [],
            DnsServers = link.DnsServers,
            LinkSpeedBitsPerSecond = link.LinkSpeedBitsPerSecond,
            Ssid = link.Wireless?.Ssid,
            Bssid = link.Wireless?.Bssid,
            VirtualAdapterPresent = virtualAdapterPresent,
        };
    }

    /// <summary>
    /// Stable digest of everything above.
    /// <para>
    /// Fields are written in a fixed order with an explicit culture, because the digest has
    /// to match across machines and across builds. Anything that varies with the reader -
    /// property ordering, locale, enum formatting - would make every session look like it
    /// changed environment on the first sample.
    /// </para>
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var builder = new StringBuilder();

            Append(builder, "iface", InterfaceId);
            Append(builder, "name", InterfaceName);
            Append(builder, "medium", Medium.ToString());
            Append(builder, "mac", MacAddress);
            Append(builder, "gw", GatewayAddress);
            Append(builder, "gwMac", GatewayMac);
            Append(builder, "src", string.Join(',', SourceAddresses.Order(StringComparer.Ordinal)));
            Append(builder, "dns", string.Join(',', DnsServers.Order(StringComparer.Ordinal)));
            Append(builder, "speed", LinkSpeedBitsPerSecond?.ToString(CultureInfo.InvariantCulture));
            Append(builder, "ssid", Ssid);
            Append(builder, "bssid", Bssid);
            Append(builder, "driver", DriverDescription);
            Append(builder, "virtual", VirtualAdapterPresent ? "1" : "0");

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }

    /// <summary>
    /// Differences a reader would care about, in Serbian, ready for the log and the report.
    /// Empty when nothing that matters changed.
    /// </summary>
    public IReadOnlyList<string> DifferencesFrom(NetworkEnvironment other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var changes = new List<string>();

        Compare("Adapter", other.InterfaceName, InterfaceName);
        Compare("Medij", Label(other.Medium), Label(Medium));
        Compare("MAC adresa", other.MacAddress, MacAddress);
        Compare("Podrazumevani mrežni prolaz", other.GatewayAddress, GatewayAddress);
        Compare("MAC mrežnog prolaza", other.GatewayMac, GatewayMac);
        Compare("Izvorišna adresa", Join(other.SourceAddresses), Join(SourceAddresses));
        Compare("DNS serveri", Join(other.DnsServers), Join(DnsServers));
        Compare("Wi-Fi mreža", other.Ssid, Ssid);
        Compare("Pristupna tačka", other.Bssid, Bssid);
        Compare("Brzina linka", Speed(other.LinkSpeedBitsPerSecond), Speed(LinkSpeedBitsPerSecond));

        if (other.VirtualAdapterPresent != VirtualAdapterPresent)
        {
            changes.Add(VirtualAdapterPresent
                ? "Pojavio se virtuelni adapter (VPN ili sličan)"
                : "Nestao je virtuelni adapter (VPN ili sličan)");
        }

        return changes;

        void Compare(string label, string? before, string? after)
        {
            if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add($"{label} promenjen: {before ?? "nepoznato"} → {after ?? "nepoznato"}");
            }
        }
    }

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "nepoznato" : string.Join(", ", values.Order(StringComparer.Ordinal));

    private static string? Speed(long? bitsPerSecond) => bitsPerSecond is { } value
        ? $"{value / 1_000_000d:0.###} Mbit/s"
        : null;

    private static string Label(LinkMedium medium) => medium switch
    {
        LinkMedium.Ethernet => "Ethernet",
        LinkMedium.Wireless => "Wi-Fi",
        LinkMedium.Other => "drugi tip veze",
        _ => "nepoznat",
    };

    private static void Append(StringBuilder builder, string name, string? value) =>
        builder.Append(name).Append('=').Append(value ?? string.Empty).Append('\n');
}
