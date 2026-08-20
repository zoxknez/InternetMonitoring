using System.Globalization;
using System.Runtime.Versioning;
using IEM.Core.Hosting;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Service;
using IEM.Service.Runtime;
using IEM.Storage;
using IEM.Storage.Layout;
using IEM.Windows;
using IEM.Windows.Storage;
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

// Platform adapter registration (Composition Root)
builder.Services.AddSingleton<IPlatformProbeFactory>(WindowsProbeFactory.Instance);
builder.Services.AddSingleton<IPowerEventSource, PowerEventBroker>();
builder.Services.AddSingleton<IPlatformStorageLayout>(WindowsStorageLayout.Instance);
builder.Services.AddSingleton<IStorageProtectionProvider, WindowsSessionAclProvisioner>();
builder.Services.AddSingleton<IPlatformInstallationProbe>(WindowsInstallationProbe.Default);

// Runtime registration
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
/// </summary>
[SupportedOSPlatform("windows")]
static void WarnIfAnotherSessionIsUnfinished(string outputRoot)
{
    var analysis = SessionResumeAnalyzer.Analyze(outputRoot, DateTimeOffset.UtcNow);

    if (analysis.Decision != ResumeDecision.Resumable || analysis.Start is null)
    {
        return;
    }

    Console.WriteLine(
        $"Pažnja: u folderu već postoji nedovršena sesija '{analysis.Start.SessionId}'. " +
        $"Servis će nastaviti tu sesiju pre nego što pokrene novu.");
}

[SupportedOSPlatform("windows")]
static int CancelSession()
{
    var outputRoot = ResolveOutputRoot();
    SessionRequest.Clear(outputRoot);
    Console.WriteLine("Zahtev za sesiju je uklonjen.");
    return 0;
}

[SupportedOSPlatform("windows")]
static int RequestSpeed(string[] args)
{
    var delayText = args.Length > 1 ? args[1] : "3h";

    if (!MonitorSettings.TryParseDuration(delayText, out var delay))
    {
        Console.Error.WriteLine(
            $"Nije prepoznato vreme '{delayText}'. Primeri: 30m, 3h, 24h.");

        return 2;
    }

    var expectedDown = (double?)null;
    var expectedUp = (double?)null;

    if (args.Length > 2 && TryParseSpeedTarget(args[2], out var down, out var up))
    {
        expectedDown = down;
        expectedUp = up;
    }

    var dueAt = DateTimeOffset.UtcNow + delay;
    var request = new SpeedRequest(dueAt, expectedDown, expectedUp, Interface: null);
    var outputRoot = ResolveOutputRoot();

    request.Write(outputRoot);

    Console.WriteLine("Merenje brzine je zakazano.");
    Console.WriteLine($"  Vreme:    {SerbianText.DateTime(dueAt.ToLocalTime())} (za {SerbianText.Duration(delay)})");

    if (expectedDown.HasValue && expectedUp.HasValue)
    {
        Console.WriteLine($"  Ugovor:   {expectedDown:0.#} / {expectedUp:0.#} Mbit/s");
    }

    Console.WriteLine($"  Folder:   {outputRoot}");
    Console.WriteLine();
    Console.WriteLine($"Servis mora raditi u zakazano vreme: sc start {ServiceContract.ServiceName}");
    return 0;
}

[SupportedOSPlatform("windows")]
static int CancelSpeed()
{
    var outputRoot = ResolveOutputRoot();
    SpeedRequest.Clear(outputRoot);
    Console.WriteLine("Zakazano merenje brzine je uklonjeno.");
    return 0;
}

static bool TryParseSpeedTarget(string value, out double down, out double up)
{
    down = 0;
    up = 0;

    var parts = value.Split(['/', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length != 2)
    {
        return false;
    }

    return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out down) &&
           double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out up) &&
           down > 0 && up > 0;
}

static string ResolveOutputRoot()
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var settings = new MonitorSettings();
    configuration.GetSection(MonitorSettings.SectionName).Bind(settings);
    return settings.ResolveOutputRoot(WindowsStorageLayout.Instance.DefaultOutputRoot);
}

static int PrintUsage()
{
    Console.WriteLine(
        $"""
        Monitor internet dokaza - servis

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

    builder.Services.AddSingleton<IHostLifetime, IemWindowsServiceLifetime>();
    builder.Logging.AddEventLog(settings => settings.SourceName = ServiceContract.ServiceName);
}
