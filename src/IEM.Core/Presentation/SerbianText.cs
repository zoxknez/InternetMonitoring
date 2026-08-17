using System.Globalization;
using IEM.Core.Classification;
using IEM.Core.Model;

namespace IEM.Core.Presentation;

/// <summary>
/// Renders machine-readable states as Serbian text.
/// <para>
/// Kept strictly separate from the evidence model: the raw log stores stable enum names,
/// and wording is applied only at the moment something is shown or printed. Rephrasing
/// the interface therefore cannot alter a single recorded fact.
/// </para>
/// <para>
/// Established jargon stays as it is - <c>ping</c>, <c>jitter</c>, <c>DNS</c> - because
/// the report has two readers: the customer, and the operator's technician. Forcing
/// invented translations on the second one helps neither.
/// </para>
/// </summary>
public static class SerbianText
{
    /// <summary>
    /// Serbian Latin, Serbia. Set explicitly rather than inherited from the system: the
    /// application is Serbian regardless of what language the user's Windows happens to be,
    /// and dates in a complaint must not silently come out as 8/13/2026.
    /// </summary>
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("sr-Latn-RS");

    public static string Label(this NetworkState state) => state switch
    {
        NetworkState.Ok => "Ispravno",
        NetworkState.AdapterDown => "Mrežna kartica nije aktivna",
        NetworkState.MonitoringGap => "Prekid nadzora",
        NetworkState.SelfTest => "Merenje brzine u toku",
        NetworkState.WifiRoaming => "Prelazak na drugu pristupnu tačku",
        NetworkState.WifiRadioDown => "Ruter je prestao da emituje Wi-Fi",
        NetworkState.GatewayDown => "Mrežni prolaz ne odgovara",
        NetworkState.CpeUpstreamUnreachable => "Ruter radi, veza ka internetu ne radi",
        NetworkState.CpeReboot => "Ruter se restartovao",
        NetworkState.InternetDown => "Internet nedostupan",
        NetworkState.DnsIspFailure => "DNS server operatera ne radi",
        NetworkState.DnsGlobalFailure => "Nijedan DNS server ne odgovara",
        NetworkState.IcmpFiltered => "Ping je filtriran (nije prekid)",
        NetworkState.CaptivePortalSuspected => "Sumnja na portal za prijavu na mrežu",
        NetworkState.PacketLoss => "Gubitak paketa",
        NetworkState.HighLatency => "Visoko kašnjenje",
        NetworkState.HighJitter => "Velika varijacija kašnjenja",
        _ => state.ToString(),
    };

    /// <summary>Plain-language explanation for someone who is not a network engineer.</summary>
    public static string Explanation(this NetworkState state) => state switch
    {
        NetworkState.Ok => "Sve provere prolaze.",
        NetworkState.AdapterDown =>
            "Mrežna kartica na vašem računaru nije aktivna. Uzrok je na računaru, ne kod operatera.",
        NetworkState.MonitoringGap =>
            "Nadzor je bio pauziran, najčešće zato što je računar spavao ili se restartovao. " +
            "Ovo se ne računa ni kao prekid ni kao ispravan rad.",
        NetworkState.SelfTest =>
            "U toku je merenje brzine, koje namerno opterećuje vezu. Ovaj period se ne ocenjuje.",
        NetworkState.WifiRoaming =>
            "Uređaj je prešao na drugu pristupnu tačku iste mreže. Ovo nije kvar.",
        NetworkState.WifiRadioDown =>
            "Vaša mreža je nestala iz opsega vidljivih mreža, a računar je i dalje ispravan. " +
            "Wi-Fi na ruteru je otkazao. Ovo je kvar rutera, ne operatera.",
        NetworkState.GatewayDown =>
            "Ruter ne odgovara, a internet nije dostupan. Problem je u lokalnoj mreži ili na samom ruteru.",
        NetworkState.CpeUpstreamUnreachable =>
            "Veza računara sa ruterom je ispravna, ali iza rutera ništa nije dostupno. " +
            "Problem je izolovan na putanju iza rutera. Merenje je rađeno sa vašeg računara, " +
            "pa ne pokazuje šta se dešava unutar mreže operatera - ali isključuje vašu " +
            "lokalnu mrežu kao uzrok.",
        NetworkState.CpeReboot =>
            "Ruter se restartovao tokom prekida. Ovo je kvar ili ponašanje rutera, ne operatera.",
        NetworkState.InternetDown =>
            "Nijedna internet meta nije dostupna, a stanje rutera nije poznato.",
        NetworkState.DnsIspFailure =>
            "DNS server koji vam je dodelio operater ne odgovara, dok javni DNS kroz istu " +
            "mrežnu putanju radi. Veza postoji, ali sajtovi se ne otvaraju. Kvar je na " +
            "razrešavanju imena, a ne na samoj vezi.",
        NetworkState.DnsGlobalFailure =>
            "Nijedan DNS server ne odgovara iako veza radi.",
        NetworkState.IcmpFiltered =>
            "Ping ne prolazi, ali saobraćaj radi normalno. Mnoge mreže namerno blokiraju ping. " +
            "Ovo NIJE prekid i ne ulazi u prigovor.",
        NetworkState.CaptivePortalSuspected =>
            "Mreža vraća neočekivan odgovor, što liči na portal za prijavu (hotel, javni Wi-Fi).",
        NetworkState.PacketLoss => "Deo paketa se gubi. Veza radi, ali nestabilno.",
        NetworkState.HighLatency => "Odziv je sporiji nego što bi trebalo.",
        NetworkState.HighJitter => "Kašnjenje jako varira, što kvari pozive i video.",
        _ => string.Empty,
    };

