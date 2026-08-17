using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Storage;

namespace IEM.Legal;

/// <param name="Refusal">Why nothing was prepared, when nothing was.</param>
public sealed record ComplaintPreparationResult(
    ComplaintCase? Case,
    string? Letter,
    string? Refusal)
{
    public bool Prepared => Case is not null;
}

/// <summary>
/// Decides whether a session supports a complaint, and writes it if so.
/// <para>
/// One implementation, called by both the console and the window. Two would drift, and a
/// window offering to write a complaint the console would refuse - or the reverse - is
/// worse than either behaviour alone.
/// </para>
/// <para>
/// It lives here rather than in the interface project so it can be tested. Logic that
/// decides whether someone has grounds for a complaint has no business sitting where no
/// test can reach it.
/// </para>
/// </summary>
public static class ComplaintPreparation
{
    /// <summary>
    /// Prepares the complaint, or refuses with a reason.
    /// </summary>
    /// <param name="operatorName">Whoever the contract is with, when it is known.</param>
    /// <param name="today">The day the letter is dated.</param>
    public static ComplaintPreparationResult From(
        SessionSnapshot session,
        string? operatorName,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Refuse(session) is { } refusal)
        {
            return new ComplaintPreparationResult(null, null, refusal);
        }

        var complaint = new ComplaintCase
        {
            OperatorName = string.IsNullOrWhiteSpace(operatorName) ? "____________________" : operatorName,
            SubscriberName = "____________________",
            EventDate = FirstUpstreamOutage(session) ?? DateOnly.FromDateTime(session.StartedUtc.LocalDateTime),
        };

        return new ComplaintPreparationResult(
            complaint,
            ComplaintLetter.ToOperator(complaint, session, today),
            null);
    }

    /// <summary>
    /// Why this session cannot support a complaint, or null when it can.
    /// <para>
    /// Two ways it might not: nothing went wrong, or something did but the recording is too
    /// short for anyone to draw a conclusion from. Both refuse rather than produce a
    /// document, because a complaint that overstates what was measured is worse for its
    /// author than none - the operator answers its weakest sentence and the rest goes with it.
    /// </para>
    /// </summary>
    public static string? Refuse(SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var upstream = session.Incidents.Count(i => i.Attribution == FaultAttribution.Upstream);

        if (upstream == 0)
        {
            return session.Incidents.Count > 0
                ? $"Zabeleženo je {session.Incidents.Count} prekida, ali nijedan nije isključio vašu " +
                  "opremu kao uzrok. Takvi prekidi se ne mogu pripisati operateru."
                : "U ovoj sesiji nije zabeležen nijedan prekid usluge, pa nema osnova za prigovor.";
        }

        return session.MonitoredTime < SessionVerdict.MinimumUsefulDuration
            ? $"Nadzor je trajao samo {SerbianText.Duration(session.MonitoredTime)}, što je prekratko " +
              "da bi se izveo zaključak koji se može braniti."
            : null;
    }

    /// <summary>
    /// The date the clock runs from: the first outage that ruled out the customer's own
    /// equipment, since that is the one the complaint is about.
    /// </summary>
    public static DateOnly? FirstUpstreamOutage(SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var first = session.Incidents
            .Where(i => i.Attribution == FaultAttribution.Upstream)
            .OrderBy(i => i.StartedUtc)
            .FirstOrDefault();

        return first is null ? null : DateOnly.FromDateTime(first.StartedUtc.LocalDateTime);
    }
}
