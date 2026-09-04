using IEM.Core;

namespace IEM.Service.Runtime;

/// <summary>
/// Result of a control-plane request delivered to the long-running monitor worker.
/// The IPC layer maps these explicit outcomes to protocol statuses instead of claiming
/// success before the worker has accepted the request.
/// </summary>
public enum SessionControlOutcome
{
    Accepted,
    Conflict,
    NotFound,
    InvalidRequest,
}

public sealed record SessionControlResult(
    SessionControlOutcome Outcome,
    string? SessionId,
    string Message)
{
    public bool Accepted => Outcome == SessionControlOutcome.Accepted;
}

/// <summary>
/// Platform-neutral control surface implemented by <see cref="MonitorWorker"/>.
/// A host-specific transport authenticates and authorizes the caller before invoking it.
/// </summary>
public interface IMonitorSessionController
{
    ServiceStatus Status { get; }

    MonitorSnapshot Live { get; }

    Task<SessionControlResult> StartSessionAsync(
        string sessionId,
        TimeSpan duration,
        string? interfaceName,
        string ownerPrincipalRef,
        CancellationToken cancellationToken);

    Task<SessionControlResult> PauseSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken);

    Task<SessionControlResult> FinalizeSessionAsync(
        string? sessionId,
        CancellationToken cancellationToken);
}