    public static string Label(this FaultAttribution attribution) => attribution switch
    {
        FaultAttribution.None => "Nije kvar",
        FaultAttribution.LocalDevice => "Vaš računar",
        FaultAttribution.Router => "Vaš ruter",
        FaultAttribution.Upstream => "Operater",
        _ => "Nije utvrđeno",
    };

    public static string Label(this Severity severity) => severity switch
    {
        Severity.Ok => "U redu",
        Severity.Info => "Informacija",
        Severity.Degraded => "Pogoršano",
        _ => "Prekid",
    };

    /// <summary>
    /// The band, which is what a reader is shown.
    /// <para>
    /// Never a percentage. Ninety-four percent looks like a probability and is not one - it
    /// is a weighted sum of heuristics, and printing it invites an argument about the second
    /// decimal instead of about the evidence.
    /// </para>
    /// </summary>
    public static string Label(this ConfidenceBand band) => band switch
    {
        ConfidenceBand.VeryHigh => "VRLO VISOKA",
        ConfidenceBand.High => "VISOKA",
        ConfidenceBand.Moderate => "UMERENA",
        ConfidenceBand.Low => "NISKA",
        _ => "VRLO NISKA",
    };

    /// <summary>
    /// Why the band came out where it did, in one sentence.
    /// <para>
    /// Coverage is the half that used to be missing: perfect support over a tenth of the
    /// picture is not a strong finding, and a reader is entitled to know which of the two
    /// limited the conclusion.
    /// </para>
    /// </summary>
    public static string Explain(this ConfidenceScore score)
    {
        ArgumentNullException.ThrowIfNull(score);

        var checkedCount = score.Supporting.Count() + score.Contradicting.Count();
        var missing = score.Missing.Count();

        if (checkedCount == 0)
        {
            return "Nijedan relevantan pokazatelj nije mogao da se proveri, pa zaključak nema potporu.";
        }

        var supported = score.Supporting.Count();
        var text =
            $"Provereno je {checkedCount} od {checkedCount + missing} relevantnih pokazatelja, " +
            $"od kojih {supported} ide u prilog ovom zaključku.";

        return missing > 0
            ? text + $" {missing} nije moglo da se proveri, što ograničava koliko visoko ocena sme da ide."
            : text;
    }

    /// <summary>
    /// Why monitoring paused, named accurately.
    /// <para>
    /// Three separate places used to fold every cause except a reboot and a clock change
    /// into "probably the computer sleeping" - so a service restart, which the engine knows
    /// about precisely, was described to an operator as a guess about something that did not
    /// happen. A statement like that in a document is one the operator can disprove from
    /// their own records, and it takes the rest of the evidence with it.
    /// </para>
    /// </summary>
    public static string Label(this GapCause cause) => cause switch
    {
        GapCause.Reboot => "restart računara",
        GapCause.ClockAdjustment => "pomeranje sistemskog sata",
        GapCause.Sleep => "računar je bio u stanju spavanja",
        GapCause.MonitorNotRunning => "nadzor nije bio pokrenut",
        _ => "uzrok nije utvrđen",
    };

