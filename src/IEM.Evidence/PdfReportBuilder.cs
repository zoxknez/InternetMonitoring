using System.Globalization;
using System.Text;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Speed;
using IEM.Storage;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace IEM.Evidence;

/// <summary>
/// The same report as <see cref="HtmlReportBuilder"/>, as a printable document.
/// <para>
/// It exists because of what happens to the HTML on the way to an operator. Printing a page
/// from a browser bakes in whichever margins, scaling and header that browser was set to,
/// silently drops backgrounds unless a checkbox is ticked, and cuts tables across page
/// boundaries without repeating their headers. Two people sending "the same" report send
/// two different documents, and a complaint desk that receives a table whose header is on
/// the previous page has been handed a reason to put it aside.
/// </para>
/// <para>
/// The wording, the figures and the caveats are the same as the HTML - deliberately, and it
/// is the reason both call into <see cref="SessionVerdict"/> and <see cref="SerbianText"/>
/// rather than phrasing anything themselves. A PDF that reached a different conclusion than
/// the page it was generated beside would discredit both.
/// </para>
/// </summary>
public static class PdfReportBuilder
{
    private const double MarginX = 48;
    private const double MarginTop = 50;
    private const double MarginBottom = 52;

    /// <summary>Padding inside a table cell, left and right.</summary>
    private const double CellPad = 4;

    private const double TimelineHeight = 26;
    private const double ChartHeight = 150;

    public static void Write(
        string path,
        SessionSnapshot session,
        string? chainHeadHash,
        bool chainValid,
        SpeedMeasurementNote? speed = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        EmbeddedFonts.Install();

        using var document = new PdfDocument();
        Describe(document, session);

        using (var layout = new Layout(document))
        {
            AppendTitle(layout, session);
            AppendVerdict(layout, session);
            AppendFacts(layout, session);
            AppendStats(layout, session);
            AppendSpeed(layout, speed);
            AppendTimeline(layout, session);
            AppendLatencyChart(layout, session);
            AppendIncidents(layout, session);
            AppendTraces(layout, session);
            AppendGaps(layout, session);
            AppendIntegrity(layout, session, chainHeadHash, chainValid);
            AppendLimitations(layout, speed);
        }

        AppendFooters(document, session);
        document.Save(path);
    }

    /// <summary>
    /// Document properties, with both timestamps taken from the session rather than the
    /// clock. Stamping the moment of export would mean rebuilding a report from untouched
    /// evidence produced a different file every time - which is exactly the claim the
    /// integrity section makes, contradicted in the file's own metadata.
    /// </summary>
    private static void Describe(PdfDocument document, SessionSnapshot session)
    {
        var stamp = (session.EndedUtc ?? session.StartedUtc).UtcDateTime;

        document.Info.Title = $"Evidencija internet veze - {session.SessionId}";
        document.Info.Author = session.Machine;
        document.Info.Subject = "Evidencija kvaliteta internet veze";
        document.Info.Creator = $"{BuildInfo.Product} {BuildInfo.Version}";
        document.Info.CreationDate = stamp;
        document.Info.ModificationDate = stamp;
    }

    // ---- Sections -----------------------------------------------------------

    private static void AppendTitle(Layout layout, SessionSnapshot session)
    {
        layout.Text("Evidencija kvaliteta internet veze", Fonts.Title, Brushes.Ink, 21);
        layout.Text($"Sesija {session.SessionId} · {session.Machine}", Fonts.Body, Brushes.Muted, 12);
        layout.Y += 10;
    }

    private static void AppendVerdict(Layout layout, SessionSnapshot session)
    {
        // Shared with the window, the console and the HTML, so no two of them can reach a
        // different conclusion about the same session.
        var verdict = SessionVerdict.Evaluate(
            session.MonitoredTime,
            session.UpstreamIncidents.Count(),
            session.LocalDowntime);

        var (fill, edge) = verdict.Kind switch
        {
            VerdictKind.UpstreamFault => (Brushes.BadFill, Brushes.Bad),
            VerdictKind.LocalFault or VerdictKind.TooShort => (Brushes.WarnFill, Brushes.Warn),
            _ => (Brushes.GoodFill, Brushes.Good),
        };

        const double pad = 10;
        var inner = layout.Width - (2 * pad) - 4;

        var headline = layout.Wrap(verdict.Headline, Fonts.SubHeading, inner);
        var detail = layout.Wrap(verdict.Detail, Fonts.Body, inner);
        var height = pad + (headline.Count * 13) + 3 + (detail.Count * 11.5) + pad;

        layout.Ensure(height);

        layout.Gfx.DrawRectangle(fill, layout.Left, layout.Y, layout.Width, height);
        layout.Gfx.DrawRectangle(edge, layout.Left, layout.Y, 4, height);

        var y = layout.Y + pad;
        y = layout.Lines(headline, Fonts.SubHeading, Brushes.Ink, layout.Left + pad + 4, y, inner, 13);
        layout.Lines(detail, Fonts.Body, Brushes.Ink, layout.Left + pad + 4, y + 3, inner, 11.5);

        layout.Y += height + 16;
    }

    private static void AppendFacts(Layout layout, SessionSnapshot session)
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

        var period =
            $"{SerbianText.DateTime(session.StartedUtc)} - " +
            $"{(session.EndedUtc is { } ended ? SerbianText.DateTime(ended) : "nije završeno")}";

        layout.Ensure(28);
        layout.Gfx.DrawLine(Pens.Rule, layout.Left, layout.Y, layout.Right, layout.Y);

        Fact("Period", period);
        Fact("Adapter", $"{session.InterfaceName} - {medium}");
        Fact("Brzina veze adaptera", speed);
        Fact("Ruter", session.Gateway ?? "nepoznat");
        Fact("Broj uzoraka", session.SampleCount.ToString("N0", SerbianText.Culture));

        if (session.Medium == LinkMedium.Wireless)
        {
            layout.Y += 6;
            layout.Note(
                "Nadzor je vršen preko bežične veze. Evidencija prekida je punovažna, ali se " +
                "ugovorena brzina po propisu ne može dokazivati bežično - za to je potrebno " +
                "merenje preko Ethernet kabla povezanog direktno na modem.");
        }

