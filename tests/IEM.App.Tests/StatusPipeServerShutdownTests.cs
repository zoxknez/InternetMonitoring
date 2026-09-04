using System.Runtime.Versioning;
using IEM.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace IEM.App.Tests;

/// <summary>
/// Regression test for StatusPipeServer shutdown cancellation handling.
/// Directly executes StatusPipeServer.ListenAsync (non-owner branch) to guarantee that SCM shutdown
/// cancellation while waiting for security descriptor completes gracefully without faulting tasks.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StatusPipeServerShutdownTests
{
    [Fact]
    public async Task StatusPipeServer_NonOwner_ShutdownCancellation_MustCompleteGracefully()
    {
        // Instanciramo produkcioni StatusPipeServer
        var server = new StatusPipeServer(null!, null!, NullLogger<StatusPipeServer>.Instance);
        using var cts = new CancellationTokenSource();

        // Pokrecemo stvarni internal ListenAsync metod sa ownsSecurity = false
        // Listener ceka na _securityEstablished.Task.WaitAsync(stoppingToken)
        var listenTask = server.ListenAsync(ownsSecurity: false, cts.Token);

        // Simuliramo SCM shutdown cancellation pre nego sto je security postavljen
        cts.Cancel();

        // Metoda MORA cisto da se zavrsi zahvaljujuci try/catch(OperationCanceledException) bloku
        await listenTask;

        Assert.True(listenTask.IsCompletedSuccessfully);
    }
}
