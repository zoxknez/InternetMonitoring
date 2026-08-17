using System.Text.Json;
using IEM.Legal;

namespace IEM.Core.Tests;

/// <summary>
/// The case outlives the session. The journal is what keeps the filed and answered dates
/// between runs - and, since 2.7, the rules they were counted under, so a case does not
/// silently acquire today's periods the next time somebody opens it.
/// </summary>
public sealed class CaseJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-case-tests", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Today = new(2026, 3, 12);

    public CaseJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over a leftover temp directory.
        }
    }

    private static ComplaintCase Case(DateOnly? submitted = null, DateOnly? responded = null, bool? upheld = null) =>
        new()
        {
            OperatorName = "Operater",
            SubscriberName = "Petar Petrović",
            EventDate = new DateOnly(2026, 3, 2),
            EventOrigin = FactOrigin.UserProvided,
            SubmittedDate = submitted,
            OperatorRespondedDate = responded,
            OperatorUpheld = upheld,
        };

    [Fact]
    public void A_saved_journal_reads_back_identically()
    {
        var journal = new CaseJournal
        {
            Case = Case(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20), upheld: false) with
            {
                RegulatorFiledDate = new DateOnly(2026, 3, 25),
            },
        };

        CaseJournalStore.Save(_root, journal, Today);

        var read = CaseJournalStore.Load(_root)!;

        Assert.Equal(journal.Case, read.Case);
        Assert.Equal(CaseJournal.CurrentSchemaVersion, read.SchemaVersion);
    }

    [Fact]
    public void A_missing_journal_loads_as_null()
    {
        Assert.Null(CaseJournalStore.Load(_root));
    }

    [Fact]
    public void An_unparseable_journal_loads_as_null()
    {
        File.WriteAllText(CaseJournalStore.PathOf(_root), "{ nije json");
        Assert.Null(CaseJournalStore.Load(_root));
    }

    /// <summary>
    /// HISTORICAL_CASE_NEVER_CHANGES_MEANING. The rules are written into the file, so a case
    /// decided under one set keeps its dates when the registry moves on. Storing only an
    /// identifier would let a later correction rewrite what an old case meant, which is the
    /// one thing a record of a dispute must never do.
    /// </summary>
    [Fact]
    public void A_saved_case_carries_the_rules_it_was_decided_under()
    {
        CaseJournalStore.Save(
            _root,
            new CaseJournal { Case = Case(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20)) },
            Today);

        var legal = CaseJournalStore.Load(_root)!.Legal!;

        Assert.Equal("RS-ZEK-2025", legal.Ruleset.Id);
        Assert.NotEmpty(legal.Ruleset.Version);
        Assert.Equal(64, legal.Ruleset.ContentHash.Length);
        Assert.Equal(LegalContextState.Resolved, legal.State);

        // The periods themselves, not just their name: eight days for a consumer's answer and
        // sixty to reach the Regulator, frozen with the anchor each was counted from.
        var response = legal.For(ComplaintStep.OperatorResponseDue)!;
        Assert.Equal(8, response.Value);
        Assert.Equal(new DateOnly(2026, 3, 10), response.AnchoredOn!.Date);

        Assert.Equal(60, legal.For(ComplaintStep.RegulatorDisputeDue)!.Value);
    }

    /// <summary>
    /// UNKNOWN_NEVER_BECOMES_CONFIRMED, in the legal layer.
    /// <para>
    /// A case file written by 2.6 has no record of which rules were applied to it. Reading it
    /// as the old regime would hand somebody a fifteen-day window that stopped existing at
    /// the start of 2025 - "I do not know" turned into a specific, wrong answer, on the most
    /// consequential screen in the program.
    /// </para>
    /// </summary>
    [Fact]
    public void A_case_file_from_an_older_build_is_not_read_as_the_old_regime()
    {
        // Exactly what 2.6 wrote: the case, a regulator date beside it, and nothing else.
        File.WriteAllText(CaseJournalStore.PathOf(_root), JsonSerializer.Serialize(new
        {
            Case = new
            {
                OperatorName = "Operater",
                SubscriberName = "Petar Petrović",
                EventDate = "2026-03-02",
                SubmittedDate = "2026-03-10",
            },
            RegulatorFiledDate = "2026-03-25",
        }));

        var journal = CaseJournalStore.Load(_root)!;

        Assert.Null(journal.Legal);
        Assert.Equal(FactOrigin.ImportedLegacy, journal.Case.EventOrigin);

        // The date beside the case moves onto it, where the transitional rule can see it.
        Assert.Equal(new DateOnly(2026, 3, 25), journal.Case.RegulatorFiledDate);

        var legal = journal.Case.Resolve(Today);

        Assert.Equal(LegalContextState.InferredFromRecordedDates, legal.State);
        Assert.Equal(60, legal.For(ComplaintStep.RegulatorDisputeDue)!.Value);
        Assert.NotEqual(15, legal.For(ComplaintStep.RegulatorDisputeDue)!.Value);

        // And the reader is told the position was reconstructed rather than recorded.
        Assert.Contains("rekonstruisan", legal.State.Explain(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one deadline nothing in the milestones could ever mark as met: it falls after
    /// every other date has passed, so only a recorded filing date can say the window was
    /// used in time.
    /// </summary>
    [Fact]
    public void A_regulator_deadline_met_in_time_is_not_reported_as_missed()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10), responded: new DateOnly(2026, 3, 20));
        var late = new DateOnly(2026, 6, 1);
        var milestones = complaint.Milestones(late);

        // The window closed on 19 May; the request reached the Regulator on 1 May.
        var missed = ComplaintDeadlines.Missed(milestones, late, regulatorFiled: new DateOnly(2026, 5, 1));

        Assert.DoesNotContain(missed, m => m.Step == ComplaintStep.RegulatorDisputeDue);

        // Without that date the same deadline is outstanding, as it always was.
        Assert.Contains(
            ComplaintDeadlines.Missed(milestones, late),
            m => m.Step == ComplaintStep.RegulatorDisputeDue);
    }

    /// <summary>
    /// The case the journal exists for: the letter went out, the operator said nothing, and
    /// the only thing that matters now is the day the window to escalate closes.
    /// </summary>
    [Fact]
    public void A_recorded_submission_moves_the_case_to_waiting_for_the_operator()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        Assert.Equal(CaseStage.AwaitingOperator, complaint.StageOn(new DateOnly(2026, 3, 12)));
        Assert.Equal(CaseStage.OperatorSilent, complaint.StageOn(new DateOnly(2026, 3, 26)));

        Assert.Equal(CaseStage.Refused, Case(
            submitted: new DateOnly(2026, 3, 10),
            responded: new DateOnly(2026, 3, 20),
            upheld: false).StageOn(new DateOnly(2026, 4, 2)));
    }
}
