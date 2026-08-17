using IEM.Core;

namespace IEM.App.Hosting;

public enum HostKind
{
    /// <summary>Attached to the Windows service. Monitoring outlives this window.</summary>
    Service,

    /// <summary>Running the engine inside this process. Monitoring stops when the app does.</summary>
    InProcess,
}

/// <summary>
/// Where the measurements come from.
/// <para>
/// Two implementations exist because there are two honest ways to use this application.
/// Someone preparing a complaint installs the service, so a two-day test survives reboots
/// and a closed window. Someone who just wants to look at their connection for ten minutes
/// should not have to install anything at all. The interface is identical either way, and
/// the difference is stated plainly in the window rather than hidden.
/// </para>
/// </summary>
public interface IMonitorHost : IAsyncDisposable
{
    HostKind Kind { get; }

    /// <summary>True while a session is actually running.</summary>
    bool IsRunning { get; }

    /// <summary>Raised on the UI thread whenever new measurements arrive.</summary>
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
