using System.Globalization;
using IEM.Core.Presentation;
using IEM.Core.Speed;

namespace IEM.Cli;

public sealed record CliSettings
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromHours(48);

    public string? InterfaceName { get; init; }

    /// <summary>Print every sample rather than only state changes.</summary>
    public bool Verbose { get; init; }

    public bool ShowHelp { get; init; }

    /// <summary>
    /// Where session directories are created. Uses the resolved desktop path rather than a
    /// composed one, so a desktop redirected into OneDrive still works.
    /// </summary>
    public string OutputRoot { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "InternetEvidence");

    /// <summary>Skip recording entirely. For a quick look, not for anything evidential.</summary>
    public bool NoRecording { get; init; }

    /// <summary>
    /// Session directory to verify instead of monitoring.
    /// <para>
    /// Exists so a package can be checked by whoever receives it, not only by whoever
    /// produced it. Evidence nobody else can verify is not worth much.
    /// </para>
    /// </summary>
    public string? VerifyDirectory { get; init; }

    /// <summary>
    /// Session directory to rebuild the report for, instead of monitoring.
    /// <para>
    /// Matters because a session cut short by a power cut still has intact evidence on
    /// disk; getting a report out of it must not mean repeating a two-day test.
    /// </para>
    /// </summary>
    public string? ReportDirectory { get; init; }

    /// <summary>
    /// Print which adapter and source address traffic to each probe target leaves through,
    /// then exit.
    /// <para>
    /// Worth having on its own: a customer running the test over Wi-Fi while a docking
    /// station or a VPN is quietly carrying the traffic would otherwise collect two days of
    /// evidence about the wrong link.
    /// </para>
    /// </summary>
    public bool ShowPaths { get; init; }

    /// <summary>A single address to resolve, instead of the usual probe targets.</summary>
    public string? PathTarget { get; init; }

    /// <summary>Measure download speed and judge it, then exit.</summary>
    public bool MeasureSpeed { get; init; }

    /// <summary>Contracted download rate in Mbit/s, when the user supplied one.</summary>
    public double? ContractedDownloadMbps { get; init; }

    /// <summary>
    /// Contracted upload rate in Mbit/s, when the user supplied one.
    /// <para>
    /// Separate from the download figure because domestic contracts state the two separately
    /// and asymmetrically, and a connection that meets its download rate while falling short
    /// on upload is an ordinary complaint - one a download-only measurement could not state.
    /// </para>
    /// </summary>
    public double? ContractedUploadMbps { get; init; }

    /// <summary>
    /// Whether the sending half of the measurement runs. Off doubles nothing and halves the
    /// data the measurement costs, which matters on a metered connection.
    /// </summary>
    public bool MeasureUpload { get; init; } = true;

    /// <summary>
    /// How long <c>--brzina</c> stands in the queue while the link is busy, before giving up.
    /// <para>
    /// A measurement on a busy link is worthless, but so is telling someone who came for a
    /// measurement to come back later. The default parks the command until the connection
    /// goes quiet; zero restores the check-and-refuse behaviour.
    /// </para>
    /// </summary>
    public TimeSpan SpeedQueueTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Whether <c>--cekaj</c> was given, so it can be refused without <c>--brzina</c>.</summary>
    public bool SpeedQueueRequested { get; init; }

    /// <summary>
    /// How long to wait before the speed measurement starts.
    /// <para>
    /// A line whose speed dips at some hour of the evening is best measured at that hour,
    /// and nobody wants to sit by the console until then. The command parks itself for the
    /// given time, then measures exactly as it would have.
    /// </para>
    /// </summary>
    public TimeSpan SpeedStartDelay { get; init; }

    /// <summary>Whether <c>--zakazi</c> was given, so it can be refused without <c>--brzina</c>.</summary>
    public bool SpeedStartDelayRequested { get; init; }

    /// <summary>Session directory to prepare a complaint for.</summary>
    public string? ComplaintDirectory { get; init; }

    /// <summary>Operator the complaint is addressed to.</summary>
    public string? OperatorName { get; init; }

    /// <summary>Print where the complaint case stands, then exit.</summary>
    public bool ShowCase { get; init; }

    /// <summary>Date the complaint was submitted, recorded into the case journal.</summary>
    public DateOnly? ComplaintSubmitted { get; init; }

    /// <summary>Date the operator answered, recorded into the case journal.</summary>
    public DateOnly? OperatorResponded { get; init; }

    /// <summary>Whether the operator upheld the complaint: <c>--usvojen</c> or <c>--odbijen</c>.</summary>
    public bool? OperatorUpheld { get; init; }

    /// <summary>
    /// Date the request actually reached the Regulator, recorded into the case journal.
    /// <para>
    /// Recorded rather than assumed from the day the letter was generated. Writing a
    /// submission is not filing it, and until this is entered the case rightly shows the
    /// deadline as still outstanding.
    /// </para>
    /// </summary>
    public DateOnly? RegulatorFiled { get; init; }

    /// <summary>The subscriber is not a consumer, so the consumer protection period does not apply.</summary>
    public bool NonConsumer { get; init; }

    /// <summary>The complaint is about the amount charged rather than the quality of the service.</summary>
    public bool BillingComplaint { get; init; }

    /// <summary>The day the disputed invoice fell due, which a billing complaint runs from.</summary>
    public DateOnly? InvoiceDue { get; init; }

    /// <summary>Which recorded outage the complaint is about, where the session has several.</summary>
    public int? IncidentNumber { get; init; }

    /// <summary>Session directory to prepare the regulator submission from.</summary>
    public string? RegulatorDirectory { get; init; }

    /// <summary>Report what the wireless layer can read, then exit.</summary>
    public bool ShowWireless { get; init; }
}

