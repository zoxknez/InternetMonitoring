using IEM.Legal;

namespace IEM.Core.Tests;

/// <summary>
/// The case outlives the session. The journal is what keeps the filed and answered dates
/// between runs, so the fifteen days to reach the regulator are counted from reality rather
/// than from whatever the session happened to record.
/// </summary>
public sealed class CaseJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-case-tests", Guid.NewGuid().ToString("N"));

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
            SubmittedDate = submitted,
            OperatorRespondedDate = responded,
            OperatorUpheld = upheld,
        };

    [Fact]
    public void A_saved_journal_reads_back_identically()
    {
        var journal = new CaseJournal
        {
            Case = Case(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 20), upheld: false),
            RegulatorFiledDate = new DateOnly(2026, 3, 25),
        };

        CaseJournalStore.Save(_root, journal);

        Assert.Equal(journal, CaseJournalStore.Load(_root));
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
    /// The one deadline nothing in the milestones could ever mark as met: it falls after
    /// every other date has passed, so only the journal can say the window was used in time.
    /// </summary>
    [Fact]
    public void A_regulator_deadline_met_in_time_is_not_reported_as_missed()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10), responded: new DateOnly(2026, 3, 20));
        var milestones = complaint.Milestones();

        // The window closed on 4 April; the journal says the regulator was reached on 1 April.
        var missed = ComplaintDeadlines.Missed(milestones, new DateOnly(2026, 4, 10), regulatorFiled: new DateOnly(2026, 4, 1));

        Assert.DoesNotContain(missed, m => m.Step == ComplaintStep.RegulatorDue);

        // Without the journal entry the same deadline is outstanding, as it always was.
        var withoutJournal = ComplaintDeadlines.Missed(milestones, new DateOnly(2026, 4, 10));
        Assert.Contains(withoutJournal, m => m.Step == ComplaintStep.RegulatorDue);
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

        // Fifteen days from the unanswered deadline of 25 March - and no longer.
        Assert.Equal(CaseStage.Refused, Case(
            submitted: new DateOnly(2026, 3, 10),
            responded: new DateOnly(2026, 3, 20),
            upheld: false).StageOn(new DateOnly(2026, 4, 2)));
    }
}
