using IEM.Core.Model;
using IEM.Legal;
using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// The submission refuses to write itself until the operator has had their chance.
/// <para>
/// One filed too early is sent back, and the person has then spent their effort - and
/// possibly part of their window - on a document nobody could consider. Refusing with a
/// reason is worth more than a document that cannot be used.
/// </para>
/// </summary>
public sealed class RegulatorSubmissionTests
{
    private static readonly DateOnly Fault = new(2026, 3, 2);

    private static ComplaintCase Case(DateOnly? submitted = null, DateOnly? responded = null, bool? upheld = null) =>
        new()
        {
            OperatorName = "Operater",
            SubscriberName = "Petar Petrović",
            ContractNumber = "123456",
            EventDate = Fault,
            SubmittedDate = submitted,
            OperatorRespondedDate = responded,
            OperatorUpheld = upheld,
        };

    private static SessionSnapshot Session() => new(
        "S1",
        new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 3, 4, 8, 0, 0, TimeSpan.Zero),
        "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1",
        MonitoredTime: TimeSpan.FromHours(48),
        GapTime: TimeSpan.Zero,
        UpstreamDowntime: TimeSpan.FromMinutes(42),
        LocalDowntime: TimeSpan.Zero,
        AvailabilityPercent: 98.5,
        UpstreamAvailabilityPercent: 98.5,
        SampleCount: 172_800,
        Incidents:
        [
            new IncidentRow(
                1,
                new DateTimeOffset(2026, 3, 3, 14, 12, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 3, 3, 14, 54, 0, TimeSpan.Zero),
                NetworkState.CpeUpstreamUnreachable,
                FaultAttribution.Upstream,
                TimeSpan.FromMinutes(41),
                TimeSpan.FromMinutes(42),
                TimeSpan.FromMinutes(43),
                2_520, false, false, false, false, Guid.NewGuid()),
        ],
        Gaps: [],
        Latency: [],
        Traces: []);

    // ---- When it refuses ------------------------------------------------------

    [Fact]
    public void Nothing_is_written_before_the_operator_has_been_asked()
    {
        var text = RegulatorSubmission.Build(Case(), Session(), new DateOnly(2026, 3, 5), out var obstacle);

        Assert.Null(text);
        Assert.Equal(RegulatorSubmission.Obstacle.OperatorNotContacted, obstacle);
        Assert.Contains("Prvo podnesite prigovor", RegulatorSubmission.Explain(obstacle), StringComparison.Ordinal);
    }

    /// <summary>
    /// The mistake that costs a window: filing while the operator still has time, having it
    /// sent back, and only then discovering how much of the clock has gone.
    /// </summary>
    [Fact]
    public void Nothing_is_written_while_the_operator_still_has_time()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        var text = RegulatorSubmission.Build(complaint, Session(), new DateOnly(2026, 3, 20), out var obstacle);

        Assert.Null(text);
        Assert.Equal(RegulatorSubmission.Obstacle.OperatorStillHasTime, obstacle);
        Assert.Contains("Sačekajte", RegulatorSubmission.Explain(obstacle), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_written_when_the_complaint_was_accepted()
    {
        var complaint = Case(
            submitted: new DateOnly(2026, 3, 10),
            responded: new DateOnly(2026, 3, 18),
            upheld: true);

        var text = RegulatorSubmission.Build(complaint, Session(), new DateOnly(2026, 3, 20), out var obstacle);

        Assert.Null(text);
        Assert.Equal(RegulatorSubmission.Obstacle.ComplaintUpheld, obstacle);
    }

    [Fact]
    public void Nothing_is_written_once_the_window_has_closed()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        var text = RegulatorSubmission.Build(complaint, Session(), new DateOnly(2026, 6, 1), out var obstacle);

        Assert.Null(text);
        Assert.Equal(RegulatorSubmission.Obstacle.Expired, obstacle);
    }

    // ---- When it writes -------------------------------------------------------

    [Fact]
    public void A_silent_operator_produces_a_submission_that_says_so()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        var text = RegulatorSubmission.Build(complaint, Session(), new DateOnly(2026, 3, 30), out var obstacle);

        Assert.Equal(RegulatorSubmission.Obstacle.None, obstacle);
        Assert.NotNull(text);

        Assert.Contains("nije odgovorio u roku", text, StringComparison.Ordinal);
        Assert.Contains("10.03.2026.", text, StringComparison.Ordinal);
        Assert.Contains("123456", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_produces_a_submission_that_carries_the_operators_answer()
    {
        var complaint = Case(
            submitted: new DateOnly(2026, 3, 10),
            responded: new DateOnly(2026, 3, 18),
            upheld: false);

        var text = RegulatorSubmission.Build(complaint, Session(), new DateOnly(2026, 3, 25), out _)!;

        Assert.Contains("prigovor odbijen", text, StringComparison.Ordinal);
        Assert.Contains("Odgovor operatera", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The figures come from the session rather than from anything the user typed, so the
    /// document cannot claim more than the attachments support.
    /// </summary>
    [Fact]
    public void The_figures_come_from_the_recorded_session()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        var text = RegulatorSubmission.Build(complaint, Session(), new DateOnly(2026, 3, 30), out _)!;

        // Written as "98,5 %", not "98,5000 %": the trailing zeros claimed a precision
        // the figure does not have. The wording comes from SerbianText.Percent, which
        // every other surface shares, so the letter cannot drift from the report.
        Assert.Contains("98,5 %", text, StringComparison.Ordinal);
        Assert.Contains("SirovaEvidencija.jsonl", text, StringComparison.Ordinal);
        Assert.Contains("NetTest", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_generated_says_it_is_not_legal_advice()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        var submission = RegulatorSubmission.Build(complaint, Session(), new DateOnly(2026, 3, 30), out _)!;
        var letter = ComplaintLetter.ToOperator(complaint, Session(), new DateOnly(2026, 3, 10));

        Assert.Contains("Nije pravni savet", submission, StringComparison.Ordinal);
        Assert.Contains("Nije pravni savet", letter, StringComparison.Ordinal);
    }

    // ---- The complaint to the operator ----------------------------------------

    /// <summary>
    /// Only the outages that ruled out the customer's own equipment. Listing every recorded
    /// fault would put the customer's own Wi-Fi drops in a letter complaining about the
    /// operator, and hand them the answer.
    /// </summary>
    [Fact]
    public void The_letter_states_what_was_measured_rather_than_who_is_at_fault()
    {
        var letter = ComplaintLetter.ToOperator(Case(), Session(), new DateOnly(2026, 3, 10));

        Assert.Contains("ruter je uredno odgovarao", letter, StringComparison.Ordinal);
        Assert.Contains("isključuje moju opremu", letter, StringComparison.Ordinal);
        Assert.Contains("lancem otisaka", letter, StringComparison.Ordinal);
    }

    /// <summary>
    /// The letter quotes only the outages that ruled out the customer's own equipment. A
    /// session where none did has nothing to say to an operator, and a letter demanding the
    /// cause of outages that were never recorded is exactly the overclaiming that gets the
    /// rest of the evidence dismissed along with it.
    /// </summary>
    [Fact]
    public void A_session_with_no_upstream_outage_has_nothing_to_put_in_a_letter()
    {
        var clean = Session() with
        {
            Incidents = [],
            UpstreamDowntime = TimeSpan.Zero,
            UpstreamAvailabilityPercent = 100,
        };

        var letter = ComplaintLetter.ToOperator(Case(), clean, new DateOnly(2026, 3, 10));

        // The letter itself does not invent outages; the count simply never appears.
        Assert.DoesNotContain("prekida usluge u ukupnom trajanju", letter, StringComparison.Ordinal);
        Assert.DoesNotContain("Najduži prekidi", letter, StringComparison.Ordinal);
    }

    [Fact]
    public void The_letter_asks_for_a_receipt_because_without_one_the_deadline_cannot_be_proved()
    {
        var letter = ComplaintLetter.ToOperator(Case(), Session(), new DateOnly(2026, 3, 10));

        Assert.Contains("potvrdite prijem", letter, StringComparison.Ordinal);
        Assert.Contains("broj predmeta", letter, StringComparison.Ordinal);
    }
}
