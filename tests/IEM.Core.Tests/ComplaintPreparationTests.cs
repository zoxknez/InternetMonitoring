using IEM.Core.Model;
using IEM.Legal;
using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// The decision whether someone has grounds for a complaint, which both the console and the
/// window now call. It used to exist twice - once in each - with neither copy reachable by a
/// test, so a window offering to write a complaint the console would refuse was possible and
/// nothing would have caught it.
/// </summary>
public sealed class ComplaintPreparationTests
{
    private static IncidentRow Incident(NetworkState state, FaultAttribution attribution) => new(
        1,
        new DateTimeOffset(2026, 3, 3, 14, 12, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 3, 14, 54, 0, TimeSpan.Zero),
        state,
        attribution,
        TimeSpan.FromMinutes(41),
        TimeSpan.FromMinutes(42),
        TimeSpan.FromMinutes(43),
        2_520, false, false, false, false, Guid.NewGuid());

    private static SessionSnapshot Session(
        IReadOnlyList<IncidentRow> incidents,
        TimeSpan? monitored = null,
        IReadOnlyList<GapRow>? gaps = null) => new(
        "S1",
        new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 4, 8, 0, 0, TimeSpan.Zero),
        "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1",
        MonitoredTime: monitored ?? TimeSpan.FromHours(48),
        GapTime: TimeSpan.Zero,
        UpstreamDowntime: TimeSpan.FromMinutes(42),
        LocalDowntime: TimeSpan.Zero,
        AvailabilityPercent: 98.5,
        UpstreamAvailabilityPercent: 98.5,
        SampleCount: 172_800,
        Incidents: incidents,
        Gaps: gaps ?? [],
        Latency: [],
        Traces: []);

    private static readonly DateOnly Today = new(2026, 3, 10);

    // ---- When it refuses ------------------------------------------------------

    /// <summary>
    /// A letter demanding the cause of outages that were never recorded is the first thing
    /// an operator would notice, and it takes the rest of the evidence down with it.
    /// </summary>
    [Fact]
    public void A_session_without_a_single_outage_is_refused()
    {
        var result = ComplaintPreparation.From(Session([]), "Operater", Today);

        Assert.False(result.Prepared);
        Assert.Null(result.Letter);
        Assert.Contains("nije zabeležen nijedan prekid", result.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Outages that did not rule out the customer's own equipment cannot be put to the
    /// operator, and saying which is which spares someone a complaint they would lose.
    /// </summary>
    [Fact]
    public void Outages_that_do_not_rule_out_local_equipment_are_refused_with_the_count()
    {
        var local = Session([
            Incident(NetworkState.AdapterDown, FaultAttribution.LocalDevice),
            Incident(NetworkState.WifiRadioDown, FaultAttribution.Router),
        ]);

        var result = ComplaintPreparation.From(local, "Operater", Today);

        Assert.False(result.Prepared);
        Assert.Contains("2 prekida", result.Refusal!, StringComparison.Ordinal);
        Assert.Contains("nijedan nije isključio vašu opremu", result.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recording_too_short_to_defend_is_refused()
    {
        var brief = Session(
            [Incident(NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream)],
            monitored: TimeSpan.FromSeconds(20));

        var result = ComplaintPreparation.From(brief, "Operater", Today);

        Assert.False(result.Prepared);
        Assert.Contains("prekratko", result.Refusal!, StringComparison.Ordinal);
    }

    // ---- When it writes -------------------------------------------------------

    [Fact]
    public void A_conclusive_upstream_outage_produces_a_letter()
    {
        var result = ComplaintPreparation.From(
            Session([Incident(NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream)]),
            "SBB",
            Today);

        Assert.True(result.Prepared);
        Assert.NotNull(result.Letter);
        Assert.Contains("SBB", result.Letter, StringComparison.Ordinal);
        Assert.Contains("ruter je uredno odgovarao", result.Letter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The clock runs from the first outage that ruled out the customer's equipment, since
    /// that is the one the complaint is about - not from whenever monitoring happened to
    /// start, which would quietly shorten the window.
    /// </summary>
    [Fact]
    public void The_deadline_runs_from_the_first_outage_worth_complaining_about()
    {
        var result = ComplaintPreparation.From(
            Session([Incident(NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream)]),
            "Operater",
            Today);

        Assert.Equal(new DateOnly(2026, 3, 3), result.Case!.EventDate);
    }

    [Fact]
    public void An_unnamed_operator_leaves_a_blank_to_fill_in_rather_than_a_guess()
    {
        var result = ComplaintPreparation.From(
            Session([Incident(NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream)]),
            operatorName: null,
            Today);

        Assert.True(result.Prepared);
        Assert.Contains("____", result.Case!.OperatorName, StringComparison.Ordinal);
    }

    // ---- What the letter tells the operator to expect -------------------------

    /// <summary>
    /// No sentence in the letter ends in two full stops.
    /// <para>
    /// The Serbian date format carries its own trailing period, so a sentence ending on a
    /// date and adding one produced "dana 13.08.2026.." - a typo in a document whose whole
    /// purpose is to be taken seriously by the desk that receives it.
    /// </para>
    /// </summary>
    [Fact]
    public void No_sentence_ends_in_a_double_full_stop()
    {
        var letter = ComplaintPreparation.From(
            Session([Incident(NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream)]),
            "Operater",
            Today).Letter;

        Assert.NotNull(letter);
        Assert.DoesNotContain("..", letter.Replace("...", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The attachment list names the file a complaints desk will actually open. It used to
    /// lead with the HTML report, which asks someone processing paper to open a web page.
    /// </summary>
    [Fact]
    public void The_attachment_list_leads_with_the_printable_report()
    {
        var letter = ComplaintPreparation.From(
            Session([Incident(NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream)]),
            "Operater",
            Today).Letter;

        Assert.NotNull(letter);
        Assert.Contains("1. Izveštaj o nadzoru veze (Izvestaj.pdf)", letter, StringComparison.Ordinal);
        Assert.Contains("2. Isti izveštaj za pregled na ekranu (Izvestaj.html)", letter, StringComparison.Ordinal);
        Assert.DoesNotContain("Tabela pauza nadzora", letter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pause table is only attached when there were pauses, so its number depends on
    /// every entry above it. Hand-written, it went stale the moment the list changed - and
    /// an attachment list whose numbering does not match the envelope is the kind of detail
    /// that gets a complaint sent back.
    /// </summary>
    [Fact]
    public void The_conditional_attachment_is_numbered_after_the_ones_before_it()
    {
        var letter = ComplaintPreparation.From(
            Session(
                [Incident(NetworkState.CpeUpstreamUnreachable, FaultAttribution.Upstream)],
                gaps: [new GapRow(new DateTimeOffset(2026, 3, 3, 2, 0, 0, TimeSpan.Zero),
                    TimeSpan.FromHours(6), nameof(GapCause.Sleep))]),
            "Operater",
            Today).Letter;

        Assert.NotNull(letter);
        Assert.Contains("7. Tabela pauza nadzora (Pauze-nadzora.csv)", letter, StringComparison.Ordinal);
    }
}
