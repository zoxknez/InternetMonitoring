using IEM.Core;

namespace IEM.Presentation.Hosting;

/// <summary>
/// Operational mode of the monitor host.
/// Invariant 275: Both Service and InProcess hosts expose the same contract and use the same runtime engine.
/// </summary>
public enum HostKind
{
    /// <summary>Attached to the background/system service. Monitoring outlives this window/process.</summary>
    Service,

    /// <summary>Running the engine inside this process. Monitoring stops when the app does.</summary>
    InProcess,
}

/// <summary>
/// Presentation-to-host interface contract.
/// Abstracts the monitoring backend (system service vs in-process engine) for UI presentation layers.
/// Invariant 275: Both Service and InProcess hosts expose the same contract and use the same runtime engine.
/// </summary>
public interface IMonitorHost : IAsyncDisposable
{
    HostKind Kind { get; }

    /// <summary>True while a session is actually running.</summary>
    bool IsRunning { get; }

    /// <summary>Raised whenever new measurement snapshots arrive.</summary>
    event Action<MonitorSnapshot>? Updated;

    /// <summary>Raised when the host loses or regains contact with its source.</summary>
    event Action<string?>? FaultChanged;

    /// <summary>Begins watching. For the service host this attaches; it does not start a session.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Asks for a session to begin.</summary>
    Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken);

    /// <summary>Ends the running session.</summary>
    Task StopSessionAsync(CancellationToken cancellationToken);
}
