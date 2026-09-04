using IEM.Core;
using IEM.Presentation.Hosting;

namespace IEM.App.Linux.Hosting;

internal sealed class UnavailableMonitorHost(string message) : IMonitorHost
{
    public HostKind Kind => HostKind.Service;
    public bool IsRunning => false;
    public event Action<MonitorSnapshot>? Updated
    {
        add { }
        remove { }
    }
    public event Action<string?>? FaultChanged;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        FaultChanged?.Invoke(message);
        return Task.CompletedTask;
    }

    public Task<bool> StartSessionAsync(
        TimeSpan duration,
        string? interfaceName,
        CancellationToken cancellationToken)
    {
        FaultChanged?.Invoke(message);
        return Task.FromResult(false);
    }

    public Task StopSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
