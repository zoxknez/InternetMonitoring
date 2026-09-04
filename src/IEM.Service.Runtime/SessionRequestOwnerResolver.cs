using IEM.Core.Ipc;
using IEM.Storage;
using IEM.Storage.Layout;
using Microsoft.Extensions.Options;

namespace IEM.Service.Runtime;

/// <summary>
/// Restores the authenticated owner of an open session after a service restart from the
/// same durable request that carries the session intent. The in-memory resolver remains the
/// fast path; a client-supplied owner is never consulted.
/// </summary>
public sealed class SessionRequestOwnerResolver(
    IOptions<MonitorSettings> settings,
    IPlatformStorageLayout storageLayout) : ISessionOwnerResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly string _outputRoot = settings.Value.ResolveOutputRoot(storageLayout.DefaultOutputRoot);
    private string? _activeSessionId;

    public string? GetSessionOwner(string? sessionId = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && _owners.TryGetValue(sessionId, out var exactOwner))
            {
                return exactOwner;
            }

            if (string.IsNullOrWhiteSpace(sessionId) &&
                !string.IsNullOrWhiteSpace(_activeSessionId) &&
                _owners.TryGetValue(_activeSessionId, out var activeOwner))
            {
                return activeOwner;
            }
        }

        var request = SessionRequest.Read(_outputRoot);
        if (request is null || string.IsNullOrWhiteSpace(request.OwnerPrincipalRef))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(sessionId) &&
            !string.Equals(sessionId, request.SessionId, StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            RecordSessionOwner(request.SessionId, request.OwnerPrincipalRef);
        }

        return request.OwnerPrincipalRef;
    }

    public void RecordSessionOwner(string sessionId, string ownerPrincipalRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalRef);

        lock (_gate)
        {
            _activeSessionId = sessionId;
            _owners[sessionId] = ownerPrincipalRef;
        }
    }

    public void ClearSession(string? sessionId = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                _activeSessionId = null;
                return;
            }

            _owners.Remove(sessionId);
            if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                _activeSessionId = null;
            }
        }
    }
}
