using IEM.App.Hosting;
using IEM.Core;

namespace IEM.App.Tests;

/// <summary>
/// A monitoring host the test drives by hand: no network, no service, no engine.
/// <para>
/// The window is supposed to be a view onto monitoring that happens elsewhere, so this is
/// what "elsewhere" looks like when nothing is actually running. It records what the window
/// asked for and pushes back whatever the test wants the window to see.
/// </para>
/// </summary>
internal sealed class StubMonitorHost(HostKind kind = HostKind.InProcess) : IMonitorHost
{
    public HostKind Kind { get; } = kind;

    public bool IsRunning { get; private set; }

    /// <summary>What the next <see cref="StartSessionAsync"/> answers.</summary>
    public bool StartSucceeds { get; set; } = true;

    public TimeSpan? RequestedDuration { get; private set; }

    public int StopsRequested { get; private set; }

    public bool Connected { get; private set; }

    public bool Disposed { get; private set; }

    public event Action<MonitorSnapshot>? Updated;

    public event Action<string?>? FaultChanged;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        Connected = true;
        return Task.CompletedTask;
    }

    public Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken)
    {
        RequestedDuration = duration;
        IsRunning = StartSucceeds;
        return Task.FromResult(StartSucceeds);
    }

    public Task StopSessionAsync(CancellationToken cancellationToken)
    {
        StopsRequested++;
        IsRunning = false;
        return Task.CompletedTask;
    }

    /// <summary>Hands the window a new reading, as the real host does each second.</summary>
    public void Push(MonitorSnapshot snapshot) => Updated?.Invoke(snapshot);

    /// <summary>Tells the window contact with the source was lost, or regained.</summary>
    public void PushFault(string? fault) => FaultChanged?.Invoke(fault);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
