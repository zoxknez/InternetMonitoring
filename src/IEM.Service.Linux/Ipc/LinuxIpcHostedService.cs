using IEM.Core.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IEM.Service.Linux.Ipc;

/// <summary>
/// Background service that hosts the Linux IPC transport listener and dispatches requests.
/// </summary>
public sealed class LinuxIpcHostedService(
    IIpcTransport transport,
    IpcCommandDispatcher dispatcher,
    ILogger<LinuxIpcHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Pokretanje Linux IPC servisa na transportu '{TransportName}'...", transport.TransportName);

        try
        {
            await transport.RunAsync(async (context, ct) =>
            {
                await dispatcher.ProcessConnectionAsync(context, ct).ConfigureAwait(false);
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Greška tokom rada Linux IPC servisa.");
        }
    }
}