    /// <summary>The same, from the name the raw log carries.</summary>
    public static string GapCauseLabel(string cause) =>
        Enum.TryParse<GapCause>(cause, out var parsed) ? parsed.Label() : "uzrok nije utvrđen";

    /// <summary>
    /// A transfer rate as a person reads it: megabytes a second past a megabyte, kilobytes
    /// below that. Used where the report says how much of the line the computer was using
    /// itself, and the reader is being invited to recognise their own download.
    /// </summary>
    public static string Rate(long bytesPerSecond) => bytesPerSecond >= 1_000_000
        ? $"{(bytesPerSecond / 1_000_000d).ToString("0.#", Culture)} MB/s"
        : $"{(bytesPerSecond / 1_000d).ToString("0.#", Culture)} KB/s";

    /// <summary>Serbian labels for the confidence evidence keys.</summary>
    public static string EvidenceLabel(string key) => key switch
    {
        "link.stayedUp" => "Mrežna veza je bila aktivna sve vreme",
        "link.wired" => "Veza je žičana (nema Wi-Fi nedoumica)",
        "device.noAdapterReset" => "Nema reseta mrežne kartice",
        "device.noSleep" => "Računar nije spavao",
        "device.noClockJump" => "Nema pomeranja sistemskog sata",
        "device.noSaturation" => "Veza nije bila zauzeta vašim saobraćajem",
        "wifi.ssidVisible" => "Wi-Fi mreža je ostala vidljiva",
        "wifi.signalHealthy" => "Jačina signala je bila zadovoljavajuća",
        "wifi.noRoaming" => "Nema prelaska na drugu pristupnu tačku",
        "cpe.gatewayReachable" => "Ruter je odgovarao tokom prekida",
        "cpe.wanReportedDown" => "Ruter prijavljuje da je WAN veza pala",
        "cpe.noReboot" => "Ruter se nije restartovao",
        "upstream.icmpFailed" => "Sve nezavisne ping mete su bile nedostupne",
        "upstream.tcpFailed" => "Sve TCP provere su pale",
        "upstream.tlsFailed" => "Šifrovana veza nije uspostavljena",
        "upstream.publicDnsFailed" => "Javni DNS nije odgovarao",
        "upstream.httpFailed" => "HTTP provera je pala",
        "upstream.traceLeftNetwork" => "Trasa je dokazano izašla iz vaše lokalne mreže",
        "upstream.publicIpChanged" => "Javna IP adresa se promenila nakon prekida",
        _ => key,
    };

    /// <summary>
    /// A percentage carrying only the decimals it actually has.
    /// <para>
    /// Availability was printed to four decimals unconditionally, so a connection that never
    /// dropped read "100,0000 %" - four zeros claiming a precision the figure does not need.
    /// A clean hundred is written as <c>100 %</c>, while 99,9987 keeps every digit, because
    /// over two days of monitoring the fourth decimal is about ten seconds of outage.
    /// </para>
    /// <para>
    /// Two roundings are refused outright. A value below 100 is never shown as a clean 100:
    /// half a second of outage in two days rounds to 100,0000 at four decimals, and printing
    /// "100 %" for it says in a legal document that the service never failed. A value above
    /// zero is never shown as a clean 0, for the same reason reversed. In both cases the
    /// figure is moved away from the misleading round number rather than towards it.
    /// </para>
    /// </summary>
    public static string Percent(double value, int decimals = 2)
    {
        var scale = Math.Pow(10, decimals);
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);

        if (rounded >= 100 && value < 100)
        {
            rounded = Math.Floor(value * scale) / scale;
        }
        else if (rounded <= 0 && value > 0)
        {
            rounded = Math.Ceiling(value * scale) / scale;
        }

        // "0.##" keeps the decimals that carry a digit and drops the ones that are only zeros.
        return string.Concat(rounded.ToString($"0.{new string('#', decimals)}", Culture), " %");
    }

    /// <summary>Formats a duration the way a person reads it, not as 00:00:08.2710000.</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.FromMinutes(1))
        {
            return string.Format(Culture, "{0:0.0} s", value.TotalSeconds);
        }

        if (value < TimeSpan.FromHours(1))
        {
            return string.Format(Culture, "{0}m {1}s", (int)value.TotalMinutes, value.Seconds);
        }

        return string.Format(Culture, "{0}h {1}m {2}s", (int)value.TotalHours, value.Minutes, value.Seconds);
    }

    public static string DateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("dd.MM.yyyy. HH:mm:ss", Culture);
}
