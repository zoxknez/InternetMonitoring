using IEM.Core;
using IEM.Core.Incidents;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Cli;

/// <summary>
/// Prints what the engine observes.
/// <para>
/// Quiet by default: only state changes and incidents, because a two-day run that prints
/// a line per sample is unreadable and buries the handful of lines that matter.
/// </para>
/// </summary>
public sealed class ConsoleReporter
{
    private readonly MonitorEngine _engine;
    private readonly CliSettings _settings;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private NetworkState? _lastPrintedState;

    public ConsoleReporter(MonitorEngine engine, CliSettings settings)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _engine.SampleRecorded += OnSample;
        _engine.IncidentClosed += OnIncidentClosed;
        _engine.GapDetected += OnGap;
        _engine.ClockAnomalyDetected += OnClockAnomaly;
    }

    public void PrintHeader(LinkSnapshot link, SessionPaths? paths)
    {
        ArgumentNullException.ThrowIfNull(link);

        var duration = _settings.Duration == Timeout.InfiniteTimeSpan
            ? "do prekida (Ctrl+C)"
            : SerbianText.Duration(_settings.Duration);

        var medium = link.Medium switch
        {
            LinkMedium.Ethernet => "žičana veza",
            LinkMedium.Wireless => "bežična veza",
            _ => "nepoznat tip veze",
        };

        Console.WriteLine();
        Console.WriteLine("  INTERNET EVIDENCE MONITOR");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine($"  Početak:   {SerbianText.DateTime(_startedAt)}");
        Console.WriteLine($"  Trajanje:  {duration}");
        Console.WriteLine($"  Adapter:   {link.InterfaceName} ({medium})");

        if (link.GatewayAddress is not null)
        {
            Console.WriteLine($"  Ruter:     {link.GatewayAddress}");
        }

        Console.WriteLine(paths is null
            ? "  Zapis:     ISKLJUČEN - ništa se ne snima na disk"
            : $"  Zapis:     {paths.Directory}");

        // Said up front rather than only in the summary. Someone starting a two-day test
        // to prove a speed problem over Wi-Fi needs to know now, not on Sunday evening.
        if (link.Medium == LinkMedium.Wireless)
        {
            Console.WriteLine();
            Write(ConsoleColor.Yellow,
                "  Napomena: nadzirete bežičnu vezu. Evidencija PREKIDA je punovažna,");
            Write(ConsoleColor.Yellow,
                "  ali se ugovorena BRZINA ne može dokazivati preko Wi-Fi-a - za to je");
            Write(ConsoleColor.Yellow,
                "  potreban Ethernet kabl povezan direktno na modem.");
        }

        Console.WriteLine();
        Console.WriteLine("  Prekid nadzora: Ctrl+C. Prikupljeni podaci ostaju sačuvani.");
        Console.WriteLine();
    }

    /// <summary>Where the evidence landed and whether it verifies.</summary>
    public static void PrintEvidenceSummary(SessionPaths paths, ChainVerification verification)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(verification);

        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine("  EVIDENCIJA");
        Console.WriteLine();
        Console.WriteLine($"  Folder:      {paths.Directory}");
        Console.WriteLine($"  Zapisa:      {verification.EntriesChecked}");

        if (verification.Valid)
        {
            Write(ConsoleColor.Green, "  Integritet:  ISPRAVAN - lanac otisaka je neprekinut");
        }
        else
        {
            Write(ConsoleColor.Red,
                $"  Integritet:  NARUŠEN - red {verification.FirstBrokenLine}: {verification.Reason}");
        }

        Console.WriteLine($"  Otisak:      {verification.HeadHash[..16]}…");
        Console.WriteLine();
    }

    private void OnSample(MonitorSample sample)
    {
        if (_settings.Verbose)
        {
            PrintVerboseSample(sample);
            return;
        }

        // Only speak up when something actually changed.
        if (_lastPrintedState == sample.Verdict.State)
        {
            return;
        }

        _lastPrintedState = sample.Verdict.State;

        var color = sample.Verdict.Severity switch
        {
            Severity.Outage => ConsoleColor.Red,
            Severity.Degraded => ConsoleColor.Yellow,
            Severity.Info => ConsoleColor.DarkGray,
            _ => ConsoleColor.Green,
        };

        var attribution = sample.Verdict.State.AttributionOf();
        var blame = attribution == FaultAttribution.None ? string.Empty : $"  →  {attribution.Label()}";

        Write(sample.Instant.Wall, color, $"{sample.Verdict.State.Label()}{blame}");
    }

    /// <summary>
    /// Shows the tally per probe family. Useful while diagnosing, and it makes visible
    /// which evidence was actually gathered rather than only the conclusion drawn from it -
    /// including probes that were skipped because their cached result had gone stale.
    /// </summary>
    private static void PrintVerboseSample(MonitorSample sample)
    {
        var cycle = sample.Cycle;
        var rtt = cycle.AverageExternalRoundTrip;
        var latency = rtt is null ? "-" : $"{rtt.Value.TotalMilliseconds:F0} ms";

        var breakdown =
            $"gw {Tally(cycle.Gateway)} icmp {Tally(cycle.ExternalIcmp)} tcp {Tally(cycle.ExternalTcp)} " +
            $"tls {Tally(cycle.ExternalTls)} dns[isp {Tally(cycle.DnsIsp)} pub {Tally(cycle.DnsPublic)} " +
            $"sys {Tally(cycle.DnsSystem)}] http {Tally(cycle.Http)}";

        Write(sample.Instant.Wall, ConsoleColor.DarkGray,
            $"#{sample.Sequence,-5} {sample.Verdict.State.Label(),-34} {latency,7}  {breakdown}  [{sample.Phase}]");

        static string Tally(ProbeTally tally) => tally.IsSilent ? "-" : $"{tally.Succeeded}/{tally.Attempted}";
    }

    private void OnIncidentClosed(IncidentRecord incident)
    {
        var uncertainty = incident.DurationUncertainty > TimeSpan.FromMilliseconds(1)
            ? $" (±{SerbianText.Duration(incident.DurationUncertainty)})"
            : string.Empty;

        Write(incident.EndedAtUtc, ConsoleColor.Cyan,
            $"Prekid #{incident.Number} završen - trajanje {SerbianText.Duration(incident.DurationReported)}{uncertainty}" +
            $", {incident.WorstState.Label()}");

        if (incident.EndedByGap)
        {
            Write(incident.EndedAtUtc, ConsoleColor.DarkYellow,
                "  Napomena: nadzor je prestao tokom ovog prekida. Mereno je samo do tog trenutka.");
        }

        if (incident.StartedAfterGap)
        {
            Write(incident.EndedAtUtc, ConsoleColor.DarkYellow,
                "  Napomena: ovo je nastavak istog događaja koji je pauza nadzora presekla.");
        }

        if (incident.RouteChanged)
        {
            Write(incident.EndedAtUtc, ConsoleColor.DarkYellow,
                "  Napomena: saobraćaj je tokom prekida promenio mrežni adapter, " +
                "pa ovaj zapis nije čist dokaz o jednoj vezi.");
        }
    }

    private void OnGap(MonitoringGapEvent gap)
    {
        var cause = gap.Cause switch
        {
            GapCause.Reboot => "računar se restartovao",
            GapCause.ClockAdjustment => "sistemski sat je pomeren",
            GapCause.MonitorNotRunning => "nadzor nije bio pokrenut",
            GapCause.Sleep => "računar je bio u stanju spavanja",
            _ => "uzrok nepoznat, najverovatnije spavanje računara",
        };

        Write(gap.DetectedAt.Wall, ConsoleColor.DarkYellow,
            $"Nadzor pauziran {SerbianText.Duration(gap.Duration)} - {cause}. Ne računa se kao prekid veze.");
    }

    private void OnClockAnomaly(Core.Time.ClockObservation observation)
    {
        Write(DateTimeOffset.Now, ConsoleColor.DarkYellow,
            $"Sistemski sat je pomeren za {SerbianText.Duration(observation.Skew.Duration())}. " +
            "Trajanja se i dalje mere nezavisnim brojačem i ostaju tačna.");
    }

    public void PrintSummary()
    {
        var stats = _engine.Statistics;

        Console.WriteLine();
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine("  REZIME");
        Console.WriteLine();
        Console.WriteLine($"  Ukupno vreme:           {SerbianText.Duration(stats.WallClockTime)}");
        Console.WriteLine($"  Stvarno nadzirano:      {SerbianText.Duration(stats.MonitoredTime)}");

        if (stats.GapTime > TimeSpan.Zero)
        {
            Console.WriteLine($"  Nenadzirano (pauze):    {SerbianText.Duration(stats.GapTime)}");
        }

        Console.WriteLine();
        Console.WriteLine($"  Dostupnost:             {SerbianText.Percent(stats.AvailabilityPercent)}");
        Console.WriteLine($"  Dostupnost bez lokalnih kvarova: {SerbianText.Percent(stats.UpstreamAvailabilityPercent)}");
        Console.WriteLine();
        Console.WriteLine($"  Prekida ukupno:         {stats.Incidents.Count}");
        Console.WriteLine($"  Od toga kod operatera:  {stats.UpstreamIncidentCount}");
        Console.WriteLine($"  Nedostupnost operatera: {SerbianText.Duration(stats.UpstreamDowntime)}");
        Console.WriteLine($"  Lokalna nedostupnost:   {SerbianText.Duration(stats.LocalDowntime)}");

        if (stats.UpstreamIncidentCount > 0)
        {
            Console.WriteLine($"  Najduži prekid:         {SerbianText.Duration(stats.LongestUpstreamOutage)}");
        }

        PrintVerdict(stats);

        Console.WriteLine();
        Console.WriteLine("  Napomena: ovo je tehnička evidencija prekida. Za dokazivanje ugovorene");
        Console.WriteLine("  brzine potrebno je posebno merenje preko Ethernet kabla, kao i rezultati");
        Console.WriteLine("  RATEL NetTest aplikacije.");
        Console.WriteLine();
    }

    /// <summary>
    /// The one line a non-technical user actually needs: is there a case here, and against whom.
    /// </summary>
    private static void PrintVerdict(SessionStatistics stats)
    {
        Console.WriteLine();

        if (stats.MonitoredTime < TimeSpan.FromMinutes(1))
        {
            Write(ConsoleColor.DarkGray, "  Test je prekratak da bi se izveo zaključak.");
            return;
        }

        if (stats.UpstreamIncidentCount > 0)
        {
            Write(ConsoleColor.Red,
                $"  Potvrđeni prekidi na strani operatera: {stats.UpstreamIncidentCount}. Imate osnov za prigovor.");
            return;
        }

        if (stats.LocalDowntime > TimeSpan.Zero)
        {
            Write(ConsoleColor.Yellow,
                "  Prekidi postoje, ali su lokalni (računar, Wi-Fi ili ruter). Rešite to pre prigovora.");
            return;
        }

        Write(ConsoleColor.Green, "  Veza je bila stabilna. Nema osnova za prigovor.");
    }

    private static void Write(DateTimeOffset when, ConsoleColor color, string message) =>
        Write(color, $"  {when.ToLocalTime():HH:mm:ss}  {message}");

    private static void Write(ConsoleColor color, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
