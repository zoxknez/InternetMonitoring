using System.Text.Json;

namespace IEM.Core.Tests.Parity;

/// <summary>
/// 3.1-12 slice 1: the fixture parser and the fixture linter. The projection through the real
/// Core classifier, the CanonicalParityView and the ParityDiffer arrive in later slices; this
/// pins the format so those slices build on something stable.
/// </summary>
public sealed class ParityFixtureFormatTests
{
    private const string HealthySymmetric = """
    {
      "parityFixtureVersion": 1,
      "fixtureId": "parity.healthy.dual-stack",
      "scenario": "All families succeed",
      "intent": "Ok on both platforms, no incident, Stable",
      "kind": "symmetric",
      "tags": ["healthy"],
      "semanticInput": {
        "session": { "plannedDuration": "PT2M", "medium": "Wireless" },
        "ticks": [
          {
            "seq": 1,
            "world": {
              "radioSwitch": { "t": "v", "v": "on" },
              "ssidOnAir": { "t": "v", "v": true }
            }
          }
        ]
      },
      "platformFacts": {
        "windows": { "ticks": [ { "seq": 1, "radioOn": { "t": "v", "v": true }, "ssidVisibleInScan": { "t": "v", "v": true } } ] },
        "linux": { "ticks": [ { "seq": 1, "radioOn": { "t": "v", "v": true }, "ssidVisibleInScan": { "t": "null" } } ] }
      },
      "expectedCanonicalOutput": { "samples": [] },
      "allowedDivergences": [],
      "pathWhitelist": []
    }
    """;

    [Fact]
    public void Valid_symmetric_fixture_parses_and_lints_clean()
    {
        var fixture = ParityFixture.Parse(HealthySymmetric);

        Assert.Equal(1, fixture.ParityFixtureVersion);
        Assert.Equal("parity.healthy.dual-stack", fixture.FixtureId);
        Assert.Equal(ParityFixtureKind.Symmetric, fixture.Kind);
        Assert.Empty(fixture.AllowedDivergences);
        Assert.Empty(ParityFixtureLinter.Lint(fixture));
    }

