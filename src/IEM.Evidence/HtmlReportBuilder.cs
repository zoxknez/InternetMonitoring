using System.Globalization;
using System.Net;
using System.Text;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Speed;
using IEM.Storage;

namespace IEM.Evidence;

/// <summary>
/// Builds the human-readable report.
/// <para>
/// Entirely self-contained: styles and charts are inline, with no fonts, scripts or
/// images fetched from anywhere. A report that needs the internet to render is a poor
/// joke in a document about the internet being down, and it also has to survive being
/// emailed to an operator and opened on a machine that blocks remote content.
/// </para>
/// </summary>
public static class HtmlReportBuilder
{
    private const int ChartWidth = 960;
    private const int ChartHeight = 220;
    private const int TimelineHeight = 44;

    public static void Write(
        string path,
        SessionSnapshot session,
        string? chainHeadHash,
        bool chainValid,
        SpeedMeasurementNote? speed = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var html = Build(session, chainHeadHash, chainValid, speed);
        File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Build(SessionSnapshot session, string? chainHeadHash, bool chainValid, SpeedMeasurementNote? speed)
    {
        var builder = new StringBuilder(32_000);

        builder.Append(
            $"""
            <!doctype html>
            <html lang="sr-Latn">
            <head>
            <meta charset="utf-8">
            <title>Evidencija internet veze - {Escape(session.SessionId)}</title>
            <style>{Styles}</style>
            </head>
            <body>
            <main>
            <h1>Evidencija kvaliteta internet veze</h1>
            <p class="sub">Sesija {Escape(session.SessionId)} &middot; {Escape(session.Machine)}</p>
            """);

        AppendVerdict(builder, session);
        AppendFacts(builder, session);
        AppendStats(builder, session);
        AppendSpeed(builder, speed);
        AppendTimeline(builder, session);
        AppendLatencyChart(builder, session);
        AppendIncidents(builder, session);
        AppendTraces(builder, session);
        AppendGaps(builder, session);
        AppendIntegrity(builder, session, chainHeadHash, chainValid);
        AppendLimitations(builder, speed);

        builder.Append("</main></body></html>");
        return builder.ToString();
    }

    /// <summary>
    /// The banner a non-technical reader looks at first: is there a case, and against whom.
    /// </summary>
    private static void AppendVerdict(StringBuilder builder, SessionSnapshot session)
    {
        // Shared with the window and the console, so the report can never reach a
        // different conclusion than the screen the reader was just looking at.
        var verdict = SessionVerdict.Evaluate(
            session.MonitoredTime,
            session.UpstreamIncidents.Count(),
            session.LocalDowntime);

        var cls = verdict.Kind switch
        {
            VerdictKind.UpstreamFault => "bad",
            VerdictKind.LocalFault => "warn",
            VerdictKind.TooShort => "warn",
            _ => "good",
        };

        builder.Append(
            $"""
            <section class="verdict {cls}">
              <h2>{Escape(verdict.Headline)}</h2>
              <p>{Escape(verdict.Detail)}</p>
            </section>
            """);
    }

    private static void AppendFacts(StringBuilder builder, SessionSnapshot session)
    {
        var medium = session.Medium switch
        {
            LinkMedium.Ethernet => "žičana veza (Ethernet)",
            LinkMedium.Wireless => "bežična veza (Wi-Fi)",
            _ => "nepoznat tip veze",
        };

        var speed = session.LinkSpeedBitsPerSecond is { } bps
            ? $"{(bps / 1_000_000d).ToString("N0", SerbianText.Culture)} Mbit/s"
            : "nepoznato";

        builder.Append(
            $"""
            <table class="facts">
              <tr><th>Period</th><td>{Escape(SerbianText.DateTime(session.StartedUtc))} -
                  {Escape(session.EndedUtc is { } e ? SerbianText.DateTime(e) : "nije završeno")}</td></tr>
              <tr><th>Adapter</th><td>{Escape(session.InterfaceName)} - {medium}</td></tr>
              <tr><th>Brzina veze adaptera</th><td>{speed}</td></tr>
              <tr><th>Ruter</th><td>{Escape(session.Gateway ?? "nepoznat")}</td></tr>
              <tr><th>Broj uzoraka</th><td>{session.SampleCount.ToString("N0", SerbianText.Culture)}</td></tr>
            </table>
            """);

        if (session.Medium == LinkMedium.Wireless)
        {
            builder.Append(
                """
                <p class="note">Nadzor je vršen preko bežične veze. Evidencija prekida je punovažna,
                ali se ugovorena brzina po propisu ne može dokazivati bežično - za to je potrebno
                merenje preko Ethernet kabla povezanog direktno na modem.</p>
                """);
        }
    }

    private static void AppendStats(StringBuilder builder, SessionSnapshot session)
    {
        builder.Append("<div class=\"cards\">");

        Card("Dostupnost", SerbianText.Percent(session.AvailabilityPercent).Replace(" ", "&nbsp;", StringComparison.Ordinal),
            "u odnosu na stvarno nadzirano vreme");
        Card("Dostupnost bez lokalnih kvarova",
            SerbianText.Percent(session.UpstreamAvailabilityPercent).Replace(" ", "&nbsp;", StringComparison.Ordinal),
            "računa se samo ono što nije vaša oprema");
        Card("Prekida ukupno", session.Incidents.Count.ToString(CultureInfo.InvariantCulture),
            $"od toga {session.UpstreamIncidents.Count()} izolovano iza rutera");
        Card("Nedostupnost iza rutera", SerbianText.Duration(session.UpstreamDowntime),
            $"najduži {SerbianText.Duration(session.LongestUpstreamOutage)}");
        Card("Lokalna nedostupnost", SerbianText.Duration(session.LocalDowntime),
            "vaš računar, Wi-Fi ili ruter");
        Card("Nenadzirano", SerbianText.Duration(session.GapTime),
            "spavanje ili restart - ne računa se nikako");

        builder.Append("</div>");

        void Card(string label, string value, string hint) => builder.Append(
            $"""
            <div class="card">
              <div class="label">{label}</div>
              <div class="value">{value}</div>
              <div class="hint">{hint}</div>
            </div>
            """);
    }

    /// <summary>
    /// The outage strip. One column per time bucket, so a two-day session fits on a page
    /// and the pattern of failures - clustered, periodic, or scattered - is visible at a
    /// glance in a way no table conveys.
    /// </summary>
    private static void AppendTimeline(StringBuilder builder, SessionSnapshot session)
    {
        if (session.Latency.Count == 0)
        {
            return;
        }

        builder.Append("<h2>Vremenska traka</h2>");
        builder.Append(
            $"<svg class=\"chart\" viewBox=\"0 0 {ChartWidth} {TimelineHeight}\" " +
            $"role=\"img\" aria-label=\"Traka prekida kroz vreme\">");

        var columnWidth = (double)ChartWidth / session.Latency.Count;

        for (var i = 0; i < session.Latency.Count; i++)
        {
            var bucket = session.Latency[i];
            var fill = bucket.Outage ? "#c0392b" : bucket.Degraded ? "#e0a800" : "#2e9e5b";
            var x = (i * columnWidth).ToString("F2", CultureInfo.InvariantCulture);
            var w = Math.Max(columnWidth, 0.6).ToString("F2", CultureInfo.InvariantCulture);

            builder.Append(
                $"<rect x=\"{x}\" y=\"0\" width=\"{w}\" height=\"{TimelineHeight}\" fill=\"{fill}\"/>");
        }

        builder.Append("</svg>");
        builder.Append(
            """
            <p class="legend">
              <span class="swatch good"></span> ispravno
              <span class="swatch warn"></span> pogoršano
              <span class="swatch bad"></span> prekid
            </p>
            """);
    }

    /// <summary>
    /// Latency as a band between the fastest and slowest response in each bucket, with the
    /// mean drawn through it. Plotting only the mean would smooth away exactly the spikes
    /// that make a connection unusable.
    /// </summary>
    private static void AppendLatencyChart(StringBuilder builder, SessionSnapshot session)
    {
        // Carry the original bucket index so each point keeps its true position on the
        // time axis; buckets where nothing answered are skipped, not shifted over.
        var points = session.Latency
            .Select((bucket, index) => (Bucket: bucket, Index: index))
            .Where(p => p.Bucket.AverageRtt is not null)
            .ToList();

        if (points.Count < 2)
        {
            return;
        }

        var peak = points.Max(p => p.Bucket.MaxRtt ?? 0d);
        var scaleMax = Math.Max(20d, Math.Ceiling(peak / 20d) * 20d);
        var columnWidth = (double)ChartWidth / session.Latency.Count;

        builder.Append("<h2>Kašnjenje</h2>");
        builder.Append(
            $"<svg class=\"chart\" viewBox=\"0 0 {ChartWidth} {ChartHeight}\" " +
            "role=\"img\" aria-label=\"Grafikon kašnjenja kroz vreme\">");

        // Gridlines with values, so the shape can actually be read off the picture.
        for (var step = 0; step <= 4; step++)
        {
            var value = scaleMax * step / 4d;
            var y = Y(value);
            builder.Append(
                $"<line x1=\"0\" y1=\"{F(y)}\" x2=\"{ChartWidth}\" y2=\"{F(y)}\" stroke=\"#e3e3e6\" stroke-width=\"1\"/>");
            builder.Append(
                $"<text x=\"4\" y=\"{F(y - 4)}\" font-size=\"11\" fill=\"#8a8a8f\">{value:F0} ms</text>");
        }

        var upper = new StringBuilder();
        var lower = new StringBuilder();
        var mean = new StringBuilder();

        foreach (var (bucket, index) in points)
        {
            var x = (index * columnWidth) + (columnWidth / 2);

            upper.Append(CultureInfo.InvariantCulture, $"{F(x)},{F(Y(bucket.MaxRtt ?? 0))} ");

            // The lower edge is built in reverse so the two edges close into one polygon.
            lower.Insert(0, $"{F(x)},{F(Y(bucket.MinRtt ?? 0))} ");
            mean.Append(CultureInfo.InvariantCulture, $"{F(x)},{F(Y(bucket.AverageRtt ?? 0))} ");
        }

        builder.Append(
            $"<polygon points=\"{upper}{lower}\" fill=\"#4a90d9\" fill-opacity=\"0.22\"/>");
        builder.Append(
            $"<polyline points=\"{mean}\" fill=\"none\" stroke=\"#1f6fb8\" stroke-width=\"1.6\"/>");
        builder.Append("</svg>");
        builder.Append(
            "<p class=\"legend\">Osenčeno je raspon od najbržeg do najsporijeg odziva, " +
            "linija je prosek.</p>");

        double Y(double value) => ChartHeight - 12 - ((value / scaleMax) * (ChartHeight - 24));
    }

    private static void AppendIncidents(StringBuilder builder, SessionSnapshot session)
    {
        builder.Append("<h2>Prekidi</h2>");

        if (session.Incidents.Count == 0)
        {
            builder.Append("<p class=\"note\">Nije zabeležen nijedan prekid.</p>");
            return;
        }

        // Events that a pause split into several segments, so each part can say so plainly
        // instead of appearing as two unrelated short outages.
        var segmented = session.Incidents
            .GroupBy(i => i.CorrelationId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        builder.Append(
            """
            <table class="data">
            <thead><tr>
              <th class="num">#</th><th>Početak</th><th class="num">Trajanje</th><th class="num">Raspon</th>
              <th>Stanje</th><th>Problem izolovan na</th><th class="num">Pouzdanost</th>
            </tr></thead><tbody>
            """);

        foreach (var incident in session.Incidents)
        {
            var range =
                $"{SerbianText.Duration(incident.DurationMin)} - {SerbianText.Duration(incident.DurationMax)}";

            var flags = new List<string>();
            if (incident.IsOpen)
            {
                flags.Add("nezavršen na kraju testa");
            }

            if (incident.EndedByGap)
            {
                flags.Add("nadzor je prestao tokom prekida; mereno je samo do tog trenutka");
            }

            if (incident.StartedAfterGap)
            {
                flags.Add("nastavak istog događaja posle pauze nadzora");
            }

            if (incident.RouteChanged)
            {
                flags.Add(
                    "saobraćaj je tokom prekida promenio mrežni adapter, " +
                    "pa ovaj zapis nije čist dokaz o jednoj vezi");
            }

            if (segmented.Contains(incident.CorrelationId))
            {
                flags.Add("deo događaja koji je pauza nadzora presekla na više segmenata");
            }

            // The figure, not just the fact. "The line was busy" invites the question "how
            // busy", and the answer separates a stream in another room from a download that
            // was using the whole connection.
            if (incident.PeakLocalTrafficBytesPerSecond is { } localPeak &&
                localPeak >= ProbeCycle.HeavyLocalTrafficBytesPerSecond)
            {
                flags.Add(
                    $"tokom prekida je i sam računar koristio vezu (do {SerbianText.Rate(localPeak)}), " +
                    "pa se ovaj prekid ne može bez rezerve pripisati samoj vezi");
            }

            var cls = incident.Attribution == FaultAttribution.Upstream ? " class=\"upstream\"" : string.Empty;

            builder.Append(
                $"""
                <tr{cls}>
                  <td class="num">{incident.Number}</td>
                  <td>{Escape(SerbianText.DateTime(incident.StartedUtc))}</td>
                  <td class="num"><strong>{Escape(SerbianText.Duration(incident.DurationReported))}</strong></td>
                  <td class="num muted">{range}</td>
                  <td>{Escape(incident.WorstState.Label())}</td>
                  <td>{Escape(incident.WorstState.DomainOf(session.Medium).Label())}</td>
                  <td class="num">{Escape(incident.Band?.Label() ?? "-")}</td>
                </tr>
                """);

            if (flags.Count > 0)
            {
                builder.Append(
                    $"<tr class=\"flag\"><td></td><td colspan=\"6\">{Escape(string.Join("; ", flags))}</td></tr>");
            }
        }

        builder.Append("</tbody></table>");
        builder.Append(
            """
            <p class="note"><strong>Kako se čita trajanje.</strong> Merenje je diskretno, pa se tačan
            početak prekida nalazi između poslednjeg ispravnog i prvog neispravnog uzorka. Kolona
            <em>Trajanje</em> je središnja procena, a <em>Raspon</em> pokazuje najmanje i najviše moguće
            trajanje. Donja granica se ne može osporiti.</p>
            """);

        // A band, never a percentage. Ninety-four percent looks like a probability and is
        // not one; printing it invites an argument about the second decimal instead of
        // about the evidence.
        builder.Append(
            """
            <p class="note"><strong>Kako se čita pouzdanost.</strong> Izražena je kao pojas, ne kao
            procenat, i zavisi od dve stvari: koliko provera ide u prilog zaključku i koliko je
            provera uopšte moglo da se izvede. Malo provera sa savršenim rezultatom ne daje visoku
            ocenu - provere koje nisu mogle da se izvedu ograničavaju koliko visoko ocena sme da ide.</p>
            """);
    }

    /// <summary>
    /// The traces taken at incident boundaries.
    /// <para>
    /// The only evidence here that says <em>where</em> the connection stopped rather than
    /// merely that it stopped, which is why it belongs in the report and not only in the raw
    /// log. Presented with its asymmetry stated plainly: a hop that answered is proof the
    /// packets reached it, while silence past that point is not proof of anything about the
    /// next device - routers are widely configured not to answer expiring packets, and
    /// reading that as a located fault would put an accusation about a specific machine into
    /// a complaint on the strength of a configuration choice.
    /// </para>
    /// </summary>
    private static void AppendTraces(StringBuilder builder, SessionSnapshot session)
    {
        // Only those taken during an outage. A trace from after service returned shows a
        // healthy path and would read as though it contradicted the incident beside it.
        var traces = session.Traces.Where(t => t.DuringOutage).ToList();

        if (traces.Count == 0)
        {
            return;
        }

        builder.Append("<h2>Trase putanje tokom prekida</h2>");

        foreach (var trace in traces)
        {
            builder.Append(
                $"""
                <h3>Prekid #{trace.IncidentNumber} - {Escape(SerbianText.DateTime(trace.TakenUtc))}</h3>
                <p class="note">Meta: {Escape(trace.Target)}. {Escape(trace.Interpretation)}</p>
                """);

            if (trace.Hops.Count == 0)
            {
                continue;
            }

            builder.Append(
                """
                <table class="data">
                <thead><tr><th class="num">Skok</th><th>Adresa</th><th class="num">Odziv</th><th>Gde</th></tr></thead><tbody>
                """);

            foreach (var hop in trace.Hops)
            {
                var where = !hop.Answered
                    ? "nema odgovora"
                    : hop.IsPrivate ? "vaša mreža" : "izvan vaše mreže";

                var address = hop.Address ?? "-";
                var rtt = hop.RoundTrip is { } value ? SerbianText.Duration(value) : "-";
                var cls = hop.Answered ? string.Empty : " class=\"muted\"";

                builder.Append(
                    $"<tr{cls}><td class=\"num\">{hop.Ttl}</td><td>{Escape(address)}</td>" +
                    $"<td class=\"num\">{Escape(rtt)}</td><td>{where}</td></tr>");
            }

            builder.Append("</tbody></table>");
        }

        builder.Append(
            """
            <p class="note"><strong>Kako se čita trasa.</strong> Skok koji je odgovorio dokazuje da su
            paketi stigli do njega - to je čvrst nalaz. Izostanak odgovora posle nekog skoka
            <em>ne</em> dokazuje kvar na sledećem uređaju: veliki broj rutera na internetu je
            namerno podešen da ne odgovara na ovu vrstu provere. Nalaz koji jeste jak je kada
            nijedan uređaj izvan vaše mreže nije odgovorio, a ruter jeste - tada paket nije
            ni izašao iz kuće.</p>
            """);
    }

    private static void AppendGaps(StringBuilder builder, SessionSnapshot session)
    {
        if (session.Gaps.Count == 0)
        {
            return;
        }

        builder.Append("<h2>Pauze nadzora</h2>");
        builder.Append(
            "<table class=\"data\"><thead><tr><th>Vreme</th>" +
            "<th class=\"num\">Trajanje</th><th>Uzrok</th></tr></thead><tbody>");

        foreach (var gap in session.Gaps)
        {
            var cause = SerbianText.GapCauseLabel(gap.Cause);

            builder.Append(
                $"<tr><td>{Escape(SerbianText.DateTime(gap.DetectedUtc))}</td>" +
                $"<td class=\"num\">{Escape(SerbianText.Duration(gap.Duration))}</td><td>{cause}</td></tr>");
        }

        builder.Append("</tbody></table>");
        builder.Append(
            """
            <p class="note">Tokom ovih perioda ništa nije mereno, pa se ne računaju ni kao prekid ni
            kao ispravan rad. Da su računati kao ispravan rad, dostupnost bi bila veća nego što je
            stvarno izmerena.</p>
            """);
    }

    private static void AppendIntegrity(
        StringBuilder builder,
        SessionSnapshot session,
        string? headHash,
        bool valid)
    {
        builder.Append("<h2>Integritet zapisa</h2>");

        builder.Append(valid
            ? $"<p class=\"ok\">{Escape(ChainText.Consistent)}</p>"
            : $"<p class=\"bad-text\">{Escape(ChainText.Broken)}</p>");

        // Beside the finding, not a page away. A reader who takes the first sentence for
        // proof of origin has been misled by the layout even when the caveat exists somewhere.
        builder.Append($"<p class=\"note\">{Escape(ChainText.NotProofOfOrigin)}</p>");

        if (headHash is not null)
        {
            builder.Append($"<p class=\"hash\">Završni otisak: {Escape(headHash)}</p>");
        }

        // The measurements mean the same thing forever; the rules that interpret them do
        // not. Without these, a figure quoted from an old report cannot be reproduced and
        // nobody can tell whether that is a discrepancy or a changed algorithm.
        // Taken from the session, not from this build. A report rebuilt years later by a
        // newer version has to state the reasoning that produced the conclusions in it.
        builder.Append(
            $"""
            <p class="note">Zaključci u ovom izveštaju izvedeni su sledećim verzijama modela:
            format zapisa {session.SchemaVersion ?? EvidenceModelVersion.SchemaVersion},
            klasifikacija {Escape(session.ClassifierVersion ?? EvidenceModelVersion.ClassifierVersion)},
            model pripisivanja {Escape(session.AttributionModelVersion ?? EvidenceModelVersion.AttributionModelVersion)},
            model pouzdanosti {Escape(session.ConfidenceModelVersion ?? EvidenceModelVersion.ConfidenceModelVersion)}.
            Sirova merenja se ne menjaju, ali pravila koja ih tumače mogu, pa je iz izveštaja
            uvek moguće utvrditi kojom logikom je zaključak donet.</p>
            """);

        // Which build produced this. Without it, two people running "the same version" can
        // get different results from a dependency that moved underneath them, and neither
        // can say which build produced a given figure.
        builder.Append(
            $"""
            <p class="note">Izveštaj je napravio {Escape(BuildInfo.Product)} verzija
            {Escape(BuildInfo.Version)}{(BuildInfo.DependenciesLocked ? ", sa unapred utvrđenim verzijama svih zavisnosti" : string.Empty)}.
            Ista verzija programa nad istom sirovom evidencijom uvek daje isti izveštaj.</p>
            """);

        builder.Append(
            """
            <p class="note">Svaki zapis u sirovoj evidenciji sadrži otisak prethodnog, pa izmena bilo kog
            ranijeg reda narušava sve otiske posle njega. Proveru može ponoviti bilo ko, uključujući
            tehničara operatera.</p>
            """);
    }

    /// <summary>
    /// The speed measurement taken beside the session, if one was.
    /// <para>
    /// Stated with its verdict rather than as a bare number, because the conditions are what
    /// make the figure usable or useless in a complaint - and a report that printed "45
    /// Mbit/s" without saying the link was measured over Wi-Fi would be doing its reader a
    /// disservice no amount of decimal places can repair.
    /// </para>
    /// </summary>
    private static void AppendSpeed(StringBuilder builder, SpeedMeasurementNote? speed)
    {
        if (speed is null)
        {
            return;
        }

        var medium = speed.Medium switch
        {
            LinkMedium.Ethernet => "Ethernet kabl",
            LinkMedium.Wireless => "Wi-Fi (ne važi za dokazivanje brzine)",
            _ => "nepoznat tip veze",
        };

        var assessment = speed.Assess();

        var contracted = speed.ContractedMbps is { } c
            ? $"{c.ToString("0.##", SerbianText.Culture)} Mbit/s"
            : "nije navedeno";

        var contractedUpload = speed.ContractedUploadMbps is { } cu
            ? $"{cu.ToString("0.##", SerbianText.Culture)} Mbit/s"
            : "nije navedeno";

        builder.Append(
            $"""
            <h2>Merenje brzine</h2>
            <table class="facts">
              <tr><th>Datum merenja</th><td>{Escape(SerbianText.DateTime(speed.MeasuredAtUtc))}</td></tr>
              <tr><th>Preuzimanje - izmereno</th><td>{speed.DownloadMbps.ToString("0.##", SerbianText.Culture)} Mbit/s</td></tr>
              <tr><th>Preuzimanje - ugovoreno</th><td>{Escape(contracted)}</td></tr>
              <tr><th>Ocena preuzimanja</th><td>{Escape(assessment.BandLabel ?? "nema ugovorene brzine za poređenje")}</td></tr>
            </table>
            """);

        // The sending half is stated only when it ran. A row reading "0 Mbit/s" for a
        // measurement that never sent anything would be a finding this tool did not make.
        if (speed.UploadMbps is { } upload)
        {
            builder.Append(
                $"""
                <table class="facts">
                  <tr><th>Slanje - izmereno</th><td>{upload.ToString("0.##", SerbianText.Culture)} Mbit/s</td></tr>
                  <tr><th>Slanje - ugovoreno</th><td>{Escape(contractedUpload)}</td></tr>
                  <tr><th>Ocena slanja</th><td>{Escape(assessment.UploadBandLabel ?? "nema ugovorene brzine slanja za poređenje")}</td></tr>
                </table>
                """);
        }

        var transferred = speed.BytesTransferred + speed.UploadBytesTransferred;

        builder.Append(
            $"""
            <table class="facts">
              <tr><th>Preneseno</th><td>{(transferred / 1_000_000d).ToString("0.#", SerbianText.Culture)} MB za {SerbianText.Duration(speed.Duration)} po smeru</td></tr>
              <tr><th>Način merenja</th><td>{medium}, tri paralelne veze po smeru, veza mirna pre početka</td></tr>
              <tr><th>Putanja merenja</th><td>{Escape(speed.RouteState.Label())}</td></tr>
            </table>
            """);

        AppendLoadedLatency(builder, speed);

        // Asked of the finding rather than read out of it: a file written before 2.7.1 carries
        // a verdict reached under rules that have since been corrected.
        if (assessment.State == SpeedAssessmentState.MeetsConditions)
        {
            builder.Append(
                """
                <p class="note">Merenje je izvršeno pod uslovima koje propis traži: preko kabla, na mirnoj vezi,
                sa zapisom uslova. Ispunjava uslove za korišćenje uz prigovor, mada zvanično merenje
                za postupak i dalje treba obaviti RATEL NetTest aplikacijom po propisanoj proceduri.</p>
                """);
        }
        else
        {
            builder.Append($"<p class=\"note\">{Escape(assessment.State.Label())}</p>");

            if (assessment.Reason is { } reason)
            {
                builder.Append($"<p class=\"note\">{Escape(reason)}</p>");
            }

            if (assessment.Defects.Count > 0)
            {
                builder.Append("<ul class=\"limits\">");

                foreach (var defect in assessment.Defects)
                {
                    builder.Append($"<li>{Escape(defect)}</li>");
                }

                builder.Append("</ul>");
            }
        }

        builder.Append($"<p class=\"note\">{Escape(SpeedText.SaturationNote)}</p>");
    }

    /// <summary>
    /// What the connection did to everything else while it was busy.
    /// <para>
    /// Reported separately from the speed, and it is a separate complaint: a line that
    /// delivers its contracted rate can still be useless for calls and games because a
    /// buffer somewhere fills under load and every packet behind it waits. This is the one
    /// quantity the regulators' own tools measure that a plain download test cannot.
    /// </para>
    /// </summary>
    private static void AppendLoadedLatency(StringBuilder builder, SpeedMeasurementNote speed)
    {
        if (speed.IdleLatencyMs is not { } idle || speed.LatencyIncreaseMs is not { } increase)
        {
            return;
        }

        string Ms(double? value) => value is { } number
            ? $"{number.ToString("0.#", SerbianText.Culture)} ms"
            : "nije mereno";

        builder.Append(
            $"""
            <h3>Kašnjenje pod opterećenjem</h3>
            <table class="facts">
              <tr><th>Odziv dok je veza mirna</th><td>{Escape(Ms(idle))}</td></tr>
              <tr><th>Odziv tokom preuzimanja</th><td>{Escape(Ms(speed.LatencyUnderDownloadMs))}</td></tr>
              <tr><th>Odziv tokom slanja</th><td>{Escape(Ms(speed.LatencyUnderUploadMs))}</td></tr>
              <tr><th>Povećanje pod opterećenjem</th><td>{Escape(Ms(increase))}</td></tr>
              <tr><th>Ocena</th><td>{Escape(speed.LoadedLatencyLabel ?? "-")}</td></tr>
            </table>
            <p class="note">{Escape(LoadedLatency.Grade(TimeSpan.FromMilliseconds(increase)).Explain())}</p>
            <p class="note">{Escape(SpeedText.LoadedLatencyNote)}</p>
            """);
    }

    private static void AppendLimitations(StringBuilder builder, SpeedMeasurementNote? speed = null)
    {
        // The speed item says one thing when no measurement is attached and another when a
        // valid one is: "this document does not prove the contracted rate" is simply not
        // true of a report that carries a properly measured figure beside it.
        var speedItem = speed?.Assess().State == SpeedAssessmentState.MeetsConditions
            ? "Priloženo merenje brzine ispunjava propisane uslove, ali je jedno merenje i ne " +
              "zamenjuje postupak propisan propisom: merenje RATEL NetTest aplikacijom, na " +
              "Ethernetskom portu modema, po proceduri od tri dana."
            : "Ugovorena brzina se ovim dokumentom ne dokazuje. Po propisu je za to potrebno " +
              "merenje preko Ethernet kabla povezanog direktno na modem.";

        builder.Append(
            $"""
            <h2>Granice ovog dokumenta</h2>
            <ul class="limits">
              <li>Ovo je tehnička evidencija prekida, a ne merenje ovlašćene treće strane niti zapis
                  potpisan od strane operatera.</li>
              <li>{Escape(ChainText.NotProofOfOrigin)}</li>
              <li>{Escape(speedItem)}</li>
              <li>Uz prigovor zbog kvaliteta internet usluge RATEL navodi i rezultate merenja aplikacijom
                  RATEL NetTest, po proceduri od tri dana sa po dva merenja pre i posle podne.</li>
            </ul>
            """);
    }

    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private const string Styles =
        """
        :root { color-scheme: light; }
        * { box-sizing: border-box; }
        body { margin: 0; background: #f6f6f8; color: #1d1d1f;
               font: 15px/1.55 "Segoe UI", system-ui, sans-serif; }
        main { max-width: 1040px; margin: 0 auto; padding: 32px 24px 64px; background: #fff; }
        h1 { font-size: 26px; margin: 0 0 4px; }
        h2 { font-size: 18px; margin: 36px 0 12px; padding-bottom: 6px; border-bottom: 2px solid #ececef; }
        /* Below its section rather than above it. Left to the browser's default a subheading
           came out larger than the h2 it sits under, which reads as the wrong level. */
        h3 { font-size: 15px; margin: 26px 0 8px; }
        .sub { color: #6c6c72; margin: 0 0 24px; }
        .verdict { border-radius: 10px; padding: 18px 20px; margin: 0 0 28px; border-left: 6px solid; }
        .verdict h2 { margin: 0 0 6px; border: 0; padding: 0; font-size: 17px; }
        .verdict p { margin: 0; }
        .verdict.good { background: #eaf7ef; border-color: #2e9e5b; }
        .verdict.warn { background: #fdf5e2; border-color: #e0a800; }
        .verdict.bad  { background: #fdeceb; border-color: #c0392b; }
        /* Label at one edge of the row, value at the other. Set left behind a fixed indent,
           the value left a channel of blank page running down the whole block - the widest
           thing in it being an IP address. */
        table.facts { border-collapse: collapse; margin: 0 0 24px; width: 100%;
                      border-top: 1px solid #e3e3e6; }
        table.facts th { text-align: left; font-weight: 400; color: #6c6c72; padding: 7px 16px 7px 0;
                         white-space: nowrap; vertical-align: top; width: 220px;
                         border-bottom: 1px solid #f0f0f2; }
        table.facts td { padding: 7px 0; text-align: right; border-bottom: 1px solid #f0f0f2; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: 12px; }
        .card { border: 1px solid #e3e3e6; border-radius: 10px; padding: 14px 16px; }
        .card .label { font-size: 12px; color: #6c6c72; }
        .card .value { font-size: 22px; font-weight: 700; margin: 4px 0 2px; text-align: right; }
        .card .hint { font-size: 12px; color: #8a8a8f; }
        svg.chart { width: 100%; height: auto; border: 1px solid #e3e3e6; border-radius: 8px;
                    background: #fff; display: block; }
        .legend { font-size: 12px; color: #6c6c72; margin: 8px 0 0; }
        .swatch { display: inline-block; width: 11px; height: 11px; border-radius: 2px;
                  margin: 0 4px 0 14px; vertical-align: -1px; }
        .swatch.good { background: #2e9e5b; } .swatch.warn { background: #e0a800; }
        .swatch.bad { background: #c0392b; }
        table.data { width: 100%; border-collapse: collapse; font-size: 14px; }
        table.data th { text-align: left; background: #f4f4f6; padding: 8px 10px;
                        border-bottom: 1px solid #e3e3e6; font-weight: 600; }
        table.data td { padding: 7px 10px; border-bottom: 1px solid #f0f0f2; vertical-align: top; }
        /* Anything a reader compares down a column. Durations set left do not line up on
           their digits, so "7m 19s" and "48,0 s" sit at different depths and the column
           cannot be scanned - which is the only reason to put them in a column. */
        table.data th.num, table.data td.num { text-align: right; white-space: nowrap; }
        table.data tr.upstream td { background: #fffafa; }
        table.data tr.flag td { font-size: 12px; color: #a06000; padding-top: 0; border-bottom: 0; }
        .muted { color: #8a8a8f; }
        .note { font-size: 13px; color: #55555b; background: #f7f7f8; border-left: 3px solid #c9c9cf;
                padding: 10px 14px; margin: 14px 0 0; border-radius: 0 6px 6px 0; }
        .ok { color: #1e7a45; font-weight: 600; }
        .bad-text { color: #a5281c; font-weight: 600; }
        .hash { font-family: Consolas, monospace; font-size: 12px; color: #6c6c72; word-break: break-all; }
        .limits { font-size: 13px; color: #55555b; padding-left: 20px; }
        .limits li { margin-bottom: 6px; }
        @media print {
          body { background: #fff; }
          main { max-width: none; padding: 0; }
          h2 { page-break-after: avoid; }
          table.data tr { page-break-inside: avoid; }
        }
        """;
}
