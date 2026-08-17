using System.Globalization;
using System.Text;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Storage;

namespace IEM.Legal;

/// <summary>
/// Writes the complaint, filled in from the recorded session.
/// <para>
/// The point is that nobody has to compose it. Someone who has just spent two days
/// collecting evidence of a broken connection should not then have to work out how to phrase
/// a complaint, which figures matter, or what to attach - that is where most complaints
/// quietly stop.
/// </para>
/// <para>
/// What it will not do is overstate. Every figure comes from the session, the wording says
/// what was measured rather than who is at fault, and where the evidence is weak it says so
/// - a letter that claims more than the attachments support is the easiest kind for an
/// operator to answer.
/// </para>
/// </summary>
public static class ComplaintLetter
{
    private static readonly CultureInfo Serbian = CultureInfo.GetCultureInfo("sr-Latn-RS");

    /// <summary>Builds the complaint to the operator.</summary>
    public static string ToOperator(ComplaintCase complaint, SessionSnapshot session, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(complaint);
        ArgumentNullException.ThrowIfNull(session);

        var builder = new StringBuilder();

        builder.AppendLine($"{complaint.OperatorName}");
        builder.AppendLine("Služba za prigovore korisnika");
        builder.AppendLine();
        builder.AppendLine($"Mesto i datum: ____________________, {Date(today)}");
        builder.AppendLine();
        builder.AppendLine("PRIGOVOR NA KVALITET USLUGE PRISTUPA INTERNETU");
        builder.AppendLine();

        AppendSubscriber(builder, complaint);
        AppendFindings(builder, complaint, session);
        AppendRequest(builder, session);
        AppendAttachments(builder, session);

        builder.AppendLine("Potpis: ____________________");
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine(Wrap(CaseText.Disclaimer));

        return builder.ToString();
    }

    private static void AppendSubscriber(StringBuilder builder, ComplaintCase complaint)
    {
        builder.AppendLine("PODACI O KORISNIKU");
        builder.AppendLine();
        builder.AppendLine($"  Ime i prezime:      {complaint.SubscriberName}");
        builder.AppendLine($"  Broj ugovora:       {complaint.ContractNumber ?? "____________________"}");
        builder.AppendLine($"  Adresa usluge:      {complaint.ServiceAddress ?? "____________________"}");
        builder.AppendLine($"  Telefon:            {complaint.ContactPhone ?? "____________________"}");
        builder.AppendLine($"  E-pošta:            {complaint.ContactEmail ?? "____________________"}");
        builder.AppendLine();
    }