    [Fact]
    public void Unsupported_fixture_version_is_rejected()
    {
        var json = HealthySymmetric.Replace("\"parityFixtureVersion\": 1", "\"parityFixtureVersion\": 2");

        var ex = Assert.Throws<ParityFixtureFormatException>(() => ParityFixture.Parse(json));
        Assert.Contains("parityFixtureVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bare_json_null_where_a_tagged_value_is_required_is_a_format_error()
    {
        var json = HealthySymmetric.Replace("\"ssidVisibleInScan\": { \"t\": \"v\", \"v\": true }", "\"ssidVisibleInScan\": null");

        var ex = Assert.Throws<ParityFixtureFormatException>(() =>
        {
            var fixture = ParityFixture.Parse(json);
            ParityFixtureLinter.Validate(fixture);
        });
        Assert.Contains("bare JSON null", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v", TaggedValueTag.Value)]
    [InlineData("null", TaggedValueTag.Null)]
    [InlineData("unknown", TaggedValueTag.Unknown)]
    [InlineData("unavailable", TaggedValueTag.Unavailable)]
    [InlineData("skipped", TaggedValueTag.Skipped)]
    [InlineData("absent", TaggedValueTag.Absent)]
    public void Every_tag_round_trips(string tag, TaggedValueTag expected)
    {
        var body = tag == "v" ? """{ "t": "v", "v": 42 }""" : $$"""{ "t": "{{tag}}" }""";
        using var document = JsonDocument.Parse($$"""{ "field": {{body}} }""");

        var value = TaggedValue.Parse(document.RootElement, "field");

        Assert.Equal(expected, value.Tag);
    }

    [Fact]
    public void A_missing_property_is_absent_not_an_error()
    {
        using var document = JsonDocument.Parse("""{ "other": 1 }""");

        Assert.Equal(TaggedValueTag.Absent, TaggedValue.Parse(document.RootElement, "field").Tag);
    }

    [Fact]
    public void Symmetric_kind_forbids_allowed_divergences()
    {
        var json = HealthySymmetric.Replace(
            "\"allowedDivergences\": []",
            """
            "allowedDivergences": [
              { "path": "$.samples[0].networkState", "windows": "WifiRadioDown", "linux": "AdapterDown", "reason": "x" }
            ]
            """);

        var ex = Assert.Throws<ParityFixtureFormatException>(() => ParityFixture.Parse(json));
        Assert.Contains("symmetric forbids allowedDivergences", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Asymmetric_kind_requires_at_least_one_divergence()
    {
        var json = HealthySymmetric
            .Replace("\"kind\": \"symmetric\"", "\"kind\": \"asymmetric-observability\"");

        var ex = Assert.Throws<ParityFixtureFormatException>(() => ParityFixture.Parse(json));
        Assert.Contains("requires at least one allowedDivergence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Asymmetric_fixture_with_a_reasoned_divergence_parses()
    {
        var json = HealthySymmetric
            .Replace("\"fixtureId\": \"parity.healthy.dual-stack\"", "\"fixtureId\": \"parity.wifi.ssid-gone.asymmetric-scan\"")
            .Replace("\"kind\": \"symmetric\"", "\"kind\": \"asymmetric-observability\"")
            .Replace(
                "\"allowedDivergences\": []",
                """
                "allowedDivergences": [
                  {
                    "path": "$.samples[0].networkState",
                    "windows": "WifiRadioDown",
                    "linux": "AdapterDown",
                    "reason": "Windows complete triggered scan; Linux partial cache null. Both isOutage=true, LocalFault."
                  }
                ]
                """);

        var fixture = ParityFixture.Parse(json);

        Assert.Equal(ParityFixtureKind.AsymmetricObservability, fixture.Kind);
        Assert.Single(fixture.AllowedDivergences);
        Assert.Equal("$.samples[0].networkState", fixture.AllowedDivergences[0].Path);
    }

    [Theory]
    [InlineData("$.samples.*")]
    [InlineData("$.samples[*].tallies.*")]
    [InlineData("samples[0].networkState")]
    [InlineData("$.probes[*].outcome")]
    public void Whitelist_paths_that_break_the_shape_rules_are_rejected(string badPath)
    {
        var json = HealthySymmetric.Replace(
            "\"pathWhitelist\": []",
            $$"""
            "pathWhitelist": [ { "path": "{{badPath}}", "reason": "temporary" } ]
            """);

        Assert.Throws<ParityFixtureFormatException>(() => ParityFixture.Parse(json));
    }

    [Fact]
    public void Whitelist_path_on_a_sample_index_is_accepted()
    {
        var json = HealthySymmetric.Replace(
            "\"pathWhitelist\": []",
            """
            "pathWhitelist": [ { "path": "$.samples[0].debugNativeStatus", "reason": "leaked during dev; remove before 3.1.0-rc1" } ]
            """);

        Assert.Single(ParityFixture.Parse(json).PathWhitelist);
    }

    [Theory]
    [InlineData("wifi.ssid-gone")]
    [InlineData("parity")]
    [InlineData("parity.wifi.ssid.gone.extra.too-deep")]
    [InlineData("parity..name")]
    public void Malformed_fixture_ids_are_rejected(string badId)
    {
        var json = HealthySymmetric.Replace("\"parity.healthy.dual-stack\"", $"\"{badId}\"");

        Assert.Throws<ParityFixtureFormatException>(() => ParityFixture.Parse(json));
    }

    [Fact]
    public void Linter_flags_a_platform_fact_stronger_than_the_world_it_came_from()
    {
        // world.ssidOnAir is unknown, but Linux asserts a firm SsidVisibleInScan=false.
        var json = HealthySymmetric
            .Replace("\"ssidOnAir\": { \"t\": \"v\", \"v\": true }", "\"ssidOnAir\": { \"t\": \"unknown\" }")
            .Replace(
                "\"linux\": { \"ticks\": [ { \"seq\": 1, \"radioOn\": { \"t\": \"v\", \"v\": true }, \"ssidVisibleInScan\": { \"t\": \"null\" } } ] }",
                "\"linux\": { \"ticks\": [ { \"seq\": 1, \"radioOn\": { \"t\": \"v\", \"v\": true }, \"ssidVisibleInScan\": { \"t\": \"v\", \"v\": false } } ] }");

        var fixture = ParityFixture.Parse(json);
        var violations = ParityFixtureLinter.Lint(fixture);

        Assert.Contains(violations, v => v.Contains("linux.tick[1].ssidVisibleInScan", StringComparison.Ordinal));
    }

    [Fact]
    public void Linter_flags_a_platform_fact_that_contradicts_a_certain_world()
    {
        // world.radioSwitch=on, but Windows says radioOn=false.
        var json = HealthySymmetric.Replace(
            "\"windows\": { \"ticks\": [ { \"seq\": 1, \"radioOn\": { \"t\": \"v\", \"v\": true }, \"ssidVisibleInScan\": { \"t\": \"v\", \"v\": true } } ] }",
            "\"windows\": { \"ticks\": [ { \"seq\": 1, \"radioOn\": { \"t\": \"v\", \"v\": false }, \"ssidVisibleInScan\": { \"t\": \"v\", \"v\": true } } ] }");

        var fixture = ParityFixture.Parse(json);
        var violations = ParityFixtureLinter.Lint(fixture);

        Assert.Contains(violations, v => v.Contains("radioOn=false contradicts world", StringComparison.Ordinal));
    }

    [Fact]
    public void Linter_allows_weaker_platform_observability()
    {
        // world says the SSID is on air; Linux only manages null. That is weaker, and fine.
        var fixture = ParityFixture.Parse(HealthySymmetric);

        Assert.Empty(ParityFixtureLinter.Lint(fixture));
    }
}
