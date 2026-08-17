using IEM.Legal;

namespace IEM.Core.Tests;

/// <summary>
/// The deadlines are the part of this module with the worst consequences if they are wrong.
/// A customer told the wrong date loses on procedure without anyone ever looking at the
/// evidence they spent two days collecting.
/// <para>
/// Every case here is dated in 2026, so the current regime governs it. The transitional ones
/// live in <see cref="LegalTransitionTests"/>, where they belong.
/// </para>
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
            EventOrigin = FactOrigin.UserProvided,
            SubmittedDate = submitted,
            OperatorRespondedDate = responded,
            OperatorUpheld = upheld,
        };

    private static IReadOnlyList<ComplaintMilestone> Milestones(
        DateOnly? submitted = null,
        DateOnly? responded = null,
        DateOnly on = default) =>
        Case(submitted, responded).Milestones(on == default ? new DateOnly(2026, 3, 12) : on);

    // ---- The periods ----------------------------------------------------------

    [Fact]
    public void The_complaint_is_due_thirty_days_after_the_fault()
    {
        var due = Milestones().First(m => m.Step == ComplaintStep.ComplaintDue);

        Assert.Equal(new DateOnly(2026, 4, 1), due.Date);
        Assert.True(due.IsDeadline);
        Assert.Equal(LegalAnchor.ServiceUnavailableDate, due.Rule!.Anchor);
    }

    /// <summary>
    /// Eight days, not fifteen. Fifteen was the old law's period for every subscriber; for a
    /// consumer the answer is owed within eight under the consumer protection act, which the
    /// current ZEK refers to.
    /// </summary>
    [Fact]
    public void A_consumer_is_owed_an_answer_within_eight_days()
    {
        var due = Milestones(submitted: new DateOnly(2026, 3, 10))
            .First(m => m.Step == ComplaintStep.OperatorResponseDue);

        Assert.Equal(new DateOnly(2026, 3, 18), due.Date);
        Assert.Contains(due.Rule!.Citations, c => c.SourceId.StartsWith("RS-ZZP", StringComparison.Ordinal));
    }

    /// <summary>
    /// Sixty days, not fifteen. The fifteen came from article 113 of the old law, which stood
    /// only until the act under article 140 of the current one was adopted - and it was.
    /// </summary>
    [Fact]
    public void The_regulator_window_is_sixty_days_from_the_operators_answer()
    {
        var due = Milestones(submitted: new DateOnly(2026, 3, 10), responded: new DateOnly(2026, 3, 20))
            .First(m => m.Step == ComplaintStep.RegulatorDisputeDue);

        Assert.Equal(new DateOnly(2026, 5, 19), due.Date);
        Assert.Contains(due.Rule!.Citations, c => c.Article == "139");
    }

    /// <summary>
    /// An operator who simply never replies must not thereby postpone the customer's right
    /// to go further indefinitely - nor start their clock late.
    /// </summary>
    [Fact]
    public void A_silent_operator_does_not_stop_the_regulator_clock()
    {
        var due = Milestones(submitted: new DateOnly(2026, 3, 10))
            .First(m => m.Step == ComplaintStep.RegulatorDisputeDue);

        // Counted from the day the answer was due - 18 March - not from some indefinite future.
        Assert.Equal(new DateOnly(2026, 5, 17), due.Date);
    }

    /// <summary>
    /// Every period a person is shown has to be checkable. A date without its source is one
    /// they have to take on trust, and these are numbers that move.
    /// </summary>
    [Fact]
    public void Every_deadline_carries_its_source()
    {
        var milestones = Milestones(submitted: new DateOnly(2026, 3, 10), responded: new DateOnly(2026, 3, 20));

        foreach (var milestone in milestones.Where(m => m.IsDeadline))
        {
            Assert.NotNull(milestone.Rule);
            Assert.NotEmpty(milestone.Rule!.Citations);

            foreach (var citation in milestone.Rule.Citations)
            {
                Assert.NotEmpty(citation.Url);
                Assert.NotEqual(default, citation.VerifiedAt);
            }
        }
    }

    /// <summary>A company is not a consumer, and the period is a different one.</summary>
    [Fact]
    public void A_company_gets_the_sector_period_rather_than_the_consumer_one()
    {
        var company = Case(submitted: new DateOnly(2026, 3, 10)) with { CustomerType = CustomerType.NonConsumer };

        var due = company.Milestones(new DateOnly(2026, 3, 12))
            .First(m => m.Step == ComplaintStep.OperatorResponseDue);

        Assert.Equal(new DateOnly(2026, 4, 9), due.Date);
        Assert.DoesNotContain(due.Rule!.Citations, c => c.SourceId.StartsWith("RS-ZZP", StringComparison.Ordinal));
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

        Assert.Equal(CaseStage.AwaitingOperator, complaint.StageOn(new DateOnly(2026, 3, 15)));
    }

    [Fact]
    public void Once_the_operators_deadline_passes_their_silence_is_the_finding()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10));

        Assert.Equal(CaseStage.OperatorSilent, complaint.StageOn(new DateOnly(2026, 3, 20)));
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

        Assert.Equal(CaseStage.AwaitingOperator, complaint.StageOn(new DateOnly(2026, 3, 17)));
        Assert.Equal(CaseStage.OperatorSilent, complaint.StageOn(new DateOnly(2026, 3, 19)));

        // The window to reach the Regulator closed on 17 May: sixty days from the unanswered
        // deadline of 18 March.
        Assert.Equal(CaseStage.OperatorSilent, complaint.StageOn(new DateOnly(2026, 5, 16)));
        Assert.Equal(CaseStage.Expired, complaint.StageOn(new DateOnly(2026, 5, 18)));
    }

    // ---- What to do next ------------------------------------------------------

    [Fact]
    public void The_next_action_is_the_nearest_deadline_still_ahead()
    {
        var milestones = Milestones(submitted: new DateOnly(2026, 3, 10));

        var next = ComplaintDeadlines.NextAction(milestones, new DateOnly(2026, 3, 12));

        Assert.NotNull(next);
        Assert.Equal(ComplaintStep.OperatorResponseDue, next.Step);
        Assert.Equal(6, next.DaysFrom(new DateOnly(2026, 3, 12)));
    }

    [Fact]
    public void A_deadline_that_was_met_is_not_reported_as_missed()
    {
        var milestones = Milestones(submitted: new DateOnly(2026, 3, 10));

        var missed = ComplaintDeadlines.Missed(milestones, new DateOnly(2026, 4, 5));

        // The complaint was filed in time, so only the operator's silence is outstanding.
        Assert.DoesNotContain(missed, m => m.Step == ComplaintStep.ComplaintDue);
        Assert.Contains(missed, m => m.Step == ComplaintStep.OperatorResponseDue);
    }

    /// <summary>
    /// Recording the day the request reached the Regulator is what stops the case reporting
    /// that window as missed forever. Nothing in production ever wrote it until 2.7, so every
    /// case that got that far carried a permanent false alarm.
    /// </summary>
    [Fact]
    public void A_window_that_was_used_in_time_stops_being_reported_as_missed()
    {
        var complaint = Case(submitted: new DateOnly(2026, 3, 10), responded: new DateOnly(2026, 3, 20));
        var late = new DateOnly(2026, 7, 1);

        Assert.Contains(
            ComplaintDeadlines.Missed(complaint.Milestones(late), late, complaint.RegulatorFiledDate),
            m => m.Step == ComplaintStep.RegulatorDisputeDue);

        var filed = complaint with { RegulatorFiledDate = new DateOnly(2026, 5, 10) };

        Assert.DoesNotContain(
            ComplaintDeadlines.Missed(filed.Milestones(late), late, filed.RegulatorFiledDate),
            m => m.Step == ComplaintStep.RegulatorDisputeDue);
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

    /// <summary>
    /// Forty-eight hours is the time an operator has to clear a fault, not a minimum length
    /// for the monitoring - and it was quoted here as though it were the latter.
    /// </summary>
    [Fact]
    public void Gathering_evidence_is_not_described_as_a_forty_eight_hour_rule()
    {
        var text = CaseStage.Gathering.WhatNow();

        Assert.DoesNotContain("48 sati", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ne može osporiti", text, StringComparison.Ordinal);
    }
}
