using System.Globalization;
using IEM.Core.Hosting;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Service.Linux.Lifecycle;
using IEM.Service.Linux.Storage;
using IEM.Service.Runtime;
using IEM.Storage.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Serbian culture as standard across all platforms
CultureInfo.DefaultThreadCurrentCulture = SerbianText.Culture;
CultureInfo.DefaultThreadCurrentUICulture = SerbianText.Culture;

var builder = Host.CreateApplicationBuilder(args);

// systemd integration: registers SystemdLifetime, Type=notify notifications, and journal logging
builder.Services.AddSystemd();
builder.Services.AddHostedService<LinuxSystemdNotifier>();

// Platform adapter registration (Linux Composition Root)
builder.Services.AddSingleton<IPlatformProbeFactory>(LinuxProbeFactoryBaseline.Instance);
builder.Services.AddSingleton<IPowerEventSource>(LinuxPowerEventSourceStub.Instance);
builder.Services.AddSingleton<IPlatformStorageLayout>(LinuxSystemStorageLayout.Instance);

// Runtime engine workers reuse from IEM.Service.Runtime
builder.Services.Configure<MonitorSettings>(builder.Configuration.GetSection(MonitorSettings.SectionName));
builder.Services.AddSingleton<MonitorWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MonitorWorker>());
builder.Services.AddSingleton<SpeedWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SpeedWorker>());

var host = builder.Build();

// Pre-validate RuntimeDirectory prior to future socket listener
var logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger("IEM.Service.Linux.Bootstrap");
var runtimePrep = LinuxRuntimeDirectoryPreparer.Prepare(LinuxSystemStorageLayout.DefaultSystemRuntimeDir, posix: null, logger);

if (!runtimePrep.IsValid)
{
    Console.Error.WriteLine($"[FATAL] Pre-flight greška: Validacija RuntimeDirectory nije uspela: {runtimePrep.Error}");
    logger?.LogCritical("Pre-flight greška: Validacija RuntimeDirectory nije uspela: {Error}", runtimePrep.Error);
    return MonitorWorker.FatalExitCode;
}

try
{
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception ex)
{
    logger?.LogCritical(ex, "Fatalna greška tokom izvršavanja Linux servisa.");
    return MonitorWorker.FatalExitCode;
}