public static class CommandLine
{
    public static bool TryParse(string[] args, out CliSettings settings, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new CliSettings();
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            switch (argument)
            {
                case "-p" or "--pomoc" or "-h" or "--help" or "/?":
                    settings = result with { ShowHelp = true };
                    return true;

                case "-d" or "--detaljno":
                    result = result with { Verbose = true };
                    break;

                case "-t" or "--trajanje":
                    if (!TryTakeValue(args, ref i, out var durationText))
                    {
                        error = "Nedostaje vrednost za --trajanje.";
                        settings = result;
                        return false;
                    }

                    if (!TryParseDuration(durationText, out var duration))
                    {
                        error = $"Nije prepoznato trajanje '{durationText}'. Primeri: 30m, 6h, 48h, 5s, beskonacno.";
                        settings = result;
                        return false;
                    }

                    result = result with { Duration = duration };
                    break;

                case "-i" or "--interfejs":
                    if (!TryTakeValue(args, ref i, out var interfaceName))
                    {
                        error = "Nedostaje vrednost za --interfejs.";
                        settings = result;
                        return false;
                    }

                    result = result with { InterfaceName = interfaceName };
                    break;

                case "-o" or "--izlaz":
                    if (!TryTakeValue(args, ref i, out var outputRoot))
                    {
                        error = "Nedostaje vrednost za --izlaz.";
                        settings = result;
                        return false;
                    }

                    result = result with { OutputRoot = outputRoot };
                    break;

                case "--bez-zapisa":
                    result = result with { NoRecording = true };
                    break;

                case "-g" or "--prigovor":
                    if (!TryTakeValue(args, ref i, out var complaintDirectory))
                    {
                        error = "Nedostaje folder sesije za --prigovor.";
                        settings = result;
                        return false;
                    }

                    result = result with { ComplaintDirectory = complaintDirectory };
                    break;

                case "--operater":
                    if (!TryTakeValue(args, ref i, out var operatorName))
                    {
                        error = "Nedostaje ime operatera za --operater.";
                        settings = result;
                        return false;
                    }

                    result = result with { OperatorName = operatorName };
                    break;

                case "--predmet":
                    result = result with { ShowCase = true };
                    break;

                case "--podnet":
                    if (!TryTakeValue(args, ref i, out var submittedText) ||
                        !TryParseDate(submittedText, out var submitted))
                    {
                        error = "Nedostaje ili nije prepoznat datum za --podnet. Primer: --podnet 12.09.2026.";
                        settings = result;
                        return false;
                    }

                    result = result with { ComplaintSubmitted = submitted };
                    break;

                case "--odgovor":
                    if (!TryTakeValue(args, ref i, out var respondedText) ||
                        !TryParseDate(respondedText, out var responded))
                    {
                        error = "Nedostaje ili nije prepoznat datum za --odgovor. Primer: --odgovor 20.09.2026.";
                        settings = result;
                        return false;
                    }

                    result = result with { OperatorResponded = responded };
                    break;

                case "--prijavljeno":
                    if (!TryTakeValue(args, ref i, out var filedText) ||
                        !TryParseDate(filedText, out var filed))
                    {
                        error = "Nedostaje ili nije prepoznat datum za --prijavljeno. Primer: --prijavljeno 05.10.2026.";
                        settings = result;
                        return false;
                    }

                    result = result with { RegulatorFiled = filed };
                    break;

                case "--pravno-lice":
                    result = result with { NonConsumer = true };
                    break;

                case "--prigovor-na":
                    if (!TryTakeValue(args, ref i, out var kindText) ||
                        kindText is not ("kvalitet" or "racun" or "račun"))
                    {
                        error = "Za --prigovor-na navedite kvalitet ili racun. Primer: --prigovor-na racun";
                        settings = result;
                        return false;
                    }

                    result = result with { BillingComplaint = kindText != "kvalitet" };
                    break;

                case "--dospece-racuna":
                    if (!TryTakeValue(args, ref i, out var invoiceText) ||
                        !TryParseDate(invoiceText, out var invoiceDue))
                    {
                        error = "Nedostaje ili nije prepoznat datum za --dospece-racuna. Primer: --dospece-racuna 01.09.2026.";
                        settings = result;
                        return false;
                    }

                    result = result with { InvoiceDue = invoiceDue };
                    break;

                case "--prekid":
                    if (!TryTakeValue(args, ref i, out var incidentText) ||
                        !int.TryParse(incidentText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var incident) ||
                        incident < 1)
                    {
                        error = "Za --prekid navedite redni broj prekida iz izveštaja. Primer: --prekid 2";
                        settings = result;
                        return false;
                    }

                    result = result with { IncidentNumber = incident };
                    break;

                case "--usvojen":
                    result = result with { OperatorUpheld = true };
                    break;

                case "--odbijen":
                    result = result with { OperatorUpheld = false };
                    break;

                case "--prijava-ratel":
                    if (!TryTakeValue(args, ref i, out var regulatorDirectory))
                    {
                        error = "Nedostaje folder sesije za --prijava-ratel.";
                        settings = result;
                        return false;
                    }

                    result = result with { RegulatorDirectory = regulatorDirectory };
                    break;

                case "--brzina":
                    result = result with { MeasureSpeed = true };

                    // The pair as the contract states it - "100/20" - as well as a lone
                    // download figure. A value that cannot be read is refused rather than
                    // swallowed: it used to be consumed and ignored, so a typo in the one
                    // number the verdict depends on produced a measurement with no verdict
                    // and no complaint about it either.
                    if (TryPeekValue(args, ref i, out var contracted))
                    {
                        if (!ContractedRate.TryParse(contracted, out var download, out var upload))
                        {
                            error = $"Nije prepoznata ugovorena brzina '{contracted}'. Primeri: --brzina 100, --brzina 100/20.";
                            settings = result;
                            return false;
                        }

                        result = result with
                        {
                            ContractedDownloadMbps = download,
                            ContractedUploadMbps = upload ?? result.ContractedUploadMbps,
                        };
                    }

                    break;

                case "--slanje":
                    if (!TryTakeValue(args, ref i, out var uploadText) ||
                        !ContractedRate.TryParse(uploadText, out var uploadMbps, out _) ||
                        uploadMbps is null)
                    {
                        error = "Nedostaje ili nije prepoznata vrednost za --slanje. Primer: --slanje 20";
                        settings = result;
                        return false;
                    }

                    result = result with { ContractedUploadMbps = uploadMbps };
                    break;

                case "--bez-slanja":
                    result = result with { MeasureUpload = false };
                    break;

                case "--zakazi":
                    if (!TryTakeValue(args, ref i, out var delayText))
                    {
                        error = "Nedostaje vrednost za --zakazi. Primeri: 20m, 2h.";
                        settings = result;
                        return false;
                    }

                    if (!TryParseDuration(delayText, out var delay) || delay <= TimeSpan.Zero)
                    {
                        error = $"Nije prepoznato trajanje '{delayText}'. Primeri: 20m, 2h.";
                        settings = result;
                        return false;
                    }

                    result = result with { SpeedStartDelay = delay, SpeedStartDelayRequested = true };
                    break;

                case "--cekaj":
                    if (!TryTakeValue(args, ref i, out var queueText))
                    {
                        error = "Nedostaje vrednost za --cekaj. Primeri: 45s, 10m, 1h, 0.";
                        settings = result;
                        return false;
                    }

                    // The shared parser refuses a zero duration on purpose, but for patience
                    // zero is a meaningful request: check once and refuse if the link is busy.
                    if (queueText.Trim() == "0")
                    {
                        result = result with { SpeedQueueTimeout = TimeSpan.Zero, SpeedQueueRequested = true };
                        break;
                    }

                    if (!TryParseDuration(queueText, out var queue))
                    {
                        error = $"Nije prepoznato trajanje '{queueText}'. Primeri: 45s, 10m, 1h, 0.";
                        settings = result;
                        return false;
                    }

                    // "beskonacno" arrives as the infinite timespan and means exactly that:
                    // stand in the queue until the link goes quiet, however long that takes.
                    result = result with { SpeedQueueTimeout = queue, SpeedQueueRequested = true };
                    break;

                case "--wifi" or "--bezicno":
                    result = result with { ShowWireless = true };
                    break;

                case "--putanja":
                    // The address is optional: with none, the probe targets are used.
                    result = result with
                    {
                        ShowPaths = true,
                        PathTarget = TryPeekValue(args, ref i, out var pathTarget) ? pathTarget : null,
                    };
                    break;

                case "-r" or "--izvestaj":
                    if (!TryTakeValue(args, ref i, out var reportDirectory))
                    {
                        error = "Nedostaje folder sesije za --izvestaj.";
                        settings = result;
                        return false;
                    }

                    result = result with { ReportDirectory = reportDirectory };
                    break;

                case "-v" or "--proveri":
                    if (!TryTakeValue(args, ref i, out var verifyDirectory))
                    {
                        error = "Nedostaje folder sesije za --proveri.";
                        settings = result;
                        return false;
                    }

                    result = result with { VerifyDirectory = verifyDirectory };
                    break;

                default:
                    error = $"Nepoznat parametar '{argument}'.";
                    settings = result;
                    return false;
            }
        }

