using IEM.App.Linux.ViewModels;
using IEM.Core;
using IEM.Core.Presentation;
using IEM.Presentation.Hosting;

namespace IEM.App.Linux.Tests;

/// <summary>
/// 3.1-13 gate: a system-service crash remains a service outage in the shell. It must
/// never be represented as a connected service or silently switch to an in-process host.
/// </summary>
public sealed class LinuxShellLifecycleGateTests
{
    [Fact]
    public async Task Service_snapshot_then_connection_loss_becomes_ServiceUnavailable_without_portable_fallback()
    {
        var host = new LifecycleHost();
        await using var viewModel = new LinuxShellViewModel(host);

        Assert.Equal(HostKind.Service, host.Kind);
        Assert.Equal(ServiceConnectionStatus.Connecting, viewModel.ServiceStatus);

        await viewModel.InitializeAsync();
        viewModel.ApplyHostSnapshot(MonitorSnapshot.Empty);

        Assert.Equal(ServiceConnectionStatus.Connected, viewModel.ServiceStatus);
        Assert.Contains("Sistemski servis", viewModel.ModeLabel, StringComparison.Ordinal);

        viewModel.ApplyHostFault("Sistemski servis trenutno nije dostupan.");

        Assert.Equal(ServiceConnectionStatus.ServiceUnavailable, viewModel.ServiceStatus);
        Assert.Contains("nije dostupan", viewModel.ServiceStatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HostKind.Service, host.Kind);
        Assert.False(host.IsRunning);
    }

    private sealed class LifecycleHost : IMonitorHost
    {
        public HostKind Kind => HostKind.Service;
        public bool IsRunning => false;
        public event Action<MonitorSnapshot>? Updated { add { } remove { } }
        public event Action<string?>? FaultChanged { add { } remove { } }

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> StartSessionAsync(
            TimeSpan duration,
            string? interfaceName,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task StopSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }
}
