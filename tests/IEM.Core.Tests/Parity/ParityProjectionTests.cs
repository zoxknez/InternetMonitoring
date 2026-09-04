namespace IEM.Core.Tests.Parity;

/// <summary>
/// 3.1-12 slice 2: the projection (semanticInput + one platform's facts -> ProbeCycle[]) and
/// the CanonicalParityView run through the real StateClassifier. The path differ and the full
/// §25.12 catalogue are slice 3.
/// </summary>
public sealed class ParityProjectionTests
{
    private static string Fixture(
        string id,
        string kind,
        string windowsTick,
        string linuxTick,
        string world,
        string divergences = "[]",
        string medium = "Ethernet",
        string link = "") =>
        $$"""
        {
          "parityFixtureVersion": 1,
          "fixtureId": "{{id}}",
          "scenario": "s",
          "intent": "i",
          "kind": "{{kind}}",
          "tags": ["t"],
          "semanticInput": {
            "session": { "plannedDuration": "PT2M", "medium": "{{medium}}" },
            "ticks": [ { "seq": 1, {{(link.Length == 0 ? "" : link + ",")}} "world": {{world}} } ]
          },
          "platformFacts": {
            "windows": { "ticks": [ { "seq": 1, {{windowsTick}} } ] },
            "linux": { "ticks": [ { "seq": 1, {{linuxTick}} } ] }
          },
          "expectedCanonicalOutput": { "samples": [] },
          "allowedDivergences": {{divergences}},
          "pathWhitelist": []
        }
        """;

    private const string HealthyWorld = """{ "gatewayReachable": { "t": "v", "v": true }, "internetReachable": { "t": "v", "v": true } }""";
    private const string ResolvedPath = "\"path\": { \"resolved\": { \"t\": \"v\", \"v\": true }, \"bound\": { \"t\": \"v\", \"v\": true } }";

    [Fact]
    public void Healthy_dual_stack_projects_identically_on_both_platforms()
    {
        var fixture = ParityFixture.Parse(Fixture(
            "parity.healthy.dual-stack", "symmetric",
            windowsTick: ResolvedPath,
            linuxTick: ResolvedPath,
            world: HealthyWorld));

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        Assert.Equal("Ok", windows.Samples[0].NetworkState);
        Assert.False(windows.Samples[0].IsOutage);
        Assert.Equal(windows.Sha256, linux.Sha256);
    }

    [Fact]
    public void Gateway_down_is_a_symmetric_local_fault()
    {
        var world = """{ "gatewayReachable": { "t": "v", "v": false }, "internetReachable": { "t": "v", "v": false } }""";
        var fixture = ParityFixture.Parse(Fixture(
            "parity.outage.gateway-down", "symmetric", ResolvedPath, ResolvedPath, world));

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        Assert.Equal("GatewayDown", windows.Samples[0].NetworkState);
        Assert.True(windows.Samples[0].IsOutage);
        Assert.Equal(windows.Sha256, linux.Sha256);
    }

    [Fact]
    public void Cpe_upstream_unreachable_is_symmetric_when_the_gateway_still_answers()
    {
        var world = """{ "gatewayReachable": { "t": "v", "v": true }, "internetReachable": { "t": "v", "v": false } }""";
        var fixture = ParityFixture.Parse(Fixture(
            "parity.outage.cpe-upstream", "symmetric", ResolvedPath, ResolvedPath, world));

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        Assert.Equal("CpeUpstreamUnreachable", windows.Samples[0].NetworkState);
        Assert.Equal(windows.Sha256, linux.Sha256);
    }

    [Fact]
    public void Icmp_skipped_on_one_side_diverges_only_where_the_fixture_permits()
    {
        // Windows executes ICMP (all time out); Linux never gets an ICMP datagram socket.
        // Both keep TCP, so neither is an outage - but the classifier and the ICMP tally differ.
        var windowsTick = $"\"icmpV4\": {{ \"t\": \"v\", \"v\": \"TimedOut\" }}, {ResolvedPath}";
        var linuxTick = $"\"icmpV4\": {{ \"t\": \"skipped\" }}, {ResolvedPath}";
        var divergences = """
        [
          { "path": "$.samples[0].networkState", "windows": "IcmpFiltered", "linux": "Ok",
            "reason": "Linux ICMP datagram socket unavailable; TCP holds on both, isOutage=false either way." },
          { "path": "$.samples[0].tallies.externalIcmp.attempted", "windows": 3, "linux": 0,
            "reason": "A skipped family is not an attempt (invariant on tallies)." }
        ]
        """;

        var fixture = ParityFixture.Parse(Fixture(
            "parity.icmp.v4-denied.tcp-ok", "asymmetric-observability",
            windowsTick, linuxTick, HealthyWorld, divergences));

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        Assert.Equal("IcmpFiltered", windows.Samples[0].NetworkState);
        Assert.Equal("Ok", linux.Samples[0].NetworkState);
        Assert.False(windows.Samples[0].IsOutage);
        Assert.False(linux.Samples[0].IsOutage);
        Assert.Equal(3, windows.Samples[0].Tallies.ExternalIcmp.Attempted);
        Assert.Equal(0, linux.Samples[0].Tallies.ExternalIcmp.Attempted);
        Assert.NotEqual(windows.Sha256, linux.Sha256);
    }

    [Fact]
    public void Canonical_json_is_rfc8785_minimal_and_key_sorted()
    {
        var fixture = ParityFixture.Parse(Fixture(
            "parity.healthy.dual-stack", "symmetric", ResolvedPath, ResolvedPath, HealthyWorld));

        var json = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows)).CanonicalJson;

        Assert.DoesNotContain(" ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", json, StringComparison.Ordinal);
        // RFC 8785 orders object members by UTF-16 code unit: "anyExternalReachability" before "isOutage".
        Assert.True(
            json.IndexOf("anyExternalReachability", StringComparison.Ordinal) <
            json.IndexOf("isOutage", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unresolved_path_does_not_prove_the_link()
    {
        var unresolved = "\"path\": { \"resolved\": { \"t\": \"v\", \"v\": false } }";
        var fixture = ParityFixture.Parse(Fixture(
            "parity.unresolved-route.tcp-runs", "symmetric", unresolved, unresolved, HealthyWorld));

        var view = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        Assert.False(view.Samples[0].PathProvesLink);
    }
}