        // Any of these on their own would silently start a two-day monitoring run instead,
        // which is about the worst possible interpretation of a typo.
        if ((result.SpeedQueueRequested || result.SpeedStartDelayRequested ||
             result.ContractedUploadMbps is not null || !result.MeasureUpload) && !result.MeasureSpeed)
        {
            error = "--cekaj, --zakazi, --slanje i --bez-slanja imaju smisla samo uz --brzina.";
            settings = result;
            return false;
        }

        settings = result;
        return true;
    }

    /// <summary>Accepts 45s, 30m, 6h, 3d, a bare number of minutes, or "beskonacno".</summary>
    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {        duration = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim().ToLowerInvariant();

        if (value is "beskonacno" or "beskonačno" or "do-zaustavljanja")
        {
            duration = Timeout.InfiniteTimeSpan;
            return true;
        }

        var suffix = value[^1];
        var numberText = char.IsDigit(suffix) ? value : value[..^1];

        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0)
        {
            return false;
        }

        duration = suffix switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' or 'č' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ when char.IsDigit(suffix) => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.Zero,
        };

        return duration > TimeSpan.Zero;
    }

    /// <summary>
    /// Accepts the ways a person writes a date here: 12.09.2026, 12.09.2026., 12.9.2026,
    /// or the sortable 2026-09-12.
    /// </summary>
    internal static bool TryParseDate(string value, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();

        return DateOnly.TryParseExact(
                   value, ["d.M.yyyy.", "d.M.yyyy", "yyyy-MM-dd"],
                   CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date) ||
               DateOnly.TryParse(value, SerbianText.Culture, out date);
    }

    /// <summary>Takes the next argument only if it is a value rather than another option.</summary>
    private static bool TryPeekValue(string[] args, ref int index, out string value)
    {
        if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
        {
            value = args[++index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            $"""
            Internet Monitoring {ThisVersion}  -  nadzor kvaliteta internet veze

            Upotreba:
              iem [opcije]

            Opcije:
              -t, --trajanje <vreme>    Trajanje testa. Primeri: 45s, 30m, 6h, 48h, 3d,
                                        ili "beskonacno" za rad do prekida.
                                        Podrazumevano: 48h
              -i, --interfejs <ime>     Ime mrežnog adaptera koji se nadzire.
                                        Podrazumevano: adapter koji nosi podrazumevanu rutu.
              -o, --izlaz <folder>      Gde se snimaju sesije.
                                        Podrazumevano: Radna površina\InternetEvidence
                  --bez-zapisa          Ne snima ništa na disk. Samo za brzu proveru.
              -d, --detaljno            Ispisuje svaki uzorak, ne samo promene stanja.
                  --putanja             Ispisuje kojim adapterom saobraćaj izlazi ka svakoj
                                        meti, pa izlazi. Proverite pre dužeg testa.
                  --brzina [Mbit/s]     Meri brzinu preuzimanja i slanja, i kašnjenje pod
                                        opterećenjem, pa ocenjuje da li merenje može stajati
                                        uz prigovor. Uz ugovorenu brzinu daje i ocenu.
                                        Prima i par, kako piše u ugovoru: --brzina 100/20
                  --slanje <Mbit/s>     Ugovorena brzina slanja, ako nije data uz --brzina.
                                        Primer: --brzina 100 --slanje 20
                  --bez-slanja          Meri samo preuzimanje. Prepolovljuje utrošak podataka,
                                        za veze koje se plaćaju po gigabajtu.
                  --zakazi <vreme>      Koliko pre merenja sačekati, pa tek onda meriti.
                                        Primeri: 20m, 2h. Korisno kad brzina pada u određeno
                                        doba dana - zakažite merenje za to vreme.
                  --cekaj <vreme>       Koliko dugo merenje brzine čeka da veza utihne,
                                        umesto da odmah odustane. Primeri: 45s, 30m, 1h.
                                        Podrazumevano: 10m. Vrednost 0 znači: proveri
                                        i odustani ako je veza zauzeta.
                  --wifi                Ispisuje šta bežični sloj vidi - mreža, signal,
                                        pristupna tačka, vidljive mreže - pa izlazi.
                                        Korisno pre dužeg testa preko Wi-Fi veze.
              -r, --izvestaj <folder>   Ponovo pravi izveštaj za postojeću sesiju.
              -v, --proveri <folder>    Proverava integritet postojeće sesije i izlazi.
              -g, --prigovor <folder>   Priprema prigovor operateru iz postojeće sesije,
                                        sa izračunatim rokovima.
                  --operater <ime>      Ime operatera za prigovor.
                  --predmet             Prikazuje stanje predmeta i rokove iz dnevnika.
                  --podnet <datum>      Beleži dan podnošenja prigovora. Primer: --podnet 12.09.2026.
                  --odgovor <datum>     Beleži dan odgovora operatera. Primer: --odgovor 20.09.2026.
                  --prijavljeno <datum> Beleži dan kada je zahtev stigao Regulatoru.
                  --usvojen             Beleži da je operater usvojio prigovor.
                  --odbijen             Beleži da je operater odbio prigovor.
                  --prekid <broj>       Prekid od kog teku rokovi, kad ih sesija ima više.
                  --prigovor-na <vrsta> kvalitet (podrazumevano) ili racun.
                  --dospece-racuna <d>  Dan dospeća spornog računa, za prigovor na račun.
                  --pravno-lice         Korisnik nije potrošač, pa važe drugi rokovi.
                  --prijava-ratel <folder>  Priprema prijavu RATEL-u iz sesije, kad je
                                        operateru istekao rok ili je prigovor odbijen.
              -p, --pomoc               Prikazuje ovu pomoć.

            Primeri:
              iem -t 5m                 Kratka provera pre pravog testa
              iem -t 48h                Test za prigovor operateru
              iem -i "Wi-Fi" -t 24h     Nadzor konkretnog adaptera
              iem -v "C:\...\Sesija_20260813_182150"
                                        Provera doslednosti lanca otisaka
              iem -r "C:\...\Sesija_20260813_182150"
                                        Ponovna izrada izveštaja

            Napomena: merenje brzine za prigovor operateru vazi samo preko Ethernet kabla
            povezanog direktno na modem. Nadzor prekida radi i preko Wi-Fi mreže.

            O programu:
              {AppInfo.Summary}
              Slobodan softver, {AppInfo.LicenseName} licenca, autor {AppInfo.Author}.

              Izvorni kod i prijava grešaka:  {AppInfo.ProjectUrl}
              Discord:                        {AppInfo.DiscordUrl}
              Portfolio autora:               {AppInfo.PortfolioUrl}
              Mejl:                           {AppInfo.Email}

              {AppInfo.ReportingCaution}
            """);
    }

    private static string ThisVersion =>
        typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
