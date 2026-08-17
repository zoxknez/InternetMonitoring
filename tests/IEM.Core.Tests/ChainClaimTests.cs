using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Evidence;
using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// What the hash chain is allowed to claim.
/// <para>
/// The chain links every entry to the one before it, so altering an early record breaks every
/// hash after it. Up to 2.6 that was reported as "dokazano je da paket nije menjan nakon
/// snimanja" - and the verifier reads the same folder the records live in, with
/// <c>SHA256SUMS.txt</c> sitting in it. Whoever can rewrite a record can recompute both.
/// </para>
/// <para>
/// Against a careless edit the old claim held; against anyone with a reason to forge the file
/// it did not, and it is the second case that decides a dispute. An operator's technician who
/// spotted the gap would be entitled to discount the whole document.
/// </para>
/// </summary>
public sealed class ChainClaimTests
{
    /// <summary>Claims stronger than internal consistency.</summary>
    private static readonly string[] Overreach =
    [
        "dokazano je da paket nije menjan",
        "paket nije menjan nakon snimanja",
        "paket je menjan nakon snimanja",
    ];

    [Fact]
    public void The_finding_is_internal_consistency_rather_than_proof_of_origin()
    {
        Assert.Contains("unutrašnje dosledan", ChainText.Consistent, StringComparison.Ordinal);

        foreach (var phrase in Overreach)
        {
            Assert.DoesNotContain(phrase, ChainText.Consistent, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(phrase, ChainText.Broken, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The caveat has to name the actual hole - that the checksums live in the same writable
    /// folder - rather than gesturing at "a third-party timestamp would be better".
    /// </summary>
    [Fact]
    public void The_caveat_names_what_it_would_take_to_forge_and_what_is_missing()
    {
        Assert.Contains("kontrolne zbirove", ChainText.NotProofOfOrigin, StringComparison.Ordinal);
        Assert.Contains("potpis i vremenski žig", ChainText.NotProofOfOrigin, StringComparison.Ordinal);
        Assert.Contains("ovo izdanje ne radi", ChainText.NotProofOfOrigin, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the caveat travels with the finding. A report that states the conclusion on one
    /// page and qualifies it on another has misled the reader through its layout, whatever
    /// both pages say.
    /// </summary>
    [Fact]
    public void Every_report_states_the_caveat_beside_the_finding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iem-chain-{Guid.NewGuid():N}.html");

        try
        {
            HtmlReportBuilder.Write(path, Session(), "abc123", chainValid: true);
            Check(File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Check(string html)
    {
        var integrity = html.IndexOf("Integritet zapisa", StringComparison.Ordinal);
        var caveat = html.IndexOf("provera doslednosti", StringComparison.Ordinal);

        Assert.True(integrity >= 0, "Izveštaj nema odeljak o integritetu.");
        Assert.True(caveat > integrity, "Ograda ne stoji uz sam nalaz.");

        foreach (var phrase in Overreach)
        {
            Assert.DoesNotContain(phrase, html, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static SessionSnapshot Session() => new(
        "S1",
        new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero),
        "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1",
        MonitoredTime: TimeSpan.FromHours(48),
        GapTime: TimeSpan.Zero,
        UpstreamDowntime: TimeSpan.Zero,
        LocalDowntime: TimeSpan.Zero,
        AvailabilityPercent: 100,
        UpstreamAvailabilityPercent: 100,
        SampleCount: 100,
        Incidents: [],
        Gaps: [],
        Latency: [],
        Traces: []);
}
