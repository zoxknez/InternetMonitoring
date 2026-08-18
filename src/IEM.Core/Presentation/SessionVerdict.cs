namespace IEM.Core.Presentation;

public enum VerdictKind
{
    /// <summary>Not enough observation to conclude anything.</summary>
    TooShort,

    /// <summary>No outages at all.</summary>
    Stable,

    /// <summary>Outages, but all of them on the customer's own side.</summary>
    LocalFault,

    /// <summary>
    /// Outages isolated to the path beyond the customer's own equipment.
    /// <para>
    /// Not "the operator is at fault". A monitor running on the customer's computer is not
    /// a probe inside the operator's network and cannot say what happens there. What it can
    /// say - and this is what the name means - is that the local network and the router were
    /// working throughout, so neither of them accounts for the outage.
    /// </para>
    /// </summary>
    UpstreamFault,
}

/// <param name="Headline">One line stating the conclusion.</param>
/// <param name="Detail">A sentence saying what it means for the reader.</param>
public sealed record SessionVerdict(VerdictKind Kind, string Headline, string Detail)
{
    /// <summary>
    /// Below this there is not enough observation to say anything useful, and a confident
    /// verdict drawn from a few seconds would be worse than no verdict at all.
    /// </summary>
    public static readonly TimeSpan MinimumUsefulDuration = TimeSpan.FromMinutes(1);

    public bool SupportsComplaint => Kind == VerdictKind.UpstreamFault;

    /// <summary>
    /// The single conclusion the whole application exists to deliver, defined once.
    /// <para>
    /// Shared by the window, the console and the exported report deliberately. Three
    /// separate implementations of "is there a case here" would eventually disagree, and
    /// a report contradicting the screen that produced it is worthless.
    /// </para>
    /// </summary>
    public static SessionVerdict Evaluate(
        TimeSpan monitoredTime,
        int upstreamIncidentCount,
        TimeSpan localDowntime)
    {
        if (monitoredTime < MinimumUsefulDuration)
        {
            return new SessionVerdict(
                VerdictKind.TooShort,
                "Test je prekratak",
                "Nadzor je trajao prekratko da bi se izveo zaključak. Pustite ga bar nekoliko sati.");
        }

        if (upstreamIncidentCount > 0)
        {
            var word = Plural(upstreamIncidentCount, "prekid", "prekida", "prekida");

            // Deliberately not "confirmed at the operator". Nothing measured from inside the
            // customer's house establishes that, and a complaint that opens by claiming it
            // hands the operator the easiest possible rebuttal. What was actually measured -
            // the router answering throughout while nothing beyond it did - is both true and
            // much harder to dismiss.
            return new SessionVerdict(
                VerdictKind.UpstreamFault,
                "Prekidi izolovani iza vaše opreme",
                $"Zabeleženo je {upstreamIncidentCount} {word} tokom kojih je vaša lokalna mreža " +
                "radila, a ništa iza rutera nije bilo dostupno. Putanja između računara i rutera " +
                "je time isključena kao uzrok - WAN strana samog rutera nije. To je osnov za " +
                "prigovor operateru, koji na njega mora da odgovori.");
        }

        if (localDowntime > TimeSpan.Zero)
        {
            return new SessionVerdict(
                VerdictKind.LocalFault,
                "Prekidi postoje, ali su lokalni",
                "Uzrok je na vašem računaru, Wi-Fi vezi ili ruteru. Prigovor operateru po ovim " +
                "prekidima najverovatnije ne bi bio prihvaćen - rešite ih prvo.");
        }

        // Not "veza je bila stabilna". That is a statement about the connection; what was
        // established is a statement about the period that was watched, and the two come
        // apart precisely in the case people run this tool for - a fault that appears in the
        // evening, on a line that measures perfectly all afternoon. The duration is named for
        // the same reason: a clean quarter of an hour and a clean three days are not the same
        // finding, and a headline that reads identically for both invites the wrong reading.
        return new SessionVerdict(
            VerdictKind.Stable,
            "Nije zabeležen nijedan prekid",
            $"Tokom ove sesije ({SerbianText.Duration(monitoredTime)}) nisu zabeleženi događaji " +
            "koji ukazuju na prekid veze. Rezultat opisuje samo posmatrani period i ne govori " +
            "o vremenu koje nije nadzirano.");
    }

    /// <summary>Serbian has three plural forms; getting this wrong reads as machine output.</summary>
    public static string Plural(int count, string one, string few, string many)
    {
        var mod100 = count % 100;
        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return (count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}
