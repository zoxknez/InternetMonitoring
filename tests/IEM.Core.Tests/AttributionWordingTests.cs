using IEM.Core.Model;
using IEM.Core.Presentation;

namespace IEM.Core.Tests;

/// <summary>
/// PRESENTATION_NEVER_CLAIMS_MORE_THAN_RAW_EVIDENCE, for the one claim that decides whether a
/// complaint survives its first reply.
/// <para>
/// The attribution model has said since 2.0 that a measurement taken inside the customer's
/// house cannot establish whose network is at fault - only how far along the path the fault
/// was isolated. The wording built on top of it went on saying "Operater", "Prekida kod
/// operatera" and "Vaša oprema je isključena kao uzrok" for a whole release, because nothing
/// tied the two together. This is that tie.
/// </para>
/// <para>
/// The distinction is not pedantry. A router whose WAN port has failed, whose firmware has
/// wedged, whose PPPoE session has dropped or whose NAT table is full presents from this
/// machine exactly like an outage in the operator's network. An opening sentence that names
/// the operator hands them the easiest possible rebuttal; one that says what was measured
/// does not.
/// </para>
/// </summary>
public sealed class AttributionWordingTests
{
    /// <summary>Wording that claims more than the measurement establishes.</summary>
    private static readonly string[] Overreach =
    [
        "kod operatera",
        "na strani operatera",
        "Nedostupnost operatera",
        "oprema isključena kao uzrok",
        "isključuje vašu opremu",
    ];

    private static IEnumerable<NetworkState> AllStates =>
        Enum.GetValues<NetworkState>();

    private static IEnumerable<FaultDomain> AllDomains =>
        Enum.GetValues<FaultDomain>();

    [Fact]
    public void No_state_is_presented_as_a_fault_at_the_operator()
    {
        var offences = new List<string>();

        foreach (var state in AllStates)
        {
            Check(offences, $"Label({state})", state.Label());
            Check(offences, $"Explanation({state})", state.Explanation());
        }

        foreach (var domain in AllDomains)
        {
            Check(offences, $"Label({domain})", domain.Label());
            Check(offences, $"Explain({domain})", domain.Explain());
        }

        foreach (var attribution in Enum.GetValues<FaultAttribution>())
        {
            Check(offences, $"Label({attribution})", attribution.Label());
        }

        Assert.Empty(offences);
    }

    /// <summary>
    /// The label that was literally the word "Operater" - a verdict on a company, rendered
    /// from a measurement that never left the customer's living room.
    /// </summary>
    [Fact]
    public void The_upstream_attribution_names_where_it_was_isolated_not_who_is_at_fault()
    {
        var label = FaultAttribution.Upstream.Label();

        Assert.DoesNotContain("perater", label, StringComparison.Ordinal);
        Assert.Contains("iza rutera", label, StringComparison.Ordinal);
    }

    /// <summary>
    /// The strongest finding this tool produces has to carry both halves: what the
    /// measurement showed, and what it leaves open. Without the second half it reads as a
    /// conclusion about the operator's network, which is the thing it is not.
    /// </summary>
    [Fact]
    public void The_upstream_explanation_says_what_was_measured_and_what_is_not_excluded()
    {
        var text = FaultDomain.UpstreamPath.Explain();

        // What was measured.
        Assert.Contains("ruter je tokom celog prekida odgovarao", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nijedna spoljna meta nije", text, StringComparison.OrdinalIgnoreCase);

        // What it does not exclude - named, because a reader cannot weigh an unstated caveat.
        Assert.Contains("Nisu isključeni", text, StringComparison.Ordinal);
        Assert.Contains("WAN", text, StringComparison.Ordinal);
        Assert.Contains("PPPoE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Presentation changed; the recorded evidence did not. The enum member, the values in
    /// SQLite and the <c>attribution</c> field in the hash chain all keep their meaning, so a
    /// session recorded by 2.6 still verifies and still reads the same. What moved is the
    /// model version, which is how a reader of an old report knows which wording produced it.
    /// </summary>
    [Fact]
    public void The_recorded_value_is_unchanged_and_the_model_version_says_the_wording_moved()
    {
        Assert.Equal(FaultAttribution.Upstream, NetworkState.CpeUpstreamUnreachable.AttributionOf());
        Assert.Equal("Upstream", FaultAttribution.Upstream.ToString());
        Assert.Equal("2.1", EvidenceModelVersion.AttributionModelVersion);
    }

    /// <summary>
    /// Sentences that deny the attribution rather than making it - "uzrok je na računaru, ne
    /// kod operatera". Those are the tool doing exactly the right thing, and a check that
    /// could not tell them from a claim would push the wording in the wrong direction.
    /// </summary>
    private static readonly string[] Denials =
    [
        "ne kod operatera",
        "nije kod operatera",
        "ne operatera",
    ];

    private static void Check(List<string> offences, string where, string text)
    {
        foreach (var denial in Denials)
        {
            text = text.Replace(denial, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var phrase in Overreach)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                offences.Add($"{where}: „{phrase}\"");
            }
        }
    }
}
