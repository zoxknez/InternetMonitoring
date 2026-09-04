using System.Net.Sockets;
using System.Text.Json;
using IEM.Core.Model;

namespace IEM.Core.Tests.Parity;

public enum ParityPlatform
{
    Windows,
    Linux,
}

/// <summary>
/// Turns a fixture's one true story (<c>semanticInput</c>) plus one platform's observed facts
/// (<c>platformFacts.windows</c> / <c>.linux</c>) into the <see cref="ProbeCycle"/> list the
/// real Core classifier consumes - with no <c>IEM.Windows</c> or <c>IEM.Linux</c> in sight
/// (roadmap 25.13). The world decides whether a probe reaches; the platform facts decide what
/// this adapter actually managed to observe (radio, SSID, a skipped family, an unresolved path).
/// </summary>
public static class ParityProjection
{
    private static readonly string[] ExternalIcmpTargets = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];
    private static readonly string[] ExternalTcpTargets = ["1.1.1.1:443", "8.8.8.8:443"];
    private const string GatewayIp = "192.168.1.1";
    private const string SourceAddress = "192.168.1.50";
    private const string InterfaceAlias = "if:monitored";

    public static IReadOnlyList<ProbeCycle> Project(ParityFixture fixture, ParityPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var platformTicks = ReadPlatformTicks(
            platform == ParityPlatform.Windows ? fixture.PlatformFactsWindows : fixture.PlatformFactsLinux);

        if (!fixture.SemanticInput.TryGetProperty("ticks", out var ticks) || ticks.ValueKind != JsonValueKind.Array)
        {
            throw new ParityFixtureFormatException($"'{fixture.FixtureId}': semanticInput requires a ticks array.");
        }

        var sessionMedium = ReadMedium(fixture.SemanticInput);
        var cycles = new List<ProbeCycle>();

        foreach (var tick in ticks.EnumerateArray())
        {
            var seq = tick.GetProperty("seq").GetInt32();
            var platformTick = platformTicks.TryGetValue(seq, out var pt) ? pt : default;
            cycles.Add(ProjectTick(fixture.FixtureId, seq, tick, platformTick, sessionMedium, platform));
        }

        return cycles;
    }

    private static ProbeCycle ProjectTick(
        string fixtureId,
        int seq,
        JsonElement tick,
        JsonElement platformTick,
        LinkMedium sessionMedium,
        ParityPlatform platform)
    {
        var link = tick.TryGetProperty("link", out var linkElement) && linkElement.ValueKind == JsonValueKind.Object
            ? linkElement
            : default;

        var status = ReadLinkStatus(link);
        var medium = link.ValueKind == JsonValueKind.Object && link.TryGetProperty("medium", out var m)
            ? ParseMedium(m.GetString())
            : sessionMedium;
        var hasGateway = link.ValueKind != JsonValueKind.Object ||
            !link.TryGetProperty("hasGateway", out var hg) || hg.ValueKind != JsonValueKind.False;

        var world = tick.TryGetProperty("world", out var worldElement) && worldElement.ValueKind == JsonValueKind.Object
            ? worldElement
            : throw new ParityFixtureFormatException($"'{fixtureId}' tick {seq}: semanticInput tick requires a world.");

        var gatewayReachable = TaggedValue.Parse(world, "gatewayReachable");
        var internetReachable = TaggedValue.Parse(world, "internetReachable");

        var snapshot = new LinkSnapshot("Parity Adapter", InterfaceAlias, status, medium)
        {
            GatewayAddress = hasGateway ? GatewayIp : null,
            LinkSpeedBitsPerSecond = 1_000_000_000,
            Wireless = medium == LinkMedium.Wireless ? ProjectWireless(platformTick) : null,
        };

        var path = ProjectPath(platformTick);
        var results = new List<ProbeResult>();

        if (hasGateway)
        {
            results.Add(Probe(ProbeKind.Icmp, ProbeScope.Gateway, GatewayIp, Reaches(gatewayReachable), path));
        }

        AddExternalIcmp(results, platformTick, internetReachable, path);
        AddExternalTcp(results, platformTick, internetReachable, path);
        results.Add(Probe(ProbeKind.TlsHandshake, ProbeScope.External, "one.one.one.one:443", Reaches(internetReachable), path));
        results.Add(Probe(ProbeKind.Http, ProbeScope.External, "http://connectivitycheck/", Reaches(internetReachable), path));
        AddDns(results, internetReachable, path);

        return new ProbeCycle(
            seq,
            new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero).AddSeconds(seq),
            MonotonicTicks: seq * TimeSpan.TicksPerSecond,
            snapshot,
            results,
            TimeSpan.FromMilliseconds(12));
    }

    private static WirelessSnapshot ProjectWireless(JsonElement platformTick)
    {
        var radioOn = ToNullableBool(TaggedValue.Parse(platformTick, "radioOn"));
        var ssidVisible = ToNullableBool(TaggedValue.Parse(platformTick, "ssidVisibleInScan"));

        return new WirelessSnapshot("ParityNet", "AA:BB:CC:DD:EE:FF", 80, 36)
        {
            SsidVisibleInScan = ssidVisible,
            RadioOn = radioOn,
            MeasuredRssiDbm = -55,
        };
    }

    private static ProbePath ProjectPath(JsonElement platformTick)
    {
        if (platformTick.ValueKind != JsonValueKind.Object ||
            !platformTick.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.Object)
        {
            return new ProbePath(InterfaceAlias, SourceAddress, Resolved: true, Bound: true);
        }

        var resolved = ToNullableBool(TaggedValue.Parse(pathElement, "resolved"));
        if (resolved != true)
        {
            return ProbePath.Unresolved;
        }

        var bound = ToNullableBool(TaggedValue.Parse(pathElement, "bound")) == true;
        return new ProbePath(InterfaceAlias, SourceAddress, Resolved: true, Bound: bound);
    }

    private static void AddExternalIcmp(
        List<ProbeResult> results,
        JsonElement platformTick,
        TaggedValue internetReachable,
        ProbePath path)
    {
        var icmp = TaggedValue.Parse(platformTick, "icmpV4");
        if (icmp.Tag is TaggedValueTag.Skipped or TaggedValueTag.Unavailable)
        {
            foreach (var target in ExternalIcmpTargets)
            {
                results.Add(ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.External, target, "icmpV4 not executed"));
            }

            return;
        }

        var reaches = icmp.Tag == TaggedValueTag.Value ? icmp.IsValueEqualTo("Success") : Reaches(internetReachable);
        foreach (var target in ExternalIcmpTargets)
        {
            results.Add(Probe(ProbeKind.Icmp, ProbeScope.External, target, reaches, path, AddressFamily.InterNetwork));
        }
    }

    private static void AddExternalTcp(
        List<ProbeResult> results,
        JsonElement platformTick,
        TaggedValue internetReachable,
        ProbePath path)
    {
        var tcp = TaggedValue.Parse(platformTick, "tcp");
        if (tcp.Tag is TaggedValueTag.Skipped)
        {
            foreach (var target in ExternalTcpTargets)
            {
                results.Add(ProbeResult.Skip(ProbeKind.TcpConnect, ProbeScope.External, target, "tcp bind skipped"));
            }

            return;
        }

        var reaches = tcp.Tag == TaggedValueTag.Value
            ? tcp.IsValueEqualTo("Success")
            : Reaches(internetReachable);

        foreach (var target in ExternalTcpTargets)
        {
            results.Add(Probe(ProbeKind.TcpConnect, ProbeScope.External, target, reaches, path));
        }
    }

    private static void AddDns(List<ProbeResult> results, TaggedValue internetReachable, ProbePath path)
    {
        var reaches = Reaches(internetReachable);
        results.Add(Probe(ProbeKind.Dns, ProbeScope.External, GatewayIp, reaches, path) with { DnsRole = DnsResolverRole.IspAssigned });
        results.Add(Probe(ProbeKind.Dns, ProbeScope.External, "1.1.1.1", reaches, path) with { DnsRole = DnsResolverRole.Public });
        results.Add(Probe(ProbeKind.Dns, ProbeScope.External, "system", reaches, path) with { DnsRole = DnsResolverRole.System });
    }

    private static ProbeResult Probe(
        ProbeKind kind,
        ProbeScope scope,
        string target,
        bool succeeds,
        ProbePath path,
        AddressFamily? family = null) =>
        new(kind, scope, target, succeeds ? ProbeOutcome.Success : ProbeOutcome.TimedOut,
            succeeds ? TimeSpan.FromMilliseconds(20) : null)
        {
            Freshness = Freshness.Fresh,
            Path = path,
            Family = family,
        };

    private static bool Reaches(TaggedValue world) => world.IsValueEqualTo(true);

    private static bool? ToNullableBool(TaggedValue value) => value.Tag switch
    {
        TaggedValueTag.Value when value.IsValueEqualTo(true) => true,
        TaggedValueTag.Value when value.IsValueEqualTo(false) => false,
        TaggedValueTag.Value when value.Value?.ValueKind == JsonValueKind.String =>
            value.IsValueEqualTo("true") ? true : value.IsValueEqualTo("false") ? false : null,
        _ => null,
    };

    private static IReadOnlyDictionary<int, JsonElement> ReadPlatformTicks(JsonElement facts)
    {
        var result = new Dictionary<int, JsonElement>();
        if (!facts.TryGetProperty("ticks", out var ticks) || ticks.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var tick in ticks.EnumerateArray())
        {
            if (tick.TryGetProperty("seq", out var seq) && seq.TryGetInt32(out var value))
            {
                result[value] = tick;
            }
        }

        return result;
    }

    private static LinkMedium ReadMedium(JsonElement semanticInput) =>
        semanticInput.TryGetProperty("session", out var session) &&
        session.TryGetProperty("medium", out var medium)
            ? ParseMedium(medium.GetString())
            : LinkMedium.Ethernet;

    private static LinkMedium ParseMedium(string? raw) => raw switch
    {
        "Wireless" => LinkMedium.Wireless,
        "Ethernet" => LinkMedium.Ethernet,
        _ => LinkMedium.Unknown,
    };

    private static LinkStatus ReadLinkStatus(JsonElement link) =>
        link.ValueKind == JsonValueKind.Object && link.TryGetProperty("status", out var status)
            ? status.GetString() == "Down" ? LinkStatus.Down : LinkStatus.Up
            : LinkStatus.Up;
}
