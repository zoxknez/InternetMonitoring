using System.Net.Sockets;
using System.Text.Json;
using IEM.Core.Model;

namespace IEM.Core.Tests.Parity;

public enum ParityPlatform
{
    Windows,
    Linux,
}

public sealed record ParityGap(long BeforeSequence, TimeSpan StartedAt);

public sealed record ParityProjectedRun(
    IReadOnlyList<ProbeCycle> Cycles,
    IReadOnlyList<ParityGap> Gaps);

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
    private static readonly string[] ExternalIcmpV6Targets = ["2606:4700:4700::1111", "2001:4860:4860::8888", "2620:fe::fe"];
    private static readonly string[] ExternalTcpTargets = ["1.1.1.1:443", "8.8.8.8:443"];
    private const string GatewayIp = "192.168.1.1";
    private const string SourceAddress = "192.168.1.50";
    private const string InterfaceAlias = "if:monitored";

    public static IReadOnlyList<ProbeCycle> Project(ParityFixture fixture, ParityPlatform platform)
        => ProjectRun(fixture, platform).Cycles;

    public static ParityProjectedRun ProjectRun(ParityFixture fixture, ParityPlatform platform)
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
        var gaps = new List<ParityGap>();
        TimeSpan? pendingGapStartedAt = null;

        foreach (var tick in ticks.EnumerateArray())
        {
            var seq = tick.GetProperty("seq").GetInt32();
            if (IsHostAsleep(tick))
            {
                pendingGapStartedAt ??= cycles.Count == 0
                    ? ReadMonotonic(tick, seq)
                    : TimeSpan.FromTicks(cycles[^1].MonotonicTicks);
                continue;
            }

            if (pendingGapStartedAt is { } gapStartedAt)
            {
                gaps.Add(new ParityGap(seq, gapStartedAt));
                pendingGapStartedAt = null;
            }

            var platformTick = platformTicks.TryGetValue(seq, out var pt) ? pt : default;
            cycles.Add(ProjectTick(fixture.FixtureId, seq, tick, platformTick, sessionMedium, platform));
        }

        return new ParityProjectedRun(cycles, gaps);
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
        var internetReachableV4 = TaggedValue.Parse(world, "internetReachableV4");
        var internetReachableV6 = TaggedValue.Parse(world, "internetReachableV6");
        if (internetReachableV4.Tag == TaggedValueTag.Absent)
        {
            internetReachableV4 = internetReachable;
        }

        if (internetReachableV6.Tag == TaggedValueTag.Absent)
        {
            internetReachableV6 = internetReachable;
        }

        var snapshot = new LinkSnapshot("Parity Adapter", InterfaceAlias, status, medium)
        {
            GatewayAddress = hasGateway ? GatewayIp : null,
            LinkSpeedBitsPerSecond = 1_000_000_000,
            Wireless = medium == LinkMedium.Wireless ? ProjectWireless(platformTick) : null,
        };

        var pathAlias = link.ValueKind == JsonValueKind.Object && link.TryGetProperty("pathAlias", out var alias)
            ? alias.GetString() ?? InterfaceAlias
            : InterfaceAlias;
        var path = ProjectPath(platformTick, pathAlias);
        var results = new List<ProbeResult>();

        if (hasGateway)
        {
            results.Add(Probe(ProbeKind.Icmp, ProbeScope.Gateway, GatewayIp, Reaches(gatewayReachable), path));
        }

        AddExternalIcmp(results, platformTick, internetReachableV4, path);
        AddExternalIcmpV6(results, platformTick, internetReachableV6, path);
        AddExternalTcp(results, platformTick, internetReachable, path);
        results.Add(Probe(ProbeKind.TlsHandshake, ProbeScope.External, "one.one.one.one:443", Reaches(internetReachable), path));
        results.Add(Probe(ProbeKind.Http, ProbeScope.External, "http://connectivitycheck/", Reaches(internetReachable), path));
        AddDns(results, platformTick, internetReachable, path);

        var continuity = ReadPathContinuity(platformTick);
        for (var index = 0; index < results.Count; index++)
        {
            if (results[index].WasAttempted)
            {
                results[index] = results[index] with { PathContinuity = continuity };
            }
        }

        var monotonic = ReadMonotonic(tick, seq);
        var wall = tick.TryGetProperty("wallUtc", out var wallElement)
            ? DateTimeOffset.Parse(wallElement.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            : new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero).Add(monotonic);

        return new ProbeCycle(
            seq,
            wall,
            MonotonicTicks: monotonic.Ticks,
            snapshot,
            results,
            TimeSpan.FromMilliseconds(12));
    }

    private static WirelessSnapshot ProjectWireless(JsonElement platformTick)
    {
        var radioOn = ToNullableBool(TaggedValue.Parse(platformTick, "radioOn"));
        var ssidVisible = ToNullableBool(TaggedValue.Parse(platformTick, "ssidVisibleInScan"));

        // A negative scan observation is only evidence while the cache is fresh and complete.
        // Positive sightings survive a partial dump; absence does not (roadmap 25.6).
        if (ssidVisible == false && platformTick.TryGetProperty("scan", out var scan))
        {
            var complete = scan.TryGetProperty("completeness", out var completeness) &&
                completeness.GetString() == "Complete";
            var fresh = scan.TryGetProperty("ageMs", out var age) &&
                age.ValueKind == JsonValueKind.Number && age.GetInt64() <= 180_000;
            if (!complete || !fresh)
            {
                ssidVisible = null;
            }
        }

        return new WirelessSnapshot("ParityNet", "AA:BB:CC:DD:EE:FF", 80, 36)
        {
            SsidVisibleInScan = ssidVisible,
            RadioOn = radioOn,
            MeasuredRssiDbm = -55,
        };
    }

    private static ProbePath ProjectPath(JsonElement platformTick, string pathAlias)
    {
        if (platformTick.ValueKind != JsonValueKind.Object ||
            !platformTick.TryGetProperty("path", out var pathElement) ||
            pathElement.ValueKind != JsonValueKind.Object)
        {
            return new ProbePath(pathAlias, SourceAddress, Resolved: true, Bound: true);
        }

        var resolved = ToNullableBool(TaggedValue.Parse(pathElement, "resolved"));
        if (resolved != true)
        {
            return ProbePath.Unresolved;
        }

        var bound = ToNullableBool(TaggedValue.Parse(pathElement, "bound")) == true;
        return new ProbePath(pathAlias, SourceAddress, Resolved: true, Bound: bound);
    }

    private static PathContinuity ReadPathContinuity(JsonElement platformTick)
    {
        if (platformTick.ValueKind != JsonValueKind.Object ||
            !platformTick.TryGetProperty("path", out var path) ||
            path.ValueKind != JsonValueKind.Object)
        {
            return PathContinuity.Unknown;
        }

        var continuity = TaggedValue.Parse(path, "continuity");
        if (continuity.IsValueEqualTo("Held"))
        {
            return PathContinuity.Held;
        }

        return continuity.IsValueEqualTo("ChangedDuringExecution")
            ? PathContinuity.ChangedDuringExecution
            : PathContinuity.Unknown;
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

    private static void AddExternalIcmpV6(
        List<ProbeResult> results,
        JsonElement platformTick,
        TaggedValue internetReachable,
        ProbePath path)
    {
        var icmp = TaggedValue.Parse(platformTick, "icmpV6");
        if (icmp.Tag == TaggedValueTag.Absent)
        {
            return;
        }

        if (icmp.Tag is TaggedValueTag.Skipped or TaggedValueTag.Unavailable)
        {
            foreach (var target in ExternalIcmpV6Targets)
            {
                results.Add(ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.External, target, "icmpV6 not executed"));
            }

            return;
        }

        var reaches = icmp.Tag == TaggedValueTag.Value ? icmp.IsValueEqualTo("Success") : Reaches(internetReachable);
        foreach (var target in ExternalIcmpV6Targets)
        {
            results.Add(Probe(ProbeKind.Icmp, ProbeScope.External, target, reaches, path, AddressFamily.InterNetworkV6));
        }
    }

    private static void AddDns(
        List<ProbeResult> results,
        JsonElement platformTick,
        TaggedValue internetReachable,
        ProbePath path)
    {
        AddDnsProbe(results, platformTick, "dnsIspV4", GatewayIp, DnsResolverRole.IspAssigned, internetReachable, path);
        AddDnsProbe(results, platformTick, "dnsPublicV4", "1.1.1.1", DnsResolverRole.Public, internetReachable, path);
        AddDnsProbe(results, platformTick, "dnsSystem", "system", DnsResolverRole.System, internetReachable, path);
    }

    private static void AddDnsProbe(
        List<ProbeResult> results,
        JsonElement platformTick,
        string factName,
        string target,
        DnsResolverRole role,
        TaggedValue internetReachable,
        ProbePath path)
    {
        var fact = TaggedValue.Parse(platformTick, factName);
        if (fact.Tag is TaggedValueTag.Skipped or TaggedValueTag.Unavailable)
        {
            results.Add(ProbeResult.Skip(ProbeKind.Dns, ProbeScope.External, target, $"{factName} not executed") with
            {
                DnsRole = role,
                Family = AddressFamily.InterNetwork,
            });
            return;
        }

        var reaches = fact.Tag == TaggedValueTag.Value
            ? fact.IsValueEqualTo("Success")
            : Reaches(internetReachable);
        results.Add(Probe(ProbeKind.Dns, ProbeScope.External, target, reaches, path, AddressFamily.InterNetwork) with
        {
            DnsRole = role,
        });
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

    private static bool IsHostAsleep(JsonElement tick) =>
        tick.TryGetProperty("world", out var world) &&
        TaggedValue.Parse(world, "hostObservability").IsValueEqualTo("asleep");

    private static TimeSpan ReadMonotonic(JsonElement tick, int seq) =>
        tick.TryGetProperty("monotonicMs", out var monotonicElement)
            ? TimeSpan.FromMilliseconds(monotonicElement.GetInt64())
            : TimeSpan.FromSeconds(seq);
}
