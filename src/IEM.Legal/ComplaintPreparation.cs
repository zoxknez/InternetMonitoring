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

    /// <summary>
    /// Said when the session offered more than one outage to date the complaint from.
    /// <para>
    /// The program proposes the first and names it; which one the complaint is actually about
    /// is the subscriber's to say, and every deadline in the case is counted from it.
    /// </para>
    /// </summary>
    public string? AnchorNote { get; init; }
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
    /// <param name="incidentNumber">
    /// Which recorded outage the complaint is about, when the subscriber has said. A session
    /// with several offers several candidate dates, and every deadline is counted from the
    /// one chosen - so the program proposes rather than decides.
    /// </param>
    public static ComplaintPreparationResult From(
        SessionSnapshot session,
        string? operatorName,
        DateOnly today,
        int? incidentNumber = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (Refuse(session) is { } refusal)
        {
            return new ComplaintPreparationResult(null, null, refusal);
        }

        var candidates = UpstreamOutages(session);
        var chosen = incidentNumber is { } number
            ? candidates.FirstOrDefault(i => i.Number == number)
            : candidates.FirstOrDefault();

        if (incidentNumber is not null && chosen is null)
        {
            return new ComplaintPreparationResult(
                null,
                null,
                $"Prekid broj {incidentNumber} ne postoji u ovoj sesiji, ili nije bio izolovan iza rutera.");
        }

        // One candidate, or one the subscriber named: the date is established and says what
        // it was taken from. Several with nothing said: it is a proposal, recorded as one.
        var settled = chosen is not null && (incidentNumber is not null || candidates.Count == 1);

        var complaint = new ComplaintCase
        {
            OperatorName = string.IsNullOrWhiteSpace(operatorName) ? "____________________" : operatorName,
            SubscriberName = "____________________",
            EventDate = chosen is null
                ? DateOnly.FromDateTime(session.StartedUtc.LocalDateTime)
                : DateOnly.FromDateTime(chosen.StartedUtc.LocalDateTime),
            EventOrigin = settled ? FactOrigin.DerivedFromSession : FactOrigin.Unknown,
            EventEvidenceRef = chosen is null ? null : $"prekid-{chosen.Number}",
        };

        return new ComplaintPreparationResult(
            complaint,
            ComplaintLetter.ToOperator(complaint, session, today),
            null)
        {
            AnchorNote = settled || chosen is null
                ? null
                : $"Sesija ima {candidates.Count} prekida izolovanih iza rutera. Rokovi su " +
                  $"računati od prvog (prekid {chosen.Number}, " +
                  $"{DateOnly.FromDateTime(chosen.StartedUtc.LocalDateTime):dd.MM.yyyy.}). Ako se " +
                  "prigovor odnosi na drugi, izaberite ga - svi rokovi teku od njega.",
        };
    }

    /// <summary>The outages a complaint could be dated from, in the order they happened.</summary>
    public static IReadOnlyList<IncidentRow> UpstreamOutages(SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return
        [
            .. session.Incidents
                .Where(i => i.Attribution == FaultAttribution.Upstream)
                .OrderBy(i => i.StartedUtc),
        ];
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
                ? $"Zabeleženo je {session.Incidents.Count} prekida, ali nijedan nije bio izolovan " +
                  "iza rutera. Takvi prekidi ne mogu da nose prigovor."
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
