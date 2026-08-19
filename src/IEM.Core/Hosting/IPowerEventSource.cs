namespace IEM.Core.Hosting;

/// <summary>
/// Source of system suspend and resume notifications from the host platform.
/// Platform-neutral contract allowing MonitorWorker to observe suspend events
/// without direct Win32 SystemEvents or Linux logind D-Bus dependencies.
/// </summary>
public interface IPowerEventSource : IDisposable
{
    /// <summary>Registers a callback invoked when the host system is suspending.</summary>
    IDisposable OnSuspending(Action callback);
}
