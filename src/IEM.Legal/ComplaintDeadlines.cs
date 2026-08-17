namespace IEM.Legal;

/// <summary>A step in the complaint procedure that has a date attached to it.</summary>
public enum ComplaintStep
{
    /// <summary>The fault happened, or the disputed bill fell due.</summary>
    Event,

    /// <summary>The complaint has to reach the operator by this date.</summary>
    ComplaintDue,

    /// <summary>The complaint was submitted.</summary>
    ComplaintSubmitted,

    /// <summary>The operator has to answer by this date.</summary>
    OperatorResponseDue,

    /// <summary>The operator answered.</summary>
    OperatorResponded,

    /// <summary>
    /// The last date a request for out-of-court settlement can reach the Regulator.
    /// <para>
    /// The step people miss. The complaint gets filed, the operator answers with a refusal or
    /// says nothing at all, and the customer waits - and by the time they think of going
    /// further, the window has closed.
    /// </para>
    /// </summary>
    RegulatorDisputeDue,

    /// <summary>
    /// When the Regulator's decision is due, once the proceeding has been started.
    /// <para>
    /// Not a deadline for the subscriber: nothing of theirs is lost by it passing. It is
    /// stated so they know what to expect, and so a proceeding that has gone quiet past it is
    /// something they can ask about.
    /// </para>
    /// </summary>
    RegulatorDecisionTarget,
}

/// <param name="Step">Which step this is.</param>
/// <param name="Date">
/// When it happened, or when it must happen by - and null when the rules could not settle it.
/// A missing date is a finding in its own right and must not be filled in with a guess.
/// </param>
/// <param name="IsDeadline">True for a date to meet, false for something already done.</param>
public sealed record ComplaintMilestone(ComplaintStep Step, DateOnly? Date, bool IsDeadline)
{
    /// <summary>The rule this date came from, where it came from one.</summary>
    public AppliedRule? Rule { get; init; }

    public LegalContextState State { get; init; } = LegalContextState.Resolved;

    /// <summary>Days from <paramref name="today"/> to this date. Negative once it has passed.</summary>
    public int? DaysFrom(DateOnly today) => Date is { } date ? date.DayNumber - today.DayNumber : null;
}

/// <summary>
/// Works out what has to happen by when.
/// <para>
/// Every date here comes from a rule in <see cref="LegalRegistry"/>, and every rule carries
/// what it was counted from and where it comes from. Nothing is a constant: the periods this
/// program used to hold as defaults - fifteen days for an answer, fifteen to reach the
/// Regulator - were the old law's, and no call site ever passed anything else, so the comment
/// promising they were configurable was describing something that had never happened.
/// </para>
/// </summary>
public static class ComplaintDeadlines
{
    /// <summary>Builds the timetable from the rules that applied to this case.</summary>
    public static IReadOnlyList<ComplaintMilestone> Build(
        ResolvedLegalContext context,
        AnchoredDate? eventDate,
        DateOnly? submitted = null,
        DateOnly? responded = null,
        DateOnly? regulatorFiled = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        var milestones = new List<ComplaintMilestone>();

        if (eventDate is not null)
        {
            milestones.Add(new ComplaintMilestone(ComplaintStep.Event, eventDate.Date, IsDeadline: false));
        }

        milestones.Add(Deadline(ComplaintStep.ComplaintDue));

        if (submitted is not { } filed)
        {
            return milestones;
        }

        milestones.Add(new ComplaintMilestone(ComplaintStep.ComplaintSubmitted, filed, IsDeadline: false));
        milestones.Add(Deadline(ComplaintStep.OperatorResponseDue));

        if (responded is { } answered)
        {
            milestones.Add(new ComplaintMilestone(ComplaintStep.OperatorResponded, answered, IsDeadline: false));
        }

        milestones.Add(Deadline(ComplaintStep.RegulatorDisputeDue));

        // Only once the proceeding exists. Printing a target date for a decision nobody has
        // asked for would be inventing a step that has not happened.
        if (regulatorFiled is not null && context.For(ComplaintStep.RegulatorDecisionTarget) is not null)
        {
            milestones.Add(Deadline(ComplaintStep.RegulatorDecisionTarget));
        }

        return milestones;

        ComplaintMilestone Deadline(ComplaintStep step)
        {
            var applied = context.For(step);

            return new ComplaintMilestone(step, applied?.Due, IsDeadline: true)
            {
                Rule = applied,
                State = applied?.State ?? LegalContextState.Unresolved,
            };
        }
    }

    /// <summary>
    /// The next thing that has to be done, or null when nothing is outstanding.
    /// <para>
    /// A deadline whose date could not be settled is not offered as the next action. Telling
    /// somebody to act by a date the program could not work out is worse than saying nothing.
    /// </para>
    /// </summary>
    public static ComplaintMilestone? NextAction(IReadOnlyList<ComplaintMilestone> milestones, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(milestones);

        return milestones
            .Where(m => m.IsDeadline && m.Date is { } date && date >= today)
            .OrderBy(m => m.Date)
            .FirstOrDefault();
    }

    /// <summary>Deadlines that have already passed without being met.</summary>
    /// <param name="regulatorFiled">When the regulator was contacted, once they were.</param>
    public static IReadOnlyList<ComplaintMilestone> Missed(
        IReadOnlyList<ComplaintMilestone> milestones,
        DateOnly today,
        DateOnly? regulatorFiled = null)
    {
        ArgumentNullException.ThrowIfNull(milestones);

        var done = milestones.Select(m => m.Step).ToHashSet();

        // An unsettled date is never reported as missed. "I could not work out your deadline"
        // must not become "your deadline has passed" - which is the substitution that would
        // stop somebody from filing a complaint they were still entitled to file.
        return
        [
            .. milestones.Where(m =>
                m.IsDeadline &&
                m.Date is { } date &&
                date < today &&
                !WasMet(m.Step, done, regulatorFiled)),
        ];
    }

    /// <summary>Deadlines the rules could not settle, which the output has to say out loud.</summary>
    public static IReadOnlyList<ComplaintMilestone> Unsettled(IReadOnlyList<ComplaintMilestone> milestones)
    {
        ArgumentNullException.ThrowIfNull(milestones);

        return [.. milestones.Where(m => m.IsDeadline && m.Date is null)];
    }

    /// <summary>Whether the action a deadline calls for has been recorded as done.</summary>
    private static bool WasMet(
        ComplaintStep deadline,
        IReadOnlySet<ComplaintStep> recorded,
        DateOnly? regulatorFiled) => deadline switch
    {
        ComplaintStep.ComplaintDue => recorded.Contains(ComplaintStep.ComplaintSubmitted),
        ComplaintStep.OperatorResponseDue => recorded.Contains(ComplaintStep.OperatorResponded),

        // Recorded on the case rather than derived from the milestones, because it is the one
        // step that happens after every other date has already passed.
        ComplaintStep.RegulatorDisputeDue => regulatorFiled is not null,

        // Not the subscriber's deadline at all: nothing of theirs is lost by it passing.
        ComplaintStep.RegulatorDecisionTarget => true,
        _ => false,
    };
}
