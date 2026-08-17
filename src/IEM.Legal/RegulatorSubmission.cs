using System.Globalization;
using System.Text;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Storage;

namespace IEM.Legal;

/// <summary>
/// Writes the submission to the regulator.
/// <para>
/// Deliberately refuses to write itself until the operator has been given their chance. A
/// submission filed before the operator has answered, or before their deadline has passed,
/// is sent back - and the person has then spent their effort and possibly some of their
/// window on a document that could not be considered.
/// </para>
/// </summary>
public static class RegulatorSubmission
{
    private static readonly CultureInfo Serbian = CultureInfo.GetCultureInfo("sr-Latn-RS");

    public const string RegulatorName = "RATEL - Regulatorna agencija za elektronske komunikacije i poštanske usluge";

    /// <summary>Why a submission cannot be written yet.</summary>
    public enum Obstacle
    {
        None,

        /// <summary>The operator has not been complained to at all.</summary>
        OperatorNotContacted,

        /// <summary>The operator still has time to answer.</summary>
        OperatorStillHasTime,

        /// <summary>The operator accepted the complaint, so there is nothing to escalate.</summary>
        ComplaintUpheld,

        /// <summary>The window to escalate has closed.</summary>
        Expired,
    }

    /// <summary>Whether a submission can be written, and what stands in the way if not.</summary>
    public static Obstacle CanSubmit(ComplaintCase complaint, DateOnly today, ResolvedLegalContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(complaint);

        return complaint.StageOn(today, context ?? complaint.Resolve(today)) switch
        {
            CaseStage.Gathering or CaseStage.ReadyToFile => Obstacle.OperatorNotContacted,
            CaseStage.AwaitingOperator => Obstacle.OperatorStillHasTime,
            CaseStage.Upheld => Obstacle.ComplaintUpheld,
            CaseStage.Expired => Obstacle.Expired,
            _ => Obstacle.None,
        };
    }

    public static string Explain(Obstacle obstacle) => obstacle switch
    {
        Obstacle.OperatorNotContacted =>
            "RATEL-u se obraćate tek nakon što ste se prigovorom obratili operateru. " +
            "Prvo podnesite prigovor i sačuvajte potvrdu prijema.",

        Obstacle.OperatorStillHasTime =>
            "Operateru još nije istekao rok za odgovor. Sačekajte da rok prođe - prijava " +
            "podneta ranije se ne razmatra.",

        Obstacle.ComplaintUpheld =>
            "Operater je usvojio prigovor, pa nema šta da se prosleđuje. Ako problem i dalje " +
            "traje uprkos usvojenom prigovoru, to je nov događaj i nov prigovor.",

        Obstacle.Expired =>
            "Rok za obraćanje RATEL-u je istekao. Za nove prekide pokrenite nov predmet.",

        _ => string.Empty,
    };

