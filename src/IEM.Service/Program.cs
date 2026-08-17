using System.Globalization;
using System.Runtime.Versioning;
using IEM.Core.Presentation;
using IEM.Service;
using IEM.Storage;
using Microsoft.Extensions.Hosting.WindowsServices;

// Serbian regardless of the language of the machine's Windows. A service runs without a
// user profile, so it would otherwise inherit whatever the system account happens to be
// set to - and a date written as 8/13/2026 in an evidence log is a defect.
CultureInfo.DefaultThreadCurrentCulture = SerbianText.Culture;
CultureInfo.DefaultThreadCurrentUICulture = SerbianText.Culture;

// Management verbs are handled before the host is built. Registering a service should not
// require the service to start monitoring first.
if (args.Length > 0 && OperatingSystem.IsWindows())
{
    var handled = HandleManagementVerb(args);
    if (handled.HasValue)
    {
        return handled.Value;
    }
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<MonitorSettings>(builder.Configuration.GetSection(MonitorSettings.SectionName));
builder.Services.AddSingleton<PowerEventBroker>();
builder.Services.AddSingleton<MonitorWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MonitorWorker>());
builder.Services.AddSingleton<SpeedWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SpeedWorker>());
builder.Services.AddHostedService<StatusPipeServer>();

if (OperatingSystem.IsWindows())
{
    ConfigureWindowsService(builder);
}

var host = builder.Build();
await host.RunAsync();

// Whatever the engine set, not a hard-coded success.
//
// This line used to be `return 0`, which quietly undid the whole failure path: the worker
// caught a fatal error, logged it, set a non-zero exit code so the service manager would
// restart it - and then the process returned zero anyway. The manager saw a clean stop,
// the recovery action never fired, and a two-day test would sit dead with nothing to
// bring it back.
return Environment.ExitCode;

/// <summary>
/// Returns an exit code when the arguments name a management verb, or null to carry on
/// and run the service.
/// </summary>
[SupportedOSPlatform("windows")]
static int? HandleManagementVerb(string[] args) => args[0].ToLowerInvariant() switch
{
    "install" => ServiceInstaller.Install(ResolveOutputRoot()),
    "uninstall" or "remove" => ServiceInstaller.Uninstall(),
    "start-session" or "zapocni" => RequestSession(args),
    "cancel-session" or "otkazi" => CancelSession(),
    "schedule-speed" or "zakazi-merenje" => RequestSpeed(args),
    "cancel-speed" or "otkazi-merenje" => CancelSpeed(),
    "--pomoc" or "-p" or "--help" or "-h" or "/?" => PrintUsage(),
    _ => null,
};

/// <summary>
/// Records that a session is wanted. The service picks this up on its next start and
/// keeps honouring it across reboots until the session reaches its planned end.
/// </summary>
[SupportedOSPlatform("windows")]
static int RequestSession(string[] args)
{
    var durationText = args.Length > 1 ? args[1] : "48h";

    if (!MonitorSettings.TryParseDuration(durationText, out var duration))
    {
        Console.Error.WriteLine(
            $"Nije prepoznato trajanje '{durationText}'. Primeri: 90m, 6h, 48h, 7d, beskonacno.");

        return 2;
    }

    var interfaceName = args.Length > 2 ? args[2] : null;
    var outputRoot = ResolveOutputRoot();

    new SessionRequest(duration, interfaceName, DateTimeOffset.UtcNow).Write(outputRoot);

    Console.WriteLine("Sesija je zatražena.");
    Console.WriteLine($"  Trajanje: {(duration == Timeout.InfiniteTimeSpan ? "do prekida" : SerbianText.Duration(duration))}");
    Console.WriteLine($"  Adapter:  {interfaceName ?? "automatski"}");
    Console.WriteLine($"  Folder:   {outputRoot}");
    Console.WriteLine();

    WarnIfAnotherSessionIsUnfinished(outputRoot);

    Console.WriteLine($"Pokrenite servis: sc start {ServiceContract.ServiceName}");
    return 0;
}

/// <summary>
/// Says so when an unfinished session will take precedence over the one just requested.
/// <para>
/// An interrupted two-day test is deliberately continued rather than discarded, which is
/// right - but it means a new request does not start now, and staying silent about that is
/// the problem. Someone asks for a fresh test, sees "session requested", and watches a
/// recording they did not ask for, wondering why the duration is wrong.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
static void WarnIfAnotherSessionIsUnfinished(string outputRoot)
{
    var analysis = SessionResumeAnalyzer.Analyze(outputRoot, DateTimeOffset.UtcNow);

    if (analysis.Decision != ResumeDecision.Resumable || analysis.Start is null)
    {
        return;
    }

    var previous = Console.ForegroundColor;
    Console.ForegroundColor = ConsoleColor.Yellow;

    Console.WriteLine("PAŽNJA: postoji nezavršena sesija koja se nastavlja prva.");
    Console.WriteLine();
    Console.WriteLine($"  Sesija:    {analysis.Start.SessionId}");
    Console.WriteLine($"  Folder:    {analysis.Paths?.Directory}");
    Console.WriteLine($"  Preostalo: {(analysis.Remaining == Timeout.InfiniteTimeSpan ? "do prekida" : SerbianText.Duration(analysis.Remaining))}");
    Console.WriteLine();
    Console.WriteLine("  Zatražena sesija počinje tek kada se ta završi. Ako želite da");
    Console.WriteLine("  odmah počnete novu, prvo zatvorite postojeću.");
    Console.WriteLine();

    Console.ForegroundColor = previous;
}

