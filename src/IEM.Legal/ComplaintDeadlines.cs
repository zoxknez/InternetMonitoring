namespace IEM.Legal;

/// <summary>A step in the complaint procedure that has a date attached to it.</summary>
public enum ComplaintStep
{
    /// <summary>The fault happened, or the disputed bill arrived.</summary>
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
    /// The last date the matter can be taken to the regulator.
    /// <para>
    /// The step people miss. The complaint gets filed, the operator answers with a refusal
    /// or says nothing at all, and the customer waits - and by the time they think of going
    /// further, the window has closed.
    /// </para>
    /// </summary>
    RegulatorDue,
}

/// <param name="Step">Which step this is.</param>
/// <param name="Date">When it happened, or when it must happen by.</param>
/// <param name="IsDeadline">True for a date to meet, false for something already done.</param>
public sealed record ComplaintMilestone(ComplaintStep Step, DateOnly Date, bool IsDeadline)
{
    /// <summary>Days from <paramref name="today"/> to this date. Negative once it has passed.</summary>
    public int DaysFrom(DateOnly today) => Date.DayNumber - today.DayNumber;
}

/// <summary>
/// The periods the complaint procedure runs on.
/// <para>
/// Held in one place and configurable rather than scattered through the text, because these
/// are the numbers most likely to change and the ones with the worst consequences if they
/// are wrong. A customer told the wrong deadline loses the case on procedure without ever
/// reaching the evidence.
/// </para>
/// </summary>
public sealed record ComplaintPeriods
{
    public static readonly ComplaintPeriods Default = new();

    /// <summary>Days from the event within which the complaint must reach the operator.</summary>
    public int DaysToComplain { get; init; } = 30;

    /// <summary>Days the operator has to answer a complaint.</summary>
    public int DaysForOperatorResponse { get; init; } = 15;

    /// <summary>
    /// Days from the operator's answer - or from the day their answer was due - within
    /// which the matter can be taken to the regulator.
    /// <para>
    /// Zakon o elektronskim komunikacijama ("Sl. glasnik RS" 35/2023), član 113. st. 6:
    /// 15 dana od dostavljanja odgovora, odnosno 15 dana od isteka roka u kome je operater
    /// bio dužan da odgovori. Isti rok navodi i RATEL na svojoj stranici o prigovorima.
    /// Ranije je ovde stajalo 60 dana, što je korisniku obećavalo rok koji ne postoji.
    /// </para>
    /// </summary>
    public int DaysToReachRegulator { get; init; } = 15;

    /// <summary>
    /// Hours within which a reported fault should be repaired.
    /// <para>
    /// Not a deadline in the same sense as the others: nothing is lost by it passing. It is
    /// recorded because a fault left unrepaired past it is itself worth stating in the
    /// complaint.
    /// </para>
    /// </summary>
    public int HoursToRepair { get; init; } = 48;
}

/// <summary>
/// Works out what has to happen by when.
/// <para>
/// Every date here is derived from something the user tells us, and every one of them is
/// stated in the output rather than assumed. The tool cannot know what a particular contract
/// says, and pretending otherwise would produce a confident timetable that happens to be
/// wrong for the person reading it.
/// </para>
/// </summary>
public static class ComplaintDeadlines
{
    /// <summary>
    /// Builds the timetable from what is known so far.
    /// </summary>
    /// <param name="eventDate">When the fault occurred, or the disputed bill arrived.</param>
    /// <param name="submitted">When the complaint was filed, if it has been.</param>
    /// <param name="responded">When the operator answered, if they have.</param>
    public static IReadOnlyList<ComplaintMilestone> Build(
        DateOnly eventDate,
        DateOnly? submitted = null,
        DateOnly? responded = null,
        ComplaintPeriods? periods = null)
    {
        var p = periods ?? ComplaintPeriods.Default;

        var milestones = new List<ComplaintMilestone>
        {
            new(ComplaintStep.Event, eventDate, IsDeadline: false),
            new(ComplaintStep.ComplaintDue, eventDate.AddDays(p.DaysToComplain), IsDeadline: true),
        };

        if (submitted is not { } filed)
        {
            return milestones;
        }

        milestones.Add(new ComplaintMilestone(ComplaintStep.ComplaintSubmitted, filed, IsDeadline: false));

        var responseDue = filed.AddDays(p.DaysForOperatorResponse);
        milestones.Add(new ComplaintMilestone(ComplaintStep.OperatorResponseDue, responseDue, IsDeadline: true));

        if (responded is { } answered)
        {
            milestones.Add(new ComplaintMilestone(ComplaintStep.OperatorResponded, answered, IsDeadline: false));
        }

        // Counted from the answer when there is one, and from the day it was due when there
        // is not. An operator who simply never replies must not thereby postpone the
        // customer's right to go further indefinitely - nor start the clock late.
        var regulatorFrom = responded ?? responseDue;

        milestones.Add(new ComplaintMilestone(
            ComplaintStep.RegulatorDue,
            regulatorFrom.AddDays(p.DaysToReachRegulator),
            IsDeadline: true));

        return milestones;
    }

    /// <summary>
    /// The next thing that has to be done, or null when nothing is outstanding.
    /// </summary>
    public static ComplaintMilestone? NextAction(IReadOnlyList<ComplaintMilestone> milestones, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(milestones);

        return milestones
            .Where(m => m.IsDeadline && m.Date >= today)
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

        return [.. milestones.Where(m => m.IsDeadline && m.Date < today && !WasMet(m.Step, done, regulatorFiled))];
    }

    /// <summary>Whether the action a deadline calls for has been recorded as done.</summary>
    private static bool WasMet(
        ComplaintStep deadline,
        IReadOnlySet<ComplaintStep> recorded,
        DateOnly? regulatorFiled) => deadline switch
    {
        ComplaintStep.ComplaintDue => recorded.Contains(ComplaintStep.ComplaintSubmitted),
        ComplaintStep.OperatorResponseDue => recorded.Contains(ComplaintStep.OperatorResponded),

        // Recorded in the case journal rather than the milestones, because it is the one
        // step that happens after every other date has already passed.
        ComplaintStep.RegulatorDue => regulatorFiled is not null,
        _ => false,
    };
}