    /// <summary>
    /// Builds the submission, or returns null with the reason when it cannot be built yet.
    /// </summary>
    public static string? Build(
        ComplaintCase complaint,
        SessionSnapshot session,
        DateOnly today,
        out Obstacle obstacle,
        ResolvedLegalContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(complaint);
        ArgumentNullException.ThrowIfNull(session);

        var legal = context ?? complaint.Resolve(today);
        obstacle = CanSubmit(complaint, today, legal);

        if (obstacle != Obstacle.None)
        {
            return null;
        }

        var stage = complaint.StageOn(today, legal);
        var milestones = complaint.Milestones(legal);
        var builder = new StringBuilder();

        builder.AppendLine(RegulatorName);
        builder.AppendLine();
        builder.AppendLine($"Mesto i datum: ____________________, {Date(today)}");
        builder.AppendLine();
        builder.AppendLine("PRIJAVA ZBOG NEREŠENOG PRIGOVORA NA KVALITET USLUGE");
        builder.AppendLine();

        builder.AppendLine("PODACI O PODNOSIOCU");
        builder.AppendLine();
        builder.AppendLine($"  Ime i prezime:      {complaint.SubscriberName}");
        builder.AppendLine($"  Adresa usluge:      {complaint.ServiceAddress ?? "____________________"}");
        builder.AppendLine($"  Telefon:            {complaint.ContactPhone ?? "____________________"}");
        builder.AppendLine($"  E-pošta:            {complaint.ContactEmail ?? "____________________"}");
        builder.AppendLine();

        builder.AppendLine("PODACI O OPERATERU I PRIGOVORU");
        builder.AppendLine();
        builder.AppendLine($"  Operater:           {complaint.OperatorName}");
        builder.AppendLine($"  Broj ugovora:       {complaint.ContractNumber ?? "____________________"}");
        builder.AppendLine($"  Prigovor podnet:    {Date(complaint.SubmittedDate!.Value)}");
        builder.AppendLine($"  Broj predmeta:      {complaint.OperatorReference ?? "____________________"}");

        var responseDue = milestones.FirstOrDefault(m => m.Step == ComplaintStep.OperatorResponseDue)?.Date;
        builder.AppendLine($"  Rok za odgovor:     {(responseDue is { } due ? Date(due) : "nije utvrđen")}");

        builder.AppendLine(complaint.OperatorRespondedDate is { } answered
            ? $"  Operater odgovorio: {Date(answered)} - prigovor odbijen"
            : "  Operater odgovorio: nije odgovorio u roku");

        builder.AppendLine();

        builder.AppendLine("RAZLOG PRIJAVE");
        builder.AppendLine();

        builder.AppendLine(Wrap(stage == CaseStage.OperatorSilent
            ? "Operater nije odgovorio na moj prigovor u zakonskom roku, pa se obraćam RATEL-u."
            : "Operater je odbio moj prigovor, a smatram da su prekidi usluge dokazani, pa se " +
              "obraćam RATEL-u."));

        builder.AppendLine();

        AppendLegalBasis(builder, legal, milestones, today);

        AppendEvidence(builder, session);

        builder.AppendLine("ZAHTEV");
        builder.AppendLine();
        builder.AppendLine(Wrap(
            "Molim da se ispita kvalitet usluge na mojoj vezi i da se naloži operateru " +
            "otklanjanje uzroka prekida, kao i srazmerno umanjenje naknade za period " +
            "nedostupnosti usluge."));

        builder.AppendLine();

        builder.AppendLine("PRILOZI");
        builder.AppendLine();
        builder.AppendLine("  1. Prigovor upućen operateru, sa potvrdom prijema");

        builder.AppendLine(complaint.OperatorRespondedDate is not null
            ? "  2. Odgovor operatera"
            : "  2. Dokaz da odgovor operatera nije primljen (izostanak odgovora u roku)");

        builder.AppendLine("  3. Izveštaj o nadzoru veze (Izvestaj.pdf)");
        builder.AppendLine("  4. Sirova evidencija sa lancem otisaka (SirovaEvidencija.jsonl)");
        builder.AppendLine("  5. Potvrda integriteta evidencije (Provera-lanca.txt)");
        builder.AppendLine();

        // The quality pravilnik that applied when the session was recorded, not whichever one
        // is current when this letter happens to be written.
        var quality = LegalSources.QualityPravilnikOn(
            DateOnly.FromDateTime(session.StartedUtc.LocalDateTime));

        builder.AppendLine(Wrap(
            "Napomena: za zvanično merenje brzine koristi se RATEL NetTest aplikacija, " +
            "odnosno merenje na internet portu modema Eternet kablom, kako propisuje " +
            $"{quality}. Evidencija priložena uz ovu prijavu dokumentuje prekide usluge i " +
            "vreme njihovog trajanja, sa zapisom svakog pojedinačnog merenja."));

        builder.AppendLine();

        var decision = LegalRegistry.On(today).RuleFor(
            ComplaintStep.RegulatorDecisionTarget,
            complaint.CustomerType,
            complaint.ServiceKind,
            complaint.ComplaintKind);

        if (decision is not null)
        {
            builder.AppendLine(Wrap(
                $"Postupak vansudskog rešavanja spora pred Agencijom uređen je: " +
                $"{string.Join("; ", decision.CitationsOn(today))}. Agencija donosi rešenje u roku " +
                $"od {decision.Value} dana, s tim da se u složenim slučajevima taj rok može produžiti."));

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("Potpis: ____________________");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine(Wrap(CaseText.Disclaimer));

        return builder.ToString();
    }

    /// <summary>
    /// The provision the request rests on, with the period it prescribes and the date it
    /// falls on - all three from the rule that was applied, none of them written into the text.
    /// <para>
    /// This paragraph used to name article 113 and a period of fifteen days, in a constant of
    /// its own that no longer even matched the one the rest of the program counted with. Both
    /// were the old law's, and by the time anyone read this letter the article had been
    /// replaced.
    /// </para>
    /// </summary>
    private static void AppendLegalBasis(
        StringBuilder builder,
        ResolvedLegalContext legal,
        IReadOnlyList<ComplaintMilestone> milestones,
        DateOnly today)
    {
        var dispute = legal.For(ComplaintStep.RegulatorDisputeDue);

        if (dispute?.Value is not { } days || dispute.Citations.Count == 0)
        {
            builder.AppendLine(Wrap(
                "Rok za obraćanje Agenciji nije bilo moguće pouzdano utvrditi iz podataka o " +
                "predmetu, pa je pre podnošenja potrebno proveriti ga u važećem propisu."));

            builder.AppendLine();
            return;
        }

        var sources = string.Join("; ", dispute.Citations);
        var due = milestones.FirstOrDefault(m => m.Step == ComplaintStep.RegulatorDisputeDue)?.Date;

        builder.AppendLine(Wrap(
            $"Ovlašćenje za ovaj zahtev je {sources}, prema kome se korisnik može obratiti " +
            $"Agenciji u roku od {days} dana od dana dostavljanja odgovora operatera, odnosno " +
            $"od dana isteka roka u kome je operater bio dužan da odgovori." +
            (due is { } date ? $" U ovom predmetu taj rok ističe {Date(date)}." : string.Empty)));

        builder.AppendLine();

        if (legal.State != LegalContextState.Resolved)
        {
            builder.AppendLine(Wrap(legal.State.Explain()));
            builder.AppendLine();
        }
    }

    private static void AppendEvidence(StringBuilder builder, SessionSnapshot session)
    {
        var upstream = session.Incidents.Where(i => i.WorstState.IsUpstream()).ToList();

        builder.AppendLine("PRIKUPLJENI DOKAZI");
        builder.AppendLine();

        builder.AppendLine(Wrap(
            $"Nadzor veze trajao je {SerbianText.Duration(session.MonitoredTime)}, uz zapis svakog " +
            $"uzorka u evidenciju zaštićenu lancem kriptografskih otisaka."));

        builder.AppendLine();

        if (upstream.Count > 0)
        {
            var total = upstream.Aggregate(TimeSpan.Zero, (sum, i) => sum + i.DurationReported);

            builder.AppendLine(Wrap(
                $"Zabeleženo je {upstream.Count} prekida usluge u ukupnom trajanju od " +
                $"{SerbianText.Duration(total)}. Tokom svakog od njih moj ruter je uredno " +
                $"odgovarao, a nijedna meta van lokalne mreže nije bila dostupna, čime je moja " +
                $"lokalna mreža isključena kao uzrok."));

            builder.AppendLine();
        }

        builder.AppendLine(Wrap(
            $"Izmerena dostupnost usluge: " +
            $"{SerbianText.Percent(session.UpstreamAvailabilityPercent)}."));

        builder.AppendLine();
    }

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy.", Serbian);

    private static string Wrap(string text, int width = 78)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        var line = new StringBuilder();

        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                builder.AppendLine(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            builder.Append(line);
        }

        return builder.ToString();
    }
}
