namespace IEM.Core.Tests.Parity;

public sealed class ParityDifferTests
{
    private static string Fixture(string kind, string windowsIcmp, string linuxIcmp, string divergences) =>
        $$"""
        {
          "parityFixtureVersion": 1,
          "fixtureId": "parity.icmp.differ-probe",
          "scenario": "s", "intent": "i", "kind": "{{kind}}", "tags": ["t"],
          "semanticInput": {
            "session": { "medium": "Ethernet" },
            "ticks": [ { "seq": 1, "link": { "status": "Up", "hasGateway": true },
              "world": { "gatewayReachable": { "t": "v", "v": true }, "internetReachable": { "t": "v", "v": true } } } ]
          },
          "platformFacts": {
            "windows": { "ticks": [ { "seq": 1, {{windowsIcmp}} "path": { "resolved": { "t": "v", "v": true } } } ] },
            "linux": { "ticks": [ { "seq": 1, {{linuxIcmp}} "path": { "resolved": { "t": "v", "v": true } } } ] }
          },
          "expectedCanonicalOutput": { "samples": [] },
          "allowedDivergences": {{divergences}},
          "pathWhitelist": []
        }
        """;

    [Fact]
    public void Identical_views_produce_only_identical_lines()
    {
        var fixture = ParityFixture.Parse(Fixture("symmetric", "", "", "[]"));
        var view = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));

        var diff = ParityDiffer.Diff(view, view, fixture);

        Assert.False(diff.HasForbidden);
        Assert.Empty(diff.Divergences);
        Assert.Equal("identical", diff.Report());
    }

    [Fact]
    public void An_unlisted_divergence_is_forbidden_and_named_in_the_report()
    {
        // Windows runs ICMP (times out -> IcmpFiltered); Linux skips it (-> Ok). No allowedDivergences.
        var fixture = ParityFixture.Parse(Fixture(
            "symmetric",
            "\"icmpV4\": { \"t\": \"v\", \"v\": \"TimedOut\" },",
            "\"icmpV4\": { \"t\": \"skipped\" },",
            "[]"));

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        var diff = ParityDiffer.Diff(windows, linux, fixture);

        Assert.True(diff.HasForbidden);
        Assert.Contains("FORBIDDEN  $.samples[0].networkState", diff.Report(), StringComparison.Ordinal);
        Assert.Contains("windows: IcmpFiltered", diff.Report(), StringComparison.Ordinal);
        Assert.Contains("linux:   Ok", diff.Report(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_listed_divergence_with_matching_values_is_allowed()
    {
        var divergences = """
        [
          { "path": "$.samples[*].networkState", "windows": "IcmpFiltered", "linux": "Ok", "reason": "ICMP capability asymmetry" },
          { "path": "$.samples[*].tallies.externalIcmp.attempted", "windows": 3, "linux": 0, "reason": "skip is not an attempt" }
        ]
        """;
        var fixture = ParityFixture.Parse(Fixture(
            "asymmetric-observability",
            "\"icmpV4\": { \"t\": \"v\", \"v\": \"TimedOut\" },",
            "\"icmpV4\": { \"t\": \"skipped\" },",
            divergences));

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        var diff = ParityDiffer.Diff(windows, linux, fixture);

        Assert.False(diff.HasForbidden);
        Assert.Contains(diff.Divergences, line => line is { Path: "$.samples[0].networkState", Verdict: ParityVerdict.Allowed });
    }

    [Fact]
    public void A_listed_divergence_whose_values_do_not_match_the_observed_ones_is_still_forbidden()
    {
        // The fixture claims linux would be "AdapterDown", but the projection yields "Ok".
        var divergences = """
        [ { "path": "$.samples[*].networkState", "windows": "IcmpFiltered", "linux": "AdapterDown", "reason": "wrong claim" } ]
        """;
        var fixture = ParityFixture.Parse(Fixture(
            "asymmetric-observability",
            "\"icmpV4\": { \"t\": \"v\", \"v\": \"TimedOut\" },",
            "\"icmpV4\": { \"t\": \"skipped\" },",
            divergences));

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));

        var diff = ParityDiffer.Diff(windows, linux, fixture);

        Assert.True(diff.HasForbidden);
    }
}
