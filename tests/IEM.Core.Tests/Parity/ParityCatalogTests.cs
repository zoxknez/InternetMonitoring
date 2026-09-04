namespace IEM.Core.Tests.Parity;

/// <summary>
/// 3.1-12 slice 3: the golden catalogue. One case per fixture file under
/// <c>Fixtures/Parity/v1</c>; a FORBIDDEN divergence fails the release (invariant 257). The
/// set here is the start of the §25.12 catalogue - the projection grows to cover suspend,
/// reboot, DNS families and VPN route flips as their fixtures are added.
/// </summary>
public sealed class ParityCatalogTests
{
    private static readonly string CatalogRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity", "v1");

    public static IEnumerable<object[]> Fixtures =>
        Directory.EnumerateFiles(CatalogRoot, "*.json")
            .Select(path => new object[] { Path.GetFileNameWithoutExtension(path) })
            .OrderBy(row => (string)row[0]);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Catalog_fixture_projects_without_a_forbidden_divergence(string fixtureId)
    {
        var fixture = ParityFixture.Parse(File.ReadAllText(Path.Combine(CatalogRoot, fixtureId + ".json")));
        Assert.Equal(fixtureId, fixture.FixtureId);
        ParityFixtureLinter.Validate(fixture);

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        var linux = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Linux));
        var diff = ParityDiffer.Diff(windows, linux, fixture);

        Assert.False(diff.HasForbidden, diff.Report());

        switch (fixture.Kind)
        {
            case ParityFixtureKind.Symmetric:
                Assert.Equal(windows.Sha256, linux.Sha256);
                Assert.DoesNotContain(diff.Divergences, line => line.Verdict == ParityVerdict.Allowed);
                break;

            case ParityFixtureKind.AsymmetricObservability:
                Assert.NotEqual(windows.Sha256, linux.Sha256);
                Assert.Contains(diff.Divergences, line => line.Verdict == ParityVerdict.Allowed);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Catalog_fixture_matches_its_declared_network_state(string fixtureId)
    {
        var fixture = ParityFixture.Parse(File.ReadAllText(Path.Combine(CatalogRoot, fixtureId + ".json")));
        if (!fixture.ExpectedCanonicalOutput.TryGetProperty("samples", out var samples) || samples.GetArrayLength() == 0)
        {
            return;
        }

        var first = samples[0];
        if (!first.TryGetProperty("networkState", out var expectedState))
        {
            return;
        }

        var windows = CanonicalParityView.From(ParityProjection.Project(fixture, ParityPlatform.Windows));
        Assert.Equal(expectedState.GetString(), windows.Samples[0].NetworkState);
    }

    [Fact]
    public void Catalog_covers_the_core_symmetric_shapes()
    {
        var ids = Fixtures.Select(row => (string)row[0]).ToList();

        Assert.Contains("parity.healthy.dual-stack", ids);
        Assert.Contains("parity.outage.gateway-down", ids);
        Assert.Contains("parity.outage.cpe-upstream", ids);
        Assert.Contains("parity.filter.icmp-timeout-tcp-ok", ids);
        Assert.Contains("parity.wifi.radio-on-ssid-gone.symmetric", ids);
        Assert.Contains("parity.wifi.radio-null.link-down", ids);
    }
}
