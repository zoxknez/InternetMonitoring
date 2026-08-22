using System.Globalization;
using IEM.Core.Hosting;
using IEM.Core.Ipc;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Service.Linux.Composition;
using IEM.Service.Linux.Ipc;
using IEM.Service.Linux.Lifecycle;
using IEM.Service.Linux.Storage;
using IEM.Service.Runtime;
using IEM.Storage.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Serbian culture as standard across all platforms
CultureInfo.DefaultThreadCurrentCulture = SerbianText.Culture;

ILogger? logger = null;

try
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    // systemd integration: registers SystemdLifetime, Type=notify notifications, and journal logging
    builder.Services.AddSystemd();
    builder.Services.AddHostedService<LinuxSystemdNotifier>();

    // Platform adapter registration (Linux Composition Root: Invariants 8E-D, 8E-K)
    builder.Services.AddLinuxSystemServices();
    builder.Services.AddSingleton<LinuxLogindPowerSource>();
    builder.Services.AddSingleton<IPowerEventSource>(sp => sp.GetRequiredService<LinuxLogindPowerSource>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LinuxLogindPowerSource>());

    // Runtime engine workers reuse from IEM.Service.Runtime
    builder.Services.Configure<MonitorSettings>(builder.Configuration.GetSection(MonitorSettings.SectionName));
    builder.Services.AddSingleton<MonitorWorker>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<MonitorWorker>());
    builder.Services.AddSingleton<SpeedWorker>();
    builder.Services.AddHostedService(provider => provider.GetRequiredService<SpeedWorker>());

    // Unix IPC Transport & Command Dispatcher (§11 / Phase 3.1-3)
    builder.Services.AddSingleton<ISessionOwnerResolver, InMemorySessionOwnerResolver>();
    builder.Services.AddSingleton<IIpcTransport, LinuxUnixDomainSocketTransport>();
    builder.Services.AddSingleton<IpcCommandDispatcher>(sp => LinuxIpcDispatcherFactory.Create(
        sp.GetRequiredService<MonitorWorker>(),
        sp.GetRequiredService<SpeedWorker>(),
        sp.GetRequiredService<ISessionOwnerResolver>()));
    builder.Services.AddHostedService<LinuxIpcHostedService>();

    var host = builder.Build();

    // Pre-validate RuntimeDirectory prior to future socket listener
    logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger("IEM.Service.Linux.Bootstrap");
    var runtimePrep = LinuxRuntimeDirectoryPreparer.Prepare(LinuxSystemStorageLayout.DefaultSystemRuntimeDir, posix: null, logger);

    if (!runtimePrep.IsValid)
    {
        Console.Error.WriteLine($"[FATAL] Pre-flight greška: Validacija RuntimeDirectory nije uspela: {runtimePrep.Error}");
        logger?.LogCritical("Pre-flight greška: Validacija RuntimeDirectory nije uspela: {Error}", runtimePrep.Error);
        return MonitorWorker.FatalExitCode;
    }

    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[FATAL] Fatalna greška tokom pokretanja/izvršavanja Linux servisa: {ex.Message}");
    logger?.LogCritical(ex, "Fatalna greška tokom izvršavanja Linux servisa.");
    return MonitorWorker.FatalExitCode;
}