    private static void AppendFindings(StringBuilder builder, ComplaintCase complaint, SessionSnapshot session)
    {
        var monitored = session.MonitoredTime;

        // Only the outages that ruled out the customer's own equipment. Listing every
        // recorded fault would put the customer's own Wi-Fi drops in a letter complaining
        // about the operator, and hand them the answer.
        var upstream = session.Incidents
            .Where(i => i.WorstState.IsUpstream())
            .OrderByDescending(i => i.DurationReported)
            .ToList();

        builder.AppendLine("PREDMET PRIGOVORA");
        builder.AppendLine();

        builder.AppendLine(Wrap(
            $"Od {Date(DateOnly.FromDateTime(session.StartedUtc.LocalDateTime))} vršio sam neprekidno " +
            $"merenje kvaliteta pristupa internetu na svojoj vezi. Nadzor je trajao " +
            $"{SerbianText.Duration(monitored)}, uz zapis svakog uzorka."));

        builder.AppendLine();

        if (upstream.Count > 0)
        {
            var total = upstream.Aggregate(TimeSpan.Zero, (sum, i) => sum + i.DurationReported);
            var longest = upstream[0];

            builder.AppendLine(Wrap(
                $"Tokom tog perioda zabeleženo je {upstream.Count} " +
                $"{Plural(upstream.Count, "prekid", "prekida", "prekida")} usluge u ukupnom trajanju od " +
                $"{SerbianText.Duration(total)}. Najduži pojedinačni prekid trajao je " +
                // No sentence-ending period: the Serbian date format already carries one, and
                // adding a second produced "dana 13.08.2026.." in a letter going to an
                // operator's complaints desk.
                $"{SerbianText.Duration(longest.DurationReported)}, dana " +
                $"{Date(DateOnly.FromDateTime(longest.StartedUtc.LocalDateTime))}"));

            builder.AppendLine();

            builder.AppendLine(Wrap(
                "Tokom svakog od tih prekida moj ruter je uredno odgovarao, a nijedna meta van " +
                "lokalne mreže nije bila dostupna. To isključuje moju opremu i moju lokalnu " +
                "mrežu kao uzrok."));

            builder.AppendLine();
            builder.AppendLine("Najduži prekidi:");
            builder.AppendLine();

            foreach (var incident in upstream.Take(10))
            {
                var start = incident.StartedUtc.LocalDateTime;

                builder.AppendLine(
                    $"  {start.ToString("dd.MM.yyyy. HH:mm:ss", Serbian)}   " +
                    $"{SerbianText.Duration(incident.DurationReported),12}   " +
                    $"(najmanje {SerbianText.Duration(incident.DurationMin)})");
            }

            if (upstream.Count > 10)
            {
                builder.AppendLine($"  … i još {upstream.Count - 10} u priloženoj tabeli.");
            }

            builder.AppendLine();
        }

        builder.AppendLine(Wrap(
            $"Izmerena dostupnost usluge u nadziranom periodu iznosi " +
            $"{SerbianText.Percent(session.UpstreamAvailabilityPercent)}."));

        builder.AppendLine();

        if (complaint.ContractedDownloadMbps is { } contracted)
        {
            builder.AppendLine(Wrap(
                $"Ugovorena brzina preuzimanja je {contracted.ToString("0.##", Serbian)} Mbit/s."));

            builder.AppendLine();
        }
    }

    private static void AppendRequest(StringBuilder builder, SessionSnapshot session)
    {
        builder.AppendLine("ZAHTEV");
        builder.AppendLine();
        builder.AppendLine(Wrap(
            "Tražim da utvrdite uzrok navedenih prekida i otklonite ga, kao i da mi dostavite " +
            "pisani odgovor na ovaj prigovor u zakonskom roku."));

        builder.AppendLine();
        builder.AppendLine(Wrap(
            "Tražim i srazmerno umanjenje mesečne naknade za period u kome usluga nije bila " +
            "dostupna, u skladu sa ugovorom."));

        builder.AppendLine();
        builder.AppendLine(Wrap(
            "Molim da mi potvrdite prijem ovog prigovora i dodelite broj predmeta."));

        builder.AppendLine();
    }

    private static void AppendAttachments(StringBuilder builder, SessionSnapshot session)
    {
        builder.AppendLine("PRILOZI");
        builder.AppendLine();

        // The PDF is listed first because it is the one an operator's complaint desk will
        // actually open and file. The HTML carries exactly the same report and is named as
        // such, so nobody wonders whether two different documents are being submitted.
        var number = 0;

        Attachment("Izveštaj o nadzoru veze (Izvestaj.pdf)");
        Attachment("Isti izveštaj za pregled na ekranu (Izvestaj.html)");
        Attachment("Tabela prekida (Prekidi.csv)");
        Attachment("Tabela merenja (Merenja.csv)");
        Attachment("Sirova evidencija sa lancem otisaka (SirovaEvidencija.jsonl)");
        Attachment("Potvrda integriteta evidencije (Provera-lanca.txt)");

        if (session.Gaps.Count > 0)
        {
            Attachment("Tabela pauza nadzora (Pauze-nadzora.csv)");
        }

        // Numbered as they are written rather than by hand: the last entry is conditional,
        // and a hand-written number on it goes wrong the moment anything above it moves.
        void Attachment(string text) => builder.AppendLine($"  {++number}. {text}");

        builder.AppendLine();
        builder.AppendLine(Wrap(
            "Sirova evidencija je zaštićena lancem kriptografskih otisaka: svaki zapis sadrži " +
            "otisak prethodnog, pa naknadna izmena bilo kog reda narušava sve otiske posle " +
            "njega. Ispravnost lanca može proveriti bilo ko, nezavisno od mene."));

        builder.AppendLine();
    }

    /// <summary>
    /// Serbian has three plural forms; getting this wrong makes a letter read as machine
    /// output, and a letter that reads as machine output gets treated as one.
    /// </summary>
    private static string Plural(int count, string one, string few, string many) =>
        SessionVerdict.Plural(count, one, few, many);

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy.", Serbian);

    /// <summary>Wraps to a width that survives being pasted into a form or an email.</summary>
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
