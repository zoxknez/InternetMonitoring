using IEM.Core;
using IEM.Presentation.Hosting;

namespace IEM.App.Hosting;

/// <summary>
/// Host used when the service is installed on the machine but unreachable over the IPC transport,
/// or when installation state is ambiguous.
/// Invariant 276: INSTALLED_UNREACHABLE_NEVER_FALLS_BACK_TO_PORTABLE.
/// Never creates in-process sessions or falls back silently to portable storage.
/// </summary>
public sealed class ServiceUnavailableMonitorHost : IMonitorHost
{
    private readonly string _outputRoot;
    private readonly string _faultMessage;

    public ServiceUnavailableMonitorHost(string outputRoot, string faultMessage)
    {
        _outputRoot = outputRoot;
        _faultMessage = faultMessage;
    }

    public HostKind Kind => HostKind.Service;

    public bool IsRunning => false;

#pragma warning disable CS0067
    public event Action<MonitorSnapshot>? Updated;
#pragma warning restore CS0067

    public event Action<string?>? FaultChanged;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        FaultChanged?.Invoke(_faultMessage);
        return Task.CompletedTask;
    }

    public Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken)
    {
        FaultChanged?.Invoke(_faultMessage);
        return Task.FromResult(false);
    }

    public Task StopSessionAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
