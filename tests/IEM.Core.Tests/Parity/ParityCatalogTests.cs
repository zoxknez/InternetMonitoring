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

        var monitoredTime = ReadPlannedDuration(fixture);
        var windows = CanonicalParityView.From(ParityProjection.ProjectRun(fixture, ParityPlatform.Windows), monitoredTime: monitoredTime);
        var linux = CanonicalParityView.From(ParityProjection.ProjectRun(fixture, ParityPlatform.Linux), monitoredTime: monitoredTime);
        var diff = ParityDiffer.Diff(windows, linux, fixture);

        Assert.False(diff.HasForbidden, diff.Report());
        Assert.Equal(
            ParityHashNormalizer.Sha256(windows, fixture),
            ParityHashNormalizer.Sha256(linux, fixture));

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

        var windows = CanonicalParityView.From(
            ParityProjection.ProjectRun(fixture, ParityPlatform.Windows),
            monitoredTime: ReadPlannedDuration(fixture));
        foreach (var expectedSample in samples.EnumerateArray())
        {
            var seq = expectedSample.GetProperty("seq").GetInt64();
            var actual = Assert.Single(windows.Samples, sample => sample.Seq == seq);

            if (expectedSample.TryGetProperty("networkState", out var expectedState))
            {
                Assert.Equal(expectedState.GetString(), actual.NetworkState);
            }

            if (expectedSample.TryGetProperty("isOutage", out var expectedOutage))
            {
                Assert.Equal(expectedOutage.GetBoolean(), actual.IsOutage);
            }

            if (expectedSample.TryGetProperty("pathProvesLink", out var expectedPath))
            {
                Assert.Equal(expectedPath.GetBoolean(), actual.PathProvesLink);
            }

            if (expectedSample.TryGetProperty("anyExternalReachability", out var expectedReachability))
            {
                Assert.Equal(expectedReachability.GetBoolean(), actual.AnyExternalReachability);
            }
        }

        if (fixture.ExpectedCanonicalOutput.TryGetProperty("sessionVerdictKind", out var expectedVerdict))
        {
            Assert.Equal(expectedVerdict.GetString(), windows.SessionVerdictKind);
        }

        if (fixture.ExpectedCanonicalOutput.TryGetProperty("claims", out var claims))
        {
            if (claims.TryGetProperty("supportsComplaint", out var supportsComplaint))
            {
                Assert.Equal(supportsComplaint.GetBoolean(), windows.Claims.SupportsComplaint);
            }

            if (claims.TryGetProperty("namesOperatorAsFault", out var namesOperator))
            {
                Assert.Equal(namesOperator.GetBoolean(), windows.Claims.NamesOperatorAsFault);
            }

            if (claims.TryGetProperty("wifiRadioBlamed", out var wifiBlamed))
            {
                Assert.Equal(wifiBlamed.GetBoolean(), windows.Claims.WifiRadioBlamed);
            }
        }

        if (fixture.ExpectedCanonicalOutput.TryGetProperty("quality", out var quality) &&
            quality.TryGetProperty("pathAttribution", out var expectedPathQuality))
        {
            Assert.Equal(expectedPathQuality.GetString(), windows.Quality.PathAttribution);
        }


        if (fixture.ExpectedCanonicalOutput.TryGetProperty("incidents", out var expectedIncidents))
        {
            Assert.Equal(expectedIncidents.GetArrayLength(), windows.Incidents.Count);
            for (var index = 0; index < expectedIncidents.GetArrayLength(); index++)
            {
                var expectedIncident = expectedIncidents[index];
                var actual = windows.Incidents[index];
                if (expectedIncident.TryGetProperty("worstState", out var worstState))
                {
                    Assert.Equal(worstState.GetString(), actual.WorstState);
                }

                if (expectedIncident.TryGetProperty("endedByGap", out var endedByGap))
                {
                    Assert.Equal(endedByGap.GetBoolean(), actual.EndedByGap);
                }
            }
        }
    }

    private static TimeSpan ReadPlannedDuration(ParityFixture fixture)
    {
        var raw = fixture.SemanticInput.GetProperty("session").GetProperty("plannedDuration").GetString();
        return System.Xml.XmlConvert.ToTimeSpan(raw!);
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
        Assert.Contains("parity.wifi.stale-scan", ids);
        Assert.Contains("parity.wifi.ssid-gone.asymmetric-scan", ids);
        Assert.Contains("parity.adversarial.null-as-ssid-gone", ids);
        Assert.Contains("parity.adversarial.unknown-radio-as-off", ids);
        Assert.Contains("parity.path.tocou-route-change", ids);
        Assert.Contains("parity.path.observer-polling", ids);
        Assert.Contains("parity.dns.isp-fail-same-family", ids);
        Assert.Contains("parity.dual-stack.v4-down-v6-up", ids);
        Assert.Contains("parity.icmp.v4-denied.v6-ok", ids);
        Assert.Contains("parity.suspend.not-outage", ids);
        Assert.Contains("parity.reboot.not-outage", ids);
        Assert.Contains("parity.adversarial.suspend-as-outage", ids);
        Assert.Contains("parity.unresolved-route.tcp-runs", ids);
        Assert.Contains("parity.bind-fail.tcp-skipped", ids);
        Assert.Contains("parity.wifi.partial-scan-vs-complete", ids);
        Assert.Contains("parity.nm.association-conflict", ids);
        Assert.Contains("parity.vpn.default-route-flip", ids);
        Assert.Contains("parity.mode.four-way", ids);
    }

    [Fact]
    public void Four_way_mode_fixture_keeps_canonical_meaning_identical()
    {
        var fixture = ParityFixture.Parse(File.ReadAllText(Path.Combine(CatalogRoot, "parity.mode.four-way.json")));
        var monitoredTime = ReadPlannedDuration(fixture);

        var windowsService = CanonicalParityView.From(ParityProjection.ProjectRun(fixture, ParityPlatform.Windows), monitoredTime: monitoredTime);
        var windowsPortable = CanonicalParityView.From(ParityProjection.ProjectRun(fixture, ParityPlatform.Windows), monitoredTime: monitoredTime);
        var linuxService = CanonicalParityView.From(ParityProjection.ProjectRun(fixture, ParityPlatform.Linux), monitoredTime: monitoredTime);
        var linuxPortable = CanonicalParityView.From(ParityProjection.ProjectRun(fixture, ParityPlatform.Linux), monitoredTime: monitoredTime);

        Assert.Equal(windowsService.Sha256, windowsPortable.Sha256);
        Assert.Equal(windowsService.Sha256, linuxService.Sha256);
        Assert.Equal(windowsService.Sha256, linuxPortable.Sha256);
    }

    [Fact]
    public void Stronger_linux_claim_fixture_is_rejected_before_projection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Parity", "invalid", "v1",
            "parity.adversarial.stronger-linux-claim.json");
        var fixture = ParityFixture.Parse(File.ReadAllText(path));

        var error = Assert.Throws<ParityFixtureFormatException>(() => ParityFixtureLinter.Validate(fixture));

        Assert.Contains("linux.tick[1].ssidVisibleInScan", error.Message, StringComparison.Ordinal);
        Assert.Contains("stronger than world", error.Message, StringComparison.Ordinal);
    }
}
