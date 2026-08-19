namespace IEM.Core.Ipc;

/// <summary>
/// Authoritative resolver and repository for active session owners (Invariants 84, 91, 94).
/// After a successful StartSession, the owner is immutably recorded as scheme:principalId.
/// </summary>
public interface ISessionOwnerResolver
{
    string? GetSessionOwner(string? sessionId = null);
    void RecordSessionOwner(string sessionId, string ownerPrincipalRef);
    void ClearSession(string? sessionId = null);
}

/// <summary>
/// Thread-safe in-memory session owner repository.
/// </summary>
public sealed class InMemorySessionOwnerResolver : ISessionOwnerResolver
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _sessionOwners = new(StringComparer.Ordinal);
    private string? _activeSessionId;

    public string? GetSessionOwner(string? sessionId = null)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && _sessionOwners.TryGetValue(sessionId, out var owner))
            {
                return owner;
            }

            if (!string.IsNullOrWhiteSpace(_activeSessionId) && _sessionOwners.TryGetValue(_activeSessionId, out var activeOwner))
            {
                return activeOwner;
            }

            return null;
        }
    }

    public void RecordSessionOwner(string sessionId, string ownerPrincipalRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalRef);

        lock (_lock)
        {
            _activeSessionId = sessionId;
            _sessionOwners[sessionId] = ownerPrincipalRef;
        }
    }

    public void ClearSession(string? sessionId = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId == _activeSessionId)
            {
                _activeSessionId = null;
            }
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _sessionOwners.Remove(sessionId);
            }
        }
    }
}