        layout.Y += 14;

        // Label at one edge, value at the other, with a rule under each pair.
        //
        // Previously both sat against the left margin with the value starting at a fixed
        // offset, which left a channel of empty paper running down two thirds of the block
        // - the widest thing in it being "192.168.1.1". Setting the value flush right closes
        // the row and lets the rule carry the eye across it.
        void Fact(string label, string value)
        {
            const double labelWidth = 150;
            var lines = layout.Wrap(value, Fonts.Body, layout.Width - labelWidth);
            var height = (lines.Count * 11.5) + 8;

            layout.Ensure(height);

            layout.Gfx.DrawString(
                label, Fonts.Body, Brushes.Muted,
                new XRect(layout.Left, layout.Y + 4, labelWidth, 11.5), XStringFormats.TopLeft);

            layout.Lines(lines, Fonts.Body, Brushes.Ink, layout.Left + labelWidth, layout.Y + 4,
                layout.Width - labelWidth, 11.5, XStringFormats.TopRight);

            layout.Y += height;
            layout.Gfx.DrawLine(Pens.RowRule, layout.Left, layout.Y, layout.Right, layout.Y);
        }
    }

    private static void AppendStats(Layout layout, SessionSnapshot session)
    {
        var cards = new (string Label, string Value, string Hint)[]
        {
            ("Dostupnost",
                SerbianText.Percent(session.AvailabilityPercent),
                "u odnosu na stvarno nadzirano vreme"),
            ("Dostupnost bez lokalnih kvarova",
                SerbianText.Percent(session.UpstreamAvailabilityPercent),
                "računa se samo ono što nije vaša oprema"),
            ("Prekida ukupno",
                session.Incidents.Count.ToString(CultureInfo.InvariantCulture),
                $"od toga {session.UpstreamIncidents.Count()} kod operatera"),
            ("Nedostupnost operatera",
                SerbianText.Duration(session.UpstreamDowntime),
                $"najduži {SerbianText.Duration(session.LongestUpstreamOutage)}"),
            ("Lokalna nedostupnost",
                SerbianText.Duration(session.LocalDowntime),
                "vaš računar, Wi-Fi ili ruter"),
            ("Nenadzirano",
                SerbianText.Duration(session.GapTime),
                "spavanje ili restart - ne računa se nikako"),
        };

        const double gap = 8;
        const double height = 50;
        var width = (layout.Width - (2 * gap)) / 3;

        for (var row = 0; row < 2; row++)
        {
            layout.Ensure(height);

            for (var column = 0; column < 3; column++)
            {
                var card = cards[(row * 3) + column];
                var x = layout.Left + (column * (width + gap));

                layout.Gfx.DrawRectangle(Pens.Rule, x, layout.Y, width, height);

                // The figure is set against the right edge of its card for the same reason
                // the rows above are: a number set left in a box twice its width leaves the
                // card looking half-drawn.
                layout.Gfx.DrawString(card.Label, Fonts.Tiny, Brushes.Muted,
                    new XRect(x + 9, layout.Y + 6, width - 18, 9), XStringFormats.TopLeft);
                layout.Gfx.DrawString(card.Value, Fonts.CardValue, Brushes.Ink,
                    new XRect(x + 9, layout.Y + 17, width - 18, 17), XStringFormats.TopRight);
                layout.Gfx.DrawString(Fit(layout, card.Hint, Fonts.Tiny, width - 18), Fonts.Tiny, Brushes.Faint,
                    new XRect(x + 9, layout.Y + 36, width - 18, 9), XStringFormats.TopLeft);
            }

            layout.Y += height + gap;
        }

        layout.Y += 4;
    }

    /// <summary>
    /// The speed measurement taken beside the session, if one was - stated with its verdict,
    /// for the same reason the HTML report states it: the conditions are what make the figure
    /// usable or useless, and a bare number hides exactly the part an operator would ask about.
    /// </summary>
    private static void AppendSpeed(Layout layout, SpeedMeasurementNote? speed)
    {
        if (speed is null)
        {
            return;
        }

        layout.Heading("Merenje brzine");

        var medium = speed.Medium switch
        {
            LinkMedium.Ethernet => "Ethernet kabl",
            LinkMedium.Wireless => "Wi-Fi (ne važi za dokazivanje brzine)",
            _ => "nepoznat tip veze",
        };

        var contracted = speed.ContractedMbps is { } c
            ? $"{c.ToString("0.##", SerbianText.Culture)} Mbit/s"
            : "nije navedeno";

        layout.Ensure(28);
        layout.Gfx.DrawLine(Pens.Rule, layout.Left, layout.Y, layout.Right, layout.Y);

        Fact("Datum merenja", SerbianText.DateTime(speed.MeasuredAtUtc));
        Fact("Preuzimanje - izmereno", $"{speed.DownloadMbps.ToString("0.##", SerbianText.Culture)} Mbit/s");
        Fact("Preuzimanje - ugovoreno", contracted);
        Fact("Ocena preuzimanja", speed.BandLabel ?? "nema ugovorene brzine za poređenje");

        // Stated only when that half ran. A row reading "0 Mbit/s" for a measurement that
        // never sent anything would be a finding this tool did not make.
        if (speed.UploadMbps is { } upload)
        {
            Fact("Slanje - izmereno", $"{upload.ToString("0.##", SerbianText.Culture)} Mbit/s");
            Fact(
                "Slanje - ugovoreno",
                speed.ContractedUploadMbps is { } cu
                    ? $"{cu.ToString("0.##", SerbianText.Culture)} Mbit/s"
                    : "nije navedeno");
            Fact("Ocena slanja", speed.UploadBandLabel ?? "nema ugovorene brzine slanja za poređenje");
        }

        var transferred = speed.BytesTransferred + speed.UploadBytesTransferred;

        Fact("Preneseno", $"{(transferred / 1_000_000d).ToString("0.#", SerbianText.Culture)} MB za {SerbianText.Duration(speed.Duration)} po smeru");
        Fact("Način merenja", $"{medium}, tri paralelne veze po smeru, veza mirna pre početka");

        AppendLoadedLatency();

        layout.Y += 6;

        if (speed.ValidForComplaint)
        {
            layout.Note(
                "Merenje je izvršeno pod uslovima koje propis traži: preko kabla, na mirnoj vezi, " +
                "sa zapisom uslova. Ispunjava uslove za korišćenje uz prigovor, mada zvanično " +
                "merenje za postupak i dalje treba obaviti RATEL NetTest aplikacijom po propisanoj proceduri.");
        }
        else
        {
            layout.Note("Merenje NE ispunjava uslove za dokazivanje ugovorene brzine:");

            foreach (var defect in speed.Defects)
            {
                layout.Lines(layout.Wrap($"•  {defect}", Fonts.Body, layout.Width), Fonts.Body, Brushes.Ink,
                    layout.Left, layout.Y, layout.Width, 11.5);
                layout.Y += 11.5;
            }
        }

        layout.Note(SpeedText.SaturationNote);

        layout.Y += 14;

        // What the connection did to everything else while it was busy - a separate
        // complaint from the speed, and the one quantity the regulators' own tools measure
        // that a plain download test cannot produce.
        void AppendLoadedLatency()
        {
            if (speed.IdleLatencyMs is not { } idle || speed.LatencyIncreaseMs is not { } increase)
            {
                return;
            }

            static string Ms(double? value) => value is { } number
                ? $"{number.ToString("0.#", SerbianText.Culture)} ms"
                : "nije mereno";

            Fact("Odziv dok je veza mirna", Ms(idle));
            Fact("Odziv tokom preuzimanja", Ms(speed.LatencyUnderDownloadMs));
            Fact("Odziv tokom slanja", Ms(speed.LatencyUnderUploadMs));
            Fact("Povećanje pod opterećenjem", Ms(increase));
            Fact("Kašnjenje pod opterećenjem", speed.LoadedLatencyLabel ?? "-");

            layout.Y += 6;
            layout.Note(LoadedLatency.Grade(TimeSpan.FromMilliseconds(increase)).Explain());
            layout.Note(SpeedText.LoadedLatencyNote);
        }

        // The same row idiom as the facts table: label left, value right, rule underneath.
        void Fact(string label, string value)
        {
            const double labelWidth = 150;
            var lines = layout.Wrap(value, Fonts.Body, layout.Width - labelWidth);
            var height = (lines.Count * 11.5) + 8;

            layout.Ensure(height);

            layout.Gfx.DrawString(
                label, Fonts.Body, Brushes.Muted,
                new XRect(layout.Left, layout.Y + 4, labelWidth, 11.5), XStringFormats.TopLeft);

            layout.Lines(lines, Fonts.Body, Brushes.Ink, layout.Left + labelWidth, layout.Y + 4,
                layout.Width - labelWidth, 11.5, XStringFormats.TopRight);

            layout.Y += height;
            layout.Gfx.DrawLine(Pens.RowRule, layout.Left, layout.Y, layout.Right, layout.Y);
        }
    }

    /// <summary>
    /// The outage strip: one column per time bucket, so the shape of the failures - clustered,
    /// periodic or scattered - is visible at a glance in a way no table conveys.
    /// </summary>
    private static void AppendTimeline(Layout layout, SessionSnapshot session)
    {
        if (session.Latency.Count == 0)
        {
            return;
        }

        layout.Heading("Vremenska traka");
        layout.Ensure(TimelineHeight + 16);

        var columnWidth = layout.Width / session.Latency.Count;

        for (var i = 0; i < session.Latency.Count; i++)
        {
            var bucket = session.Latency[i];
            var fill = bucket.Outage ? Brushes.Bad : bucket.Degraded ? Brushes.Warn : Brushes.Good;

            layout.Gfx.DrawRectangle(
                fill,
                layout.Left + (i * columnWidth),
                layout.Y,
                // Overdrawn very slightly so neighbouring columns never leave hairline gaps
                // that a reader would take for brief recoveries inside an outage.
                Math.Max(columnWidth + 0.2, 0.4),
                TimelineHeight);
        }

        layout.Gfx.DrawRectangle(Pens.Rule, layout.Left, layout.Y, layout.Width, TimelineHeight);
        layout.Y += TimelineHeight + 5;

        var x = layout.Left;
        Swatch(Brushes.Good, "ispravno");
        Swatch(Brushes.Warn, "pogoršano");
        Swatch(Brushes.Bad, "prekid");

        layout.Y += 11;

        void Swatch(XBrush brush, string label)
        {
            layout.Gfx.DrawRectangle(brush, x, layout.Y + 1.5, 7, 7);
            layout.Gfx.DrawString(label, Fonts.Tiny, Brushes.Muted,
                new XRect(x + 10, layout.Y, 90, 10), XStringFormats.TopLeft);
            x += 12 + layout.Gfx.MeasureString(label, Fonts.Tiny).Width + 14;
        }
    }

    /// <summary>
    /// Latency as a band between the fastest and slowest response in each bucket, with the
    /// mean through it. Plotting the mean alone would smooth away exactly the spikes that
    /// make a connection unusable.
    /// </summary>
    private static void AppendLatencyChart(Layout layout, SessionSnapshot session)
    {
        // The original bucket index is carried so each point keeps its true position on the
        // time axis; buckets where nothing answered are skipped, never shifted over.
        var points = session.Latency
            .Select((bucket, index) => (Bucket: bucket, Index: index))
            .Where(p => p.Bucket.AverageRtt is not null)
            .ToList();

        if (points.Count < 2)
        {
            return;
        }

        layout.Heading("Kašnjenje");
        layout.Ensure(ChartHeight + 16);

        var peak = points.Max(p => p.Bucket.MaxRtt ?? 0d);
        var scaleMax = Math.Max(20d, Math.Ceiling(peak / 20d) * 20d);
        var columnWidth = layout.Width / session.Latency.Count;
        var top = layout.Y;

        layout.Gfx.DrawRectangle(Pens.Rule, layout.Left, top, layout.Width, ChartHeight);

        for (var step = 0; step <= 4; step++)
        {
            var value = scaleMax * step / 4d;
            var y = Y(value);

            layout.Gfx.DrawLine(Pens.Grid, layout.Left, y, layout.Left + layout.Width, y);
            layout.Gfx.DrawString(
                $"{value.ToString("F0", SerbianText.Culture)} ms", Fonts.Tiny, Brushes.Faint,
                new XRect(layout.Left + 3, y - 9, 60, 9), XStringFormats.TopLeft);
        }

        var band = new XPoint[points.Count * 2];

        for (var i = 0; i < points.Count; i++)
        {
            var (bucket, index) = points[i];
            var x = layout.Left + (index * columnWidth) + (columnWidth / 2);

            band[i] = new XPoint(x, Y(bucket.MaxRtt ?? 0));

            // The lower edge is filled in from the far end so the two edges close into one
            // polygon rather than crossing back through the middle of the chart.
            band[^(i + 1)] = new XPoint(x, Y(bucket.MinRtt ?? 0));
        }

        layout.Gfx.DrawPolygon(Brushes.Band, band, XFillMode.Alternate);

        var mean = points
            .Select(p => new XPoint(
                layout.Left + (p.Index * columnWidth) + (columnWidth / 2),
                Y(p.Bucket.AverageRtt ?? 0)))
            .ToArray();

        layout.Gfx.DrawLines(Pens.Mean, mean);

        layout.Y = top + ChartHeight + 5;
        layout.Text(
            "Osenčeno je raspon od najbržeg do najsporijeg odziva, linija je prosek.",
            Fonts.Tiny, Brushes.Muted, 11);

        double Y(double value) => top + ChartHeight - 8 - ((value / scaleMax) * (ChartHeight - 16));
    }

    private static void AppendIncidents(Layout layout, SessionSnapshot session)
    {
        layout.Heading("Prekidi");

        if (session.Incidents.Count == 0)
        {
            layout.Note("Nije zabeležen nijedan prekid.");
            return;
        }

        // Events a monitoring pause split into several segments, so each part can say so
        // plainly instead of reading as two unrelated short outages.
        var segmented = session.Incidents
            .GroupBy(i => i.CorrelationId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var table = new Table(
            layout,
            ["#", "Početak", "Trajanje", "Raspon", "Stanje", "Problem izolovan na", "Pouzdanost"],
            [22, 76, 54, 88, 92, 92, 75],
            [
                XStringFormats.TopRight,   // number
                XStringFormats.TopLeft,    // timestamp
                XStringFormats.TopRight,   // duration
                XStringFormats.TopRight,   // duration range
                XStringFormats.TopLeft,    // state
                XStringFormats.TopLeft,    // domain
                XStringFormats.TopRight,   // confidence band
            ]);

        foreach (var incident in session.Incidents)
        {
            var range =
                $"{SerbianText.Duration(incident.DurationMin)} - {SerbianText.Duration(incident.DurationMax)}";

            table.Row(
                [
                    new Cell(incident.Number.ToString(CultureInfo.InvariantCulture)),
                    new Cell(SerbianText.DateTime(incident.StartedUtc)),
                    new Cell(SerbianText.Duration(incident.DurationReported), Bold: true),
                    new Cell(range, Brush: Brushes.Faint),
                    new Cell(incident.WorstState.Label()),
                    new Cell(incident.WorstState.DomainOf(session.Medium).Label()),
                    new Cell(incident.Band?.Label() ?? "-"),
                ],
                incident.Attribution == FaultAttribution.Upstream ? Brushes.UpstreamRow : null);

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

            // As in the HTML report: the figure, so the reader can recognise their own
            // download rather than being told only that something was running.
            if (incident.PeakLocalTrafficBytesPerSecond is { } localPeak &&
                localPeak >= ProbeCycle.HeavyLocalTrafficBytesPerSecond)
            {
                flags.Add(
                    $"tokom prekida je i sam računar koristio vezu (do {SerbianText.Rate(localPeak)}), " +
                    "pa se ovaj prekid ne može bez rezerve pripisati operateru");
            }

            if (flags.Count > 0)
            {
                table.Flag(string.Join("; ", flags));
            }
        }

        layout.Y += 4;
        layout.Note(
            "Kako se čita trajanje. Merenje je diskretno, pa se tačan početak prekida nalazi " +
            "između poslednjeg ispravnog i prvog neispravnog uzorka. Kolona Trajanje je " +
            "središnja procena, a Raspon pokazuje najmanje i najviše moguće trajanje. Donja " +
            "granica se ne može osporiti.");

        // A band, never a percentage. Ninety-four percent looks like a probability and is
        // not one; printing it invites an argument about the second decimal instead of about
        // the evidence.
        layout.Note(
            "Kako se čita pouzdanost. Izražena je kao pojas, ne kao procenat, i zavisi od dve " +
            "stvari: koliko provera ide u prilog zaključku i koliko je provera uopšte moglo da " +
            "se izvede. Malo provera sa savršenim rezultatom ne daje visoku ocenu - provere " +
            "koje nisu mogle da se izvedu ograničavaju koliko visoko ocena sme da ide.");
    }

    /// <summary>
    /// The traces taken at incident boundaries - the only evidence here that says
    /// <em>where</em> the connection stopped rather than merely that it stopped.
    /// </summary>
    private static void AppendTraces(Layout layout, SessionSnapshot session)
    {
        // Only those taken during an outage. A trace from after service returned shows a
        // healthy path and would read as though it contradicted the incident beside it.
        var traces = session.Traces.Where(t => t.DuringOutage).ToList();

        if (traces.Count == 0)
        {
            return;
        }

        layout.Heading("Trase putanje tokom prekida");

        foreach (var trace in traces)
        {
            var note = $"Meta: {trace.Target}. {trace.Interpretation}";

            // The whole opening of the block is reserved at once - its heading, the sentence
            // stating what the trace supports, the column headers and a first hop. Reserving
            // only the heading left "Prekid #5" and its interpretation stranded at the foot
            // of a page with the hops that justify them overleaf, which is precisely the
            // split this report is meant not to have.
            layout.Ensure(6 + 13 + layout.NoteHeight(note) + 6 + 32);

            layout.Y += 6;
            layout.Text(
                $"Prekid #{trace.IncidentNumber} - {SerbianText.DateTime(trace.TakenUtc)}",
                Fonts.SubHeading, Brushes.Ink, 13);

            layout.Note(note);

            if (trace.Hops.Count == 0)
            {
                continue;
            }

            layout.Y += 6;

            var table = new Table(
                layout,
                ["Skok", "Adresa", "Odziv", "Gde"],
                [38, 212, 90, 159],
                [
                    XStringFormats.TopRight,
                    XStringFormats.TopLeft,
                    XStringFormats.TopRight,
                    XStringFormats.TopLeft,
                ]);

            foreach (var hop in trace.Hops)
            {
                var where = !hop.Answered
                    ? "nema odgovora"
                    : hop.IsPrivate ? "vaša mreža" : "izvan vaše mreže";

                var brush = hop.Answered ? Brushes.Ink : Brushes.Faint;

                table.Row(
                [
                    new Cell(hop.Ttl.ToString(CultureInfo.InvariantCulture), Brush: brush),
                    new Cell(hop.Address ?? "-", Brush: brush),
                    new Cell(hop.RoundTrip is { } value ? SerbianText.Duration(value) : "-", Brush: brush),
                    new Cell(where, Brush: brush),
                ]);
            }

            layout.Y += 4;
        }

        layout.Note(
            "Kako se čita trasa. Skok koji je odgovorio dokazuje da su paketi stigli do njega - " +
            "to je čvrst nalaz. Izostanak odgovora posle nekog skoka ne dokazuje kvar na " +
            "sledećem uređaju: veliki broj rutera na internetu je namerno podešen da ne " +
            "odgovara na ovu vrstu provere. Nalaz koji jeste jak je kada nijedan uređaj izvan " +
            "vaše mreže nije odgovorio, a ruter jeste - tada paket nije ni izašao iz kuće.");
    }

    private static void AppendGaps(Layout layout, SessionSnapshot session)
    {
        if (session.Gaps.Count == 0)
        {
            return;
        }

        layout.Heading("Pauze nadzora");

        var table = new Table(
            layout,
            ["Vreme", "Trajanje", "Uzrok"],
            [140, 100, 259],
            [XStringFormats.TopLeft, XStringFormats.TopRight, XStringFormats.TopLeft]);

        foreach (var gap in session.Gaps)
        {
            table.Row(
            [
                new Cell(SerbianText.DateTime(gap.DetectedUtc)),
                new Cell(SerbianText.Duration(gap.Duration)),
                new Cell(SerbianText.GapCauseLabel(gap.Cause)),
            ]);
        }

        layout.Y += 4;
        layout.Note(
            "Tokom ovih perioda ništa nije mereno, pa se ne računaju ni kao prekid ni kao " +
            "ispravan rad. Da su računati kao ispravan rad, dostupnost bi bila veća nego što " +
            "je stvarno izmerena.");
    }

    private static void AppendIntegrity(
        Layout layout,
        SessionSnapshot session,
        string? headHash,
        bool valid)
    {
        layout.Heading("Integritet zapisa");

        layout.Text(
            valid
                ? "Lanac otisaka je neprekinut - paket nije menjan nakon snimanja."
                : "Lanac otisaka je narušen - paket je menjan nakon snimanja.",
            Fonts.BodyBold,
            valid ? Brushes.OkText : Brushes.BadText,
            13);

        if (headHash is not null)
        {
            const double label = 72;

            // Monospaced so a reader comparing it against their own run can do it character
            // by character without losing their place in sixty-four hex digits.
            var hash = layout.Wrap(headHash, Fonts.Mono, layout.Width - label);
            var height = hash.Count * 9.5;

            layout.Ensure(height + 2);
            layout.Y += 2;

            layout.Gfx.DrawString("Završni otisak:", Fonts.Tiny, Brushes.Muted,
                new XRect(layout.Left, layout.Y, label, 10), XStringFormats.TopLeft);

            layout.Lines(hash, Fonts.Mono, Brushes.Muted,
                layout.Left + label, layout.Y, layout.Width - label, 9.5);

            layout.Y += height;
        }

        layout.Y += 4;

        // The measurements mean the same thing forever; the rules that interpret them do not.
        // Without these, a figure quoted from an old report cannot be reproduced and nobody
        // can tell whether that is a discrepancy or a changed algorithm.
        // Taken from the session, not from this build, for the same reason as in the HTML.
        layout.Note(
            "Zaključci u ovom izveštaju izvedeni su sledećim verzijama modela: format zapisa " +
            $"{session.SchemaVersion ?? EvidenceModelVersion.SchemaVersion}, " +
            $"klasifikacija {session.ClassifierVersion ?? EvidenceModelVersion.ClassifierVersion}, " +
            $"model pripisivanja {session.AttributionModelVersion ?? EvidenceModelVersion.AttributionModelVersion}, " +
            $"model pouzdanosti {session.ConfidenceModelVersion ?? EvidenceModelVersion.ConfidenceModelVersion}. " +
            "Sirova merenja se ne menjaju, ali pravila koja ih tumače mogu, pa je iz izveštaja " +
            "uvek moguće utvrditi kojom logikom je zaključak donet.");

        layout.Note(
            $"Izveštaj je napravio {BuildInfo.Product} verzija {BuildInfo.Version}" +
            (BuildInfo.DependenciesLocked ? ", sa unapred utvrđenim verzijama svih zavisnosti" : string.Empty) +
            ". Ista verzija programa nad istom sirovom evidencijom uvek daje isti izveštaj - isti " +
            "tekst, iste brojke, iste zaključke.");

        // The HTML report says the same thing and stops there, because for it that is the
        // whole truth. Here it is not: the PDF format carries identifiers the library mints
        // on every write, so two exports of one untouched session differ in a handful of
        // bytes that describe nothing. Left unsaid, someone rebuilding the report and
        // comparing checksums finds the PDF changed while everything else matched - and the
        // obvious reading of that is tampering.
        layout.Note(
            "Sam PDF fajl pritom nije bajt po bajt identičan između dve izrade. Format nosi " +
            "interne oznake - oznaku podskupa ugrađenog fonta i identifikator dokumenta - koje " +
            "se generišu iznova pri svakom upisu i ne zavise ni od jednog merenja. Otisak PDF " +
            "fajla se zato može razlikovati; SirovaEvidencija.jsonl i Izvestaj.html ostaju " +
            "nepromenjeni i njihovi otisci se poklapaju.");

        layout.Note(
            "Svaki zapis u sirovoj evidenciji sadrži otisak prethodnog, pa izmena bilo kog " +
            "ranijeg reda narušava sve otiske posle njega. Proveru može ponoviti bilo ko, " +
            "uključujući tehničara operatera.");
    }

    private static void AppendLimitations(Layout layout, SpeedMeasurementNote? speed = null)
    {
        layout.Heading("Granice ovog dokumenta");

        // The speed item says one thing when no measurement is attached and another when a
        // valid one is, mirroring the HTML report: a document with a properly measured figure
        // beside it should not claim it proves nothing about the rate.
        var speedItem = speed?.ValidForComplaint == true
            ? "Priloženo merenje brzine ispunjava propisane uslove, ali je jedno merenje i ne " +
              "zamenjuje postupak propisan propisom: merenje RATEL NetTest aplikacijom, na " +
              "Ethernetskom portu modema, po proceduri od tri dana."
            : "Ugovorena brzina se ovim dokumentom ne dokazuje. Po propisu je za to potrebno " +
              "merenje preko Ethernet kabla povezanog direktno na modem.";

        Bullet("Ovo je tehnička evidencija prekida, a ne merenje ovlašćene treće strane niti " +
               "zapis potpisan od strane operatera.");
        Bullet("Dokazano je da paket nije menjan nakon snimanja. Nije dokazano ko ga je i kada " +
               "napravio - za to je potreban vremenski žig treće strane.");
        Bullet(speedItem);
        Bullet("Uz prigovor zbog kvaliteta internet usluge RATEL navodi i rezultate merenja " +
               "aplikacijom RATEL NetTest, po proceduri od tri dana sa po dva merenja pre i " +
               "posle podne.");

        void Bullet(string text)
        {
            const double indent = 12;
            var lines = layout.Wrap(text, Fonts.Small, layout.Width - indent);
            var height = lines.Count * 10.5;

            layout.Ensure(height + 3);
            layout.Gfx.DrawString("•", Fonts.Small, Brushes.Muted,
                new XRect(layout.Left, layout.Y, indent, 10.5), XStringFormats.TopLeft);

            layout.Lines(lines, Fonts.Small, Brushes.Body, layout.Left + indent, layout.Y,
                layout.Width - indent, 10.5);

            layout.Y += height + 3;
        }
    }

    /// <summary>
    /// Page numbers, stamped once every page exists.
    /// <para>
    /// Present because of how the document is used: it is printed, stapled and handed across
    /// a counter, and a stack of unnumbered pages cannot be shown to be complete. "Strana 3
    /// od 7" is what makes a missing page visible.
    /// </para>
    /// </summary>
    private static void AppendFooters(PdfDocument document, SessionSnapshot session)
    {
        var total = document.PageCount;

        for (var index = 0; index < total; index++)
        {
            var page = document.Pages[index];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var width = page.Width.Point;
            var y = page.Height.Point - MarginBottom + 16;

            gfx.DrawLine(Pens.Rule, MarginX, y - 6, width - MarginX, y - 6);

            gfx.DrawString(
                $"{BuildInfo.Product} {BuildInfo.Version} · Sesija {session.SessionId}",
                Fonts.Tiny, Brushes.Faint,
                new XRect(MarginX, y, width - (2 * MarginX), 10), XStringFormats.TopLeft);

            gfx.DrawString(
                $"Strana {index + 1} od {total}",
                Fonts.Tiny, Brushes.Faint,
                new XRect(MarginX, y, width - (2 * MarginX), 10), XStringFormats.TopRight);
        }
    }

    // ---- Layout -------------------------------------------------------------

    /// <param name="Bold">Used for the reported duration, the one figure a reader looks for.</param>
    /// <param name="Align">
    /// Left for prose, right for anything a reader compares down the column. Durations and
    /// counts set left do not line up on their digits, so "7m 19s" and "48,0 s" sit at
    /// different depths and the column cannot be scanned - which is the only reason to put
    /// numbers in a column at all.
    /// </param>
    private sealed record Cell(
        string Text,
        bool Bold = false,
        XBrush? Brush = null,
        XStringFormat? Align = null);

    /// <summary>
    /// A cursor down an A4 page that starts a new one when it runs out of room.
    /// <para>
    /// Deliberately not a general layout engine. Every section here knows its own shape, and
    /// the only thing they all need is somewhere to draw and a rule for what happens at the
    /// bottom of the page.
    /// </para>
    /// </summary>
    private sealed class Layout : IDisposable
    {
        private readonly PdfDocument _document;
        private XGraphics _gfx = null!;

        public Layout(PdfDocument document)
        {
            _document = document;
            NewPage();
        }

        public XGraphics Gfx => _gfx;

        public double Y { get; set; }

        public double Left => MarginX;

        public double Right => MarginX + Width;

        public double Width { get; private set; }

        private double Bottom { get; set; }

        public void NewPage()
        {
            _gfx?.Dispose();

            var page = _document.AddPage();
            page.Size = PageSize.A4;
            page.Orientation = PageOrientation.Portrait;

            _gfx = XGraphics.FromPdfPage(page);
            Width = page.Width.Point - (2 * MarginX);
            Bottom = page.Height.Point - MarginBottom;
            Y = MarginTop;
        }

        /// <summary>Starts a new page if <paramref name="height"/> will not fit. True when it did.</summary>
        public bool Ensure(double height)
        {
            if (Y + height <= Bottom || Y <= MarginTop)
            {
                return false;
            }

            NewPage();
            return true;
        }

        /// <summary>A section heading with the rule under it.</summary>
        public void Heading(string text)
        {
            // Kept with at least a couple of lines of what follows, so no page ever ends with
            // a heading whose content is overleaf.
            Ensure(56);

            Y += 12;
            _gfx.DrawString(text, Fonts.Heading, Brushes.Ink,
                new XRect(Left, Y, Width, 14), XStringFormats.TopLeft);

            Y += 15;
            _gfx.DrawLine(Pens.Heading, Left, Y, Left + Width, Y);
            Y += 9;
        }

        /// <summary>One wrapped paragraph at the cursor, which then moves past it.</summary>
        public void Text(string text, XFont font, XBrush brush, double lineHeight)
        {
            var lines = Wrap(text, font, Width);

            Ensure(lines.Count * lineHeight);
            Y = Lines(lines, font, brush, Left, Y, Width, lineHeight);
        }

        /// <summary>
        /// How much room <see cref="Note"/> will take, so a caller can reserve a heading and
        /// its explanation together rather than discovering the split after drawing one.
        /// </summary>
        public double NoteHeight(string text) =>
            (Wrap(text, Fonts.Small, Width - 17).Count * 10.5) + 22;

        /// <summary>The tinted aside used for every "how to read this" explanation.</summary>
        public void Note(string text)
        {
            const double pad = 7;
            var inner = Width - (2 * pad) - 3;
            var lines = Wrap(text, Fonts.Small, inner);
            var height = (lines.Count * 10.5) + (2 * pad);

            Ensure(height + 8);
            Y += 8;

            _gfx.DrawRectangle(Brushes.NoteFill, Left, Y, Width, height);
            _gfx.DrawRectangle(Brushes.NoteBar, Left, Y, 3, height);

            Lines(lines, Fonts.Small, Brushes.Body, Left + pad + 3, Y + pad, inner, 10.5);
            Y += height;
        }

        /// <summary>Draws prepared lines from <paramref name="y"/> and returns where they end.</summary>
        public double Lines(
            IReadOnlyList<string> lines, XFont font, XBrush brush,
            double x, double y, double width, double lineHeight, XStringFormat? align = null)
        {
            foreach (var line in lines)
            {
                _gfx.DrawString(line, font, brush,
                    new XRect(x, y, width, lineHeight), align ?? XStringFormats.TopLeft);
                y += lineHeight;
            }

            return y;
        }

        public List<string> Wrap(string text, XFont font, double width) =>
            WrapLines(_gfx, text, font, width);

        public void Dispose() => _gfx?.Dispose();
    }

    /// <summary>
    /// A table that repeats its header whenever it crosses onto a new page.
    /// <para>
    /// Not a nicety. The incident table is the part an operator's complaint desk reads, and
    /// a continuation page of unlabelled columns of durations is a page they can set aside
    /// as unreadable - which is how a browser's print function renders it today.
    /// </para>
    /// </summary>
    private sealed class Table
    {
        private const double RowPad = 5;
        private const double LineHeight = 9.6;

        private readonly Layout _layout;
        private readonly string[] _headers;
        private readonly double[] _widths;
        private readonly XStringFormat[] _aligns;

        /// <param name="aligns">
        /// Per-column alignment, applied to the header as well as the cells. A right-aligned
        /// column of durations under a left-aligned heading reads as a mistake, so the two
        /// are never allowed to disagree.
        /// </param>
        public Table(Layout layout, string[] headers, double[] widths, XStringFormat[]? aligns = null)
        {
            _layout = layout;
            _headers = headers;
            _widths = widths;
            _aligns = aligns ?? [.. headers.Select(_ => XStringFormats.TopLeft)];

            layout.Ensure(48);
            DrawHeader();
        }

        public void Row(IReadOnlyList<Cell> cells, XBrush? background = null)
        {
            var lines = new List<string>[cells.Count];
            var tallest = 1;

            for (var i = 0; i < cells.Count; i++)
            {
                lines[i] = _layout.Wrap(cells[i].Text, Font(cells[i]), _widths[i] - (2 * CellPad));
                tallest = Math.Max(tallest, lines[i].Count);
            }

            var height = (tallest * LineHeight) + RowPad;

            if (_layout.Ensure(height))
            {
                DrawHeader();
            }

            if (background is not null)
            {
                _layout.Gfx.DrawRectangle(background, _layout.Left, _layout.Y, _layout.Width, height);
            }

            var x = _layout.Left;

            for (var i = 0; i < cells.Count; i++)
            {
                _layout.Lines(
                    lines[i], Font(cells[i]), cells[i].Brush ?? Brushes.Ink,
                    x + CellPad, _layout.Y + (RowPad / 2), _widths[i] - (2 * CellPad), LineHeight,
                    cells[i].Align ?? _aligns[i]);

                x += _widths[i];
            }

            _layout.Y += height;
            _layout.Gfx.DrawLine(Pens.RowRule, _layout.Left, _layout.Y, _layout.Left + _layout.Width, _layout.Y);
        }

        /// <summary>A full-width caveat attached to the row above it.</summary>
        public void Flag(string text)
        {
            var indent = _widths[0];
            var lines = _layout.Wrap(text, Fonts.Tiny, _layout.Width - indent - CellPad);
            var height = (lines.Count * 9) + 3;

            if (_layout.Ensure(height))
            {
                DrawHeader();
            }

            _layout.Lines(
                lines, Fonts.Tiny, Brushes.Flag,
                _layout.Left + indent + CellPad, _layout.Y, _layout.Width - indent - CellPad, 9);

            _layout.Y += height;
            _layout.Gfx.DrawLine(Pens.RowRule, _layout.Left, _layout.Y, _layout.Left + _layout.Width, _layout.Y);
        }

        private static XFont Font(Cell cell) => cell.Bold ? Fonts.TableBold : Fonts.Table;

        private void DrawHeader()
        {
            const double height = 15;

            _layout.Gfx.DrawRectangle(Brushes.HeadFill, _layout.Left, _layout.Y, _layout.Width, height);

            var x = _layout.Left;

            for (var i = 0; i < _headers.Length; i++)
            {
                _layout.Gfx.DrawString(
                    Fit(_layout, _headers[i], Fonts.TableHead, _widths[i] - (2 * CellPad)),
                    Fonts.TableHead, Brushes.Ink,
                    new XRect(x + CellPad, _layout.Y + 3.5, _widths[i] - (2 * CellPad), 10),
                    _aligns[i]);

                x += _widths[i];
            }

            _layout.Y += height;
            _layout.Gfx.DrawLine(Pens.Rule, _layout.Left, _layout.Y, _layout.Left + _layout.Width, _layout.Y);
        }
    }

    // ---- Text measurement ---------------------------------------------------

    /// <summary>
    /// Breaks text to a width, splitting inside a word only when the word alone does not fit.
    /// <para>
    /// The character-level fallback is what keeps a sixty-four character hash and a long
    /// adapter name inside the page instead of running off the right edge, where the reader
    /// would never know anything was missing.
    /// </para>
    /// </summary>
    private static List<string> WrapLines(XGraphics gfx, string text, XFont font, double width)
    {
        var lines = new List<string>();

        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        var current = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";

            if (gfx.MeasureString(candidate, font).Width <= width)
            {
                current.Clear().Append(candidate);
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (gfx.MeasureString(word, font).Width <= width)
            {
                current.Append(word);
                continue;
            }

            var remainder = word;

            while (gfx.MeasureString(remainder, font).Width > width)
            {
                var take = 1;

                while (take < remainder.Length &&
                       gfx.MeasureString(remainder[..(take + 1)], font).Width <= width)
                {
                    take++;
                }

                lines.Add(remainder[..take]);
                remainder = remainder[take..];
            }

            current.Append(remainder);
        }

        lines.Add(current.ToString());
        return lines;
    }

    /// <summary>Shortens to fit on one line, with an ellipsis, for cells that must not wrap.</summary>
    private static string Fit(Layout layout, string text, XFont font, double width)
    {
        if (layout.Gfx.MeasureString(text, font).Width <= width)
        {
            return text;
        }

        var take = text.Length;

        while (take > 1 && layout.Gfx.MeasureString($"{text[..take]}…", font).Width > width)
        {
            take--;
        }

        return $"{text[..take]}…";
    }

    // ---- Palette ------------------------------------------------------------

    private static class Fonts
    {
        /// <summary>
        /// Unicode for every face, rather than letting the encoding be chosen per string.
        /// <para>
        /// Left to itself PDFsharp writes what fits in WinAnsi as WinAnsi and switches to
        /// Unicode only when a string forces it, so one report ends up spanning two encodings
        /// and two embedded subsets of the same face. It gets the answer right - <c>ž</c> and
        /// <c>š</c> exist in WinAnsi, <c>č ć đ</c> do not, and it switches for them - but the
        /// correctness of our letters then rests on a heuristic re-deciding it for every
        /// string in the document. Pinning the encoding costs a few kilobytes and settles it.
        /// </para>
        /// </summary>
        private static readonly XPdfFontOptions Unicode = XPdfFontOptions.UnicodeDefault;

        public static readonly XFont Title = Sans(17, XFontStyleEx.Bold);
        public static readonly XFont Heading = Sans(11.5, XFontStyleEx.Bold);
        public static readonly XFont SubHeading = Sans(9.6, XFontStyleEx.Bold);
        public static readonly XFont CardValue = Sans(13, XFontStyleEx.Bold);
        public static readonly XFont Body = Sans(8.6, XFontStyleEx.Regular);
        public static readonly XFont BodyBold = Sans(8.6, XFontStyleEx.Bold);
        public static readonly XFont Small = Sans(7.9, XFontStyleEx.Regular);
        public static readonly XFont Tiny = Sans(6.9, XFontStyleEx.Regular);
        public static readonly XFont Table = Sans(7.6, XFontStyleEx.Regular);
        public static readonly XFont TableBold = Sans(7.6, XFontStyleEx.Bold);
        public static readonly XFont TableHead = Sans(7.3, XFontStyleEx.Bold);

        public static readonly XFont Mono =
            new(EmbeddedFonts.Mono, 7.2, XFontStyleEx.Regular, Unicode);

        private static XFont Sans(double size, XFontStyleEx style) =>
            new(EmbeddedFonts.Sans, size, style, Unicode);
    }

    /// <summary>The same colours the HTML report uses, so the two documents look like one.</summary>
    private static class Brushes
    {
        public static readonly XBrush Ink = Solid(0x1d, 0x1d, 0x1f);
        public static readonly XBrush Body = Solid(0x55, 0x55, 0x5b);
        public static readonly XBrush Muted = Solid(0x6c, 0x6c, 0x72);
        public static readonly XBrush Faint = Solid(0x8a, 0x8a, 0x8f);
        public static readonly XBrush Flag = Solid(0xa0, 0x60, 0x00);

        public static readonly XBrush Good = Solid(0x2e, 0x9e, 0x5b);
        public static readonly XBrush Warn = Solid(0xe0, 0xa8, 0x00);
        public static readonly XBrush Bad = Solid(0xc0, 0x39, 0x2b);

        public static readonly XBrush GoodFill = Solid(0xea, 0xf7, 0xef);
        public static readonly XBrush WarnFill = Solid(0xfd, 0xf5, 0xe2);
        public static readonly XBrush BadFill = Solid(0xfd, 0xec, 0xeb);

        public static readonly XBrush OkText = Solid(0x1e, 0x7a, 0x45);
        public static readonly XBrush BadText = Solid(0xa5, 0x28, 0x1c);

        public static readonly XBrush HeadFill = Solid(0xf4, 0xf4, 0xf6);
        public static readonly XBrush NoteFill = Solid(0xf7, 0xf7, 0xf8);
        public static readonly XBrush NoteBar = Solid(0xc9, 0xc9, 0xcf);
        public static readonly XBrush UpstreamRow = Solid(0xff, 0xfa, 0xfa);

        public static readonly XBrush Band = new XSolidBrush(XColor.FromArgb(56, 0x4a, 0x90, 0xd9));

        private static XBrush Solid(int r, int g, int b) => new XSolidBrush(XColor.FromArgb(r, g, b));
    }

    private static class Pens
    {
        public static readonly XPen Rule = new(XColor.FromArgb(0xe3, 0xe3, 0xe6), 0.6);
        public static readonly XPen RowRule = new(XColor.FromArgb(0xf0, 0xf0, 0xf2), 0.6);
        public static readonly XPen Grid = new(XColor.FromArgb(0xe3, 0xe3, 0xe6), 0.5);
        public static readonly XPen Heading = new(XColor.FromArgb(0xec, 0xec, 0xef), 1.2);
        public static readonly XPen Mean = new(XColor.FromArgb(0x1f, 0x6f, 0xb8), 1.1);
    }
}