/// <summary>
/// Records that a speed measurement is wanted at a given moment.
/// <para>
/// The instruction outlives whoever typed it: a measurement scheduled inside a window dies
/// with the window, and one scheduled in a console needs the console left open - which makes
/// the one case scheduling exists for, three in the morning, the one it could not serve.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
static int RequestSpeed(string[] args)
{
    var delayText = args.Length > 1 ? args[1] : null;

    if (delayText is null || !MonitorSettings.TryParseDuration(delayText, out var delay) ||
        delay == Timeout.InfiniteTimeSpan)
    {
        Console.Error.WriteLine(
            "Nedostaje ili nije prepoznato vreme. Primeri: zakazi-merenje 3h, zakazi-merenje 90m 100/20");

        return 2;
    }

    if (!IEM.Core.Speed.ContractedRate.TryParse(
            args.Length > 2 ? args[2] : null, out var download, out var upload))
    {
        Console.Error.WriteLine(
            "Ugovorena brzina nije prepoznata. Upišite broj, ili par: 100/20 (preuzimanje/slanje).");

        return 2;
    }

    var outputRoot = ResolveOutputRoot();
    var due = DateTimeOffset.UtcNow + delay;

    new SpeedRequest(due, download, upload).Write(outputRoot);

    Console.WriteLine("Merenje brzine je zakazano.");
    Console.WriteLine($"  Vreme:     {SerbianText.DateTime(due.ToLocalTime())} (za {SerbianText.Duration(delay)})");
    Console.WriteLine($"  Ugovoreno: {IEM.Core.Speed.ContractedRate.Describe(download, upload)}");
    Console.WriteLine($"  Folder:    {outputRoot}");
    Console.WriteLine();
    Console.WriteLine("Merenje izvršava servis, pa prozor i konzola mogu biti zatvoreni.");
    Console.WriteLine($"Servis mora raditi u zakazano vreme: sc start {ServiceContract.ServiceName}");
    Console.WriteLine();
    Console.WriteLine("Ako veza u to vreme bude zauzeta, čeka se do deset minuta da utihne, pa se");
    Console.WriteLine("merenje odbija - izmerena bi bila preostala brzina, ne raspoloživa.");
    return 0;
}

[SupportedOSPlatform("windows")]
static int CancelSpeed()
{
    SpeedRequest.Clear(ResolveOutputRoot());

    Console.WriteLine("Zakazano merenje brzine je otkazano.");
    return 0;
}

[SupportedOSPlatform("windows")]
static int CancelSession()
{
    var outputRoot = ResolveOutputRoot();
    SessionRequest.Clear(outputRoot);

    Console.WriteLine("Zahtev za sesiju je uklonjen. Sesija u toku se ne prekida ovom komandom;");
    Console.WriteLine($"za to zaustavite servis: sc stop {ServiceContract.ServiceName}");
    return 0;
}

[SupportedOSPlatform("windows")]
static string ResolveOutputRoot()
{
    // Built exactly like the host builds it, environment variables included. Reading only
    // the JSON file here would let a machine configured through the environment write its
    // session request into one folder while the service looked for it in another - and the
    // requested test would simply never start, with nothing obviously wrong.
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var settings = new MonitorSettings();
    configuration.GetSection(MonitorSettings.SectionName).Bind(settings);
    return settings.ResolveOutputRoot();
}

static int PrintUsage()
{
    Console.WriteLine(
        $"""
        Internet Monitoring - servis

        Bez argumenata pokreće nadzor. Kao Windows servis radi u pozadini, bez
        otvorenog prozora, i nastavlja prekinutu sesiju nakon restarta.

        Komande:
          install                       Registruje servis (zahteva administratorska prava).
          uninstall                     Uklanja servis. Snimljene sesije ostaju.
          start-session [trajanje] [adapter]
                                        Zahteva sesiju. Podrazumevano 48h.
          cancel-session                Uklanja zahtev za sesiju.
          zakazi-merenje <vreme> [Mbit/s]
                                        Zakazuje merenje brzine za dato vreme unapred.
                                        Merenje izvršava servis, bez otvorenog prozora.
                                        Primer: zakazi-merenje 3h 100/20
          otkazi-merenje                Uklanja zakazano merenje brzine.
          --pomoc                       Prikazuje ovu pomoć.

        Uobičajen redosled:
          InternetEvidenceService.exe install
          InternetEvidenceService.exe start-session 48h
          sc start {ServiceContract.ServiceName}

        Servis radi dok se sesija ne završi, pa se sam zaustavlja i pravi izveštaj.
        Ako se računar u međuvremenu restartuje, sesija se nastavlja tamo gde je stala.
        Prekid nadzora se beleži kao pauza, nikada kao prekid internet veze.

        Podešavanja se nalaze u appsettings.json, u sekciji "Monitor":
          Duration                 podrazumevano trajanje za AutoStart
          Interface                ime adaptera; prazno znači automatski
          OutputRoot               gde se snimaju sesije; prazno znači ProgramData
          ResumeUnfinished         nastavi prekinutu sesiju pri pokretanju
          BuildReportOnCompletion  napravi izveštaj kad se sesija završi
          AutoStart                pokreni sesiju i bez zahteva (podrazumevano isključeno)
        """);

    return 0;
}

[SupportedOSPlatform("windows")]
static void ConfigureWindowsService(HostApplicationBuilder builder)
{
    builder.Services.AddWindowsService(options => options.ServiceName = ServiceContract.ServiceName);

    if (!WindowsServiceHelpers.IsWindowsService())
    {
        return;
    }

    // Replace the lifetime AddWindowsService just registered with the one that also
    // listens for power events. Registered after it so this is the resolved instance.
    builder.Services.AddSingleton<IHostLifetime, IemWindowsServiceLifetime>();
    builder.Logging.AddEventLog(settings => settings.SourceName = ServiceContract.ServiceName);
}
