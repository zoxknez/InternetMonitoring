using IEM.Legal;

namespace IEM.Core.Tests;

/// <summary>
/// The deadlines are the part of this module with the worst consequences if they are wrong.
/// A customer told the wrong date loses on procedure without anyone ever looking at the
/// evidence they spent two days collecting.
/// </summary>
public sealed class ComplaintDeadlineTests
{
    private static readonly DateOnly Fault = new(2026, 3, 2);

    private static ComplaintCase Case(DateOnly? submitted = null, DateOnly? responded = null, bool? upheld = null) =>
        new()
        {
            OperatorName = "Operater",
            SubscriberName = "Petar Petrović",
            EventDate = Fault,
            SubmittedDate = submitted,
            OperatorRespondedDate = responded,
            OperatorUpheld = upheld,
        };

    // ---- The three periods ----------------------------------------------------

    [Fact]
    public void The_complaint_is_due_thirty_days_after_the_fault()
    {
        var due = ComplaintDeadlines.Build(Fault)
            .First(m => m.Step == ComplaintStep.ComplaintDue);

        Assert.Equal(new DateOnly(2026, 4, 1), due.Date);
        Assert.True(due.IsDeadline);
    }

    [Fact]
    public void The_operator_has_fifteen_days_from_the_complaint()
    {
        var filed = new DateOnly(2026, 3, 10);

        var due = ComplaintDeadlines.Build(Fault, filed)
            .First(m => m.Step == ComplaintStep.OperatorResponseDue);

        Assert.Equal(new DateOnly(2026, 3, 25), due.Date);
    }

    /// <summary>
    /// Zakon o elektronskim komunikacijama ("Sl. glasnik RS" 35/2023), član 113. st. 6:
    /// petnaest dana od dostavljanja odgovora operatera. Ranije je ovde važilo šezdeset,
    /// rok koji zakon ne daje.
    /// </summary>
    [Fact]
    public void The_regulator_window_is_fifteen_days_from_the_operators_answer()
    {
        var filed = new DateOnly(2026, 3, 10);
        var answered = new DateOnly(2026, 3, 20);

        var due = ComplaintDeadlines.Build(Fault, filed, answered)
            .First(m => m.Step == ComplaintStep.RegulatorDue);

        Assert.Equal(new DateOnly(2026, 4, 4), due.Date);
    }

    /// <summary>
    /// An operator who simply never replies must not thereby postpone the customer's right
    /// to go further indefinitely - nor start their clock late.
    /// </summary>
    [Fact]
    public void A_silent_operator_does_not_stop_the_regulator_clock()
    {
        var filed = new DateOnly(2026, 3, 10);

        var due = ComplaintDeadlines.Build(Fault, filed)
            .First(m => m.Step == ComplaintStep.RegulatorDue);

        // Counted from the day the answer was due, not from some indefinite future.
        Assert.Equal(new DateOnly(2026, 4, 9), due.Date);
    }

    // ---- Where the case stands ------------------------------------------------

    [Fact]
    public void Before_filing_the_case_is_ready_to_file()
    {
        Assert.Equal(CaseStage.ReadyToFile, Case().StageOn(new DateOnly(2026, 3, 5)));
    }

    [Fact]
    public void After_the_window_closes_the_case_is_expired()
    {
        Assert.Equal(CaseStage.Expired, Case().StageOn(new DateOnly(2026, 4, 2)));
    }

    [Fact]
    public void While_the_operator_still_has_time_the_case_waits()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        Assert.Equal(CaseStage.AwaitingOperator, complaint.StageOn(new DateOnly(2026, 3, 20)));
    }

    [Fact]
    public void Once_the_operators_deadline_passes_their_silence_is_the_finding()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        Assert.Equal(CaseStage.OperatorSilent, complaint.StageOn(new DateOnly(2026, 3, 26)));
    }

    [Fact]
    public void A_refusal_leaves_the_regulator_as_the_next_step()
    {
        var complaint = Case(
            submitted: new DateOnly(2026, 3, 10),
            responded: new DateOnly(2026, 3, 20),
            upheld: false);

        Assert.Equal(CaseStage.Refused, complaint.StageOn(new DateOnly(2026, 3, 25)));
    }

    [Fact]
    public void An_upheld_complaint_is_finished()
    {
        var complaint = Case(
            submitted: new DateOnly(2026, 3, 10),
            responded: new DateOnly(2026, 3, 20),
            upheld: true);

        Assert.Equal(CaseStage.Upheld, complaint.StageOn(new DateOnly(2026, 3, 25)));
    }

    /// <summary>
    /// The status is derived from the dates rather than stored, so a case cannot be marked
    /// "waiting for the operator" while its deadline sits three weeks in the past.
    /// </summary>
    [Fact]
    public void The_status_can_never_contradict_the_dates()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        Assert.Equal(CaseStage.AwaitingOperator, complaint.StageOn(new DateOnly(2026, 3, 24)));
        Assert.Equal(CaseStage.OperatorSilent, complaint.StageOn(new DateOnly(2026, 3, 26)));

        // The regulator window closed on 9 April: fifteen days from the unanswered deadline.
        Assert.Equal(CaseStage.OperatorSilent, complaint.StageOn(new DateOnly(2026, 4, 8)));
        Assert.Equal(CaseStage.Expired, complaint.StageOn(new DateOnly(2026, 4, 10)));
    }

    // ---- What to do next ------------------------------------------------------

    [Fact]
    public void The_next_action_is_the_nearest_deadline_still_ahead()
    {
        var milestones = ComplaintDeadlines.Build(Fault, new DateOnly(2026, 3, 10));

        var next = ComplaintDeadlines.NextAction(milestones, new DateOnly(2026, 3, 12));

        Assert.NotNull(next);
        Assert.Equal(ComplaintStep.OperatorResponseDue, next.Step);
        Assert.Equal(13, next.DaysFrom(new DateOnly(2026, 3, 12)));
    }

    [Fact]
    public void A_deadline_that_was_met_is_not_reported_as_missed()
    {
        var milestones = ComplaintDeadlines.Build(Fault, new DateOnly(2026, 3, 10));

        var missed = ComplaintDeadlines.Missed(milestones, new DateOnly(2026, 4, 5));

        // The complaint was filed in time, so only the operator's silence is outstanding.
        Assert.DoesNotContain(missed, m => m.Step == ComplaintStep.ComplaintDue);
        Assert.Contains(missed, m => m.Step == ComplaintStep.OperatorResponseDue);
    }

    [Fact]
    public void Every_stage_says_what_to_do_next()
    {
        foreach (var stage in Enum.GetValues<CaseStage>())
        {
            Assert.NotEmpty(stage.Label());
            Assert.NotEmpty(stage.WhatNow());
        }
    }

    /// <summary>
    /// A missed window for one fault does not extinguish the right to complain about the
    /// next one, and someone told only "expired" would reasonably conclude otherwise.
    /// </summary>
    [Fact]
    public void An_expired_case_says_that_a_new_fault_starts_a_new_clock()
    {
        Assert.Contains("nov predmet", CaseStage.Expired.WhatNow(), StringComparison.Ordinal);
    }
}
