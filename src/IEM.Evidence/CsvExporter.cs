using System.Globalization;
using System.Text;
using IEM.Core.Presentation;
using IEM.Storage;

namespace IEM.Evidence;

/// <summary>
/// Writes the tabular exports a person can open and check for themselves.
/// <para>
/// Two details here are not cosmetic. The files carry a UTF-8 byte order mark, because
/// without one Excel on a Serbian Windows reads č as Ä and the customer forwards a
/// mangled file to their operator. And the separator is a semicolon, because the Serbian
/// locale uses a comma for decimals - comma-separated numbers would collapse into the
/// wrong columns on the very machines this is written for.
/// </para>
/// </summary>
public static class CsvExporter
{
    private const char Separator = ';';

    /// <summary>Excel needs the BOM to detect UTF-8. Everything else tolerates it.</summary>
    private static readonly UTF8Encoding ExcelSafeUtf8 = new(encoderShouldEmitUTF8Identifier: true);

    public static void WriteIncidents(string path, SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var lines = new List<string>
        {
            Join(
                "Redni broj", "Početak", "Kraj", "Trajanje (s)", "Najmanje (s)", "Najviše (s)",
                "Stanje", "Uzrok kod", "Broj uzoraka", "Nezavršen",
                "Prekinut pauzom", "Nastavak posle pauze", "Promenjena putanja", "Oznaka događaja"),
        };

        foreach (var incident in session.Incidents)
        {
            lines.Add(Join(
                incident.Number.ToString(CultureInfo.InvariantCulture),
                SerbianText.DateTime(incident.StartedUtc),
                SerbianText.DateTime(incident.EndedUtc),
                Seconds(incident.DurationReported),
                Seconds(incident.DurationMin),
                Seconds(incident.DurationMax),
                incident.WorstState.Label(),
                incident.Attribution.Label(),
                incident.SampleCount.ToString(CultureInfo.InvariantCulture),
                incident.IsOpen ? "da" : "ne",
                incident.EndedByGap ? "da" : "ne",
                incident.StartedAfterGap ? "da" : "ne",
                incident.RouteChanged ? "da" : "ne",
                incident.CorrelationId.ToString()));
        }

        File.WriteAllLines(path, lines, ExcelSafeUtf8);
    }

    public static void WriteGaps(string path, SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var lines = new List<string> { Join("Vreme", "Trajanje (s)", "Uzrok") };

        foreach (var gap in session.Gaps)
        {
            lines.Add(Join(
                SerbianText.DateTime(gap.DetectedUtc),
                Seconds(gap.Duration),
                TranslateCause(gap.Cause)));
        }

        File.WriteAllLines(path, lines, ExcelSafeUtf8);
    }

    /// <summary>
    /// The bucketed latency series rather than every raw sample. The raw samples live in
    /// the chain; a spreadsheet with a hundred and seventy thousand rows helps nobody.
    /// </summary>
    public static void WriteMeasurements(string path, SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var lines = new List<string>
        {
            Join("Vreme od početka (s)", "Najmanje kašnjenje (ms)", "Prosek (ms)", "Najveće (ms)", "Prekid", "Pogoršanje"),
        };

        foreach (var bucket in session.Latency)
        {
            lines.Add(Join(
                Seconds(bucket.Offset),
                Number(bucket.MinRtt),
                Number(bucket.AverageRtt),
                Number(bucket.MaxRtt),
                bucket.Outage ? "da" : "ne",
                bucket.Degraded ? "da" : "ne"));
        }

        File.WriteAllLines(path, lines, ExcelSafeUtf8);
    }

    public static void WriteSummary(string path, SessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var lines = new List<string>
        {
            "REZIME NADZORA INTERNET VEZE",
            "",
            $"Sesija:                 {session.SessionId}",
            $"Računar:                {session.Machine}",
            $"Adapter:                {session.InterfaceName} ({Medium(session)})",
            $"Početak:                {SerbianText.DateTime(session.StartedUtc)}",
            $"Kraj:                   {(session.EndedUtc is { } end ? SerbianText.DateTime(end) : "nije završeno")}",
            "",
            $"Ukupno vreme:           {SerbianText.Duration(session.WallClockTime)}",
            $"Stvarno nadzirano:      {SerbianText.Duration(session.MonitoredTime)}",
            $"Nenadzirano (pauze):    {SerbianText.Duration(session.GapTime)}",
            $"Broj uzoraka:           {session.SampleCount.ToString("N0", SerbianText.Culture)}",
            "",
            $"Dostupnost:             {SerbianText.Percent(session.AvailabilityPercent)}",
            $"Dostupnost bez lokalnih kvarova: {SerbianText.Percent(session.UpstreamAvailabilityPercent)}",
            "",
            $"Prekida ukupno:         {session.Incidents.Count}",
            $"Od toga kod operatera:  {session.UpstreamIncidents.Count()}",
            $"Nedostupnost operatera: {SerbianText.Duration(session.UpstreamDowntime)}",
            $"Lokalna nedostupnost:   {SerbianText.Duration(session.LocalDowntime)}",
            $"Najduži prekid kod operatera: {SerbianText.Duration(session.LongestUpstreamOutage)}",
            "",
            "KAKO SE ČITAJU TRAJANJA",
            "",
            "Merenje je diskretno, pa se tačan početak prekida nalazi između poslednjeg",
            "ispravnog i prvog neispravnog uzorka. Zato svaki prekid ima tri broja:",
            "najmanje moguće trajanje (ne može se osporiti), najviše moguće, i središnju",
            "procenu koja uvek leži između njih.",
            "",
            "Dostupnost se računa u odnosu na stvarno nadzirano vreme. Pauze nadzora -",
            "spavanje ili restart računara - ne računaju se ni kao prekid ni kao ispravan",
            "rad, jer tada ništa nije mereno.",
            "",
            "GRANICE OVOG DOKUMENTA",
            "",
            "Ovo je tehnička evidencija prekida, a ne merenje ovlašćene treće strane.",
            "Za dokazivanje ugovorene brzine potrebno je merenje preko Ethernet kabla",
            "povezanog direktno na modem, kao i rezultati RATEL NetTest aplikacije.",
        };

        File.WriteAllLines(path, lines, ExcelSafeUtf8);
    }

    private static string Medium(SessionSnapshot session) => session.Medium switch
    {
        Core.Model.LinkMedium.Ethernet => "žičana veza",
        Core.Model.LinkMedium.Wireless => "bežična veza",
        _ => "nepoznat tip veze",
    };

    private static string TranslateCause(string cause) => SerbianText.GapCauseLabel(cause);

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("F3", SerbianText.Culture);

    private static string Number(double? value) =>
        value is null ? string.Empty : value.Value.ToString("F1", SerbianText.Culture);

    private static string Join(params string[] fields) =>
        string.Join(Separator, fields.Select(Escape));

    private static string Escape(string field)
    {
        if (!field.Contains(Separator, StringComparison.Ordinal) &&
            !field.Contains('"', StringComparison.Ordinal) &&
            !field.Contains('\n', StringComparison.Ordinal))
        {
            return field;
        }

        return $"\"{field.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
