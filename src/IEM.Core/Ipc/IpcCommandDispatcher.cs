using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace IEM.Core.Ipc;

public delegate Task<IpcResponseEnvelope> CommandHandlerDelegate(
    IpcRequestEnvelope request,
    PlatformPeerIdentity peerIdentity,
    CancellationToken cancellationToken);

/// <summary>
/// Authoritative command dispatcher and boundary enforcement for IPC requests.
/// Invariants 83-96.
/// </summary>
public sealed class IpcCommandDispatcher
{
    private readonly string _serviceInstanceId;
    private readonly IpcAuthorizationPolicy _authPolicy;
    private readonly ISessionOwnerResolver _sessionOwnerResolver;
    private readonly ConcurrentDictionary<string, CommandHandlerDelegate> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IpcResponseEnvelope> _idempotencyCache = new();
    private readonly List<ControlCommandObserved> _auditLog = new();
    private readonly object _auditLock = new();

    private static readonly HashSet<string> AllowlistedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetServiceStatus",
        "GetActiveSession",
        "GetSessionStatus",
        "StartSession",
        "StopSession",
        "FinalizeSession",
        "RetryTimestamp",
        "CreateExport",
    };

    private static readonly HashSet<string> StateChangingCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartSession",
        "StopSession",
        "FinalizeSession",
        "RetryTimestamp",
    };

    public IpcCommandDispatcher(
        string serviceInstanceId,
        IpcAuthorizationPolicy? authPolicy = null,
        ISessionOwnerResolver? sessionOwnerResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceInstanceId);
        _serviceInstanceId = serviceInstanceId;
        _authPolicy = authPolicy ?? IpcAuthorizationPolicy.Default;
        _sessionOwnerResolver = sessionOwnerResolver ?? new InMemorySessionOwnerResolver();
    }

    public string ServiceInstanceId => _serviceInstanceId;
    public ISessionOwnerResolver SessionOwnerResolver => _sessionOwnerResolver;
    public IReadOnlyList<ControlCommandObserved> AuditLog
    {
        get
        {
            lock (_auditLock)
            {
                return _auditLog.ToList().AsReadOnly();
            }
        }
    }

    public void RegisterHandler(string commandName, CommandHandlerDelegate handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(handler);

        if (!AllowlistedCommands.Contains(commandName))
        {
            throw new InvalidOperationException($"Komanda '{commandName}' nije na eksplicitnoj listi dozvoljenih IPC komandi (Invariant 89).");
        }

        _handlers[commandName] = handler;
    }

    public async Task ProcessConnectionAsync(
        IpcConnectionContext context,
        string? sessionOwnerPrincipalRef = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] frameBytes;
            try
            {
                frameBytes = await IpcMessageFraming.ReadFrameAsync(context.Input, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var errorResponse = IpcResponseEnvelope.CreateError(
                    requestId: Guid.NewGuid().ToString("N"),
                    serviceInstanceId: _serviceInstanceId,
                    status: IpcResponseStatus.InvalidRequest,
                    errorCode: "MALFORMED_FRAME",
                    errorMessage: ex.Message);

                var errBytes = JsonSerializer.SerializeToUtf8Bytes(errorResponse);
                await IpcMessageFraming.WriteFrameAsync(context.Output, errBytes, cancellationToken).ConfigureAwait(false);
                break;
            }

            var response = await DispatchFrameAsync(frameBytes, context.PeerIdentity, sessionOwnerPrincipalRef, cancellationToken).ConfigureAwait(false);
            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response);
            await IpcMessageFraming.WriteFrameAsync(context.Output, responseBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ProcessConnectionAsync(
        IpcConnectionContext context,
        CancellationToken cancellationToken) =>
        ProcessConnectionAsync(context, sessionOwnerPrincipalRef: null, cancellationToken);

    public async Task<IpcResponseEnvelope> DispatchFrameAsync(
        byte[] frameBytes,
        PlatformPeerIdentity peerIdentity,
        string? sessionOwnerPrincipalRef = null,
        CancellationToken cancellationToken = default)
    {
        var receivedAtUtc = DateTimeOffset.UtcNow;
        IpcRequestEnvelope? request;

        try
        {
            request = JsonSerializer.Deserialize<IpcRequestEnvelope>(frameBytes);
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId) || string.IsNullOrWhiteSpace(request.CommandName))
            {
                return IpcResponseEnvelope.CreateError(
                    requestId: Guid.NewGuid().ToString("N"),
                    serviceInstanceId: _serviceInstanceId,
                    status: IpcResponseStatus.InvalidRequest,
                    errorCode: "INVALID_REQUEST_ENVELOPE",
                    errorMessage: "Zahtev ne sadrži obavezna polja (RequestId, CommandName).");
            }
        }
        catch (Exception ex)
        {
            return IpcResponseEnvelope.CreateError(
                requestId: Guid.NewGuid().ToString("N"),
                serviceInstanceId: _serviceInstanceId,
                status: IpcResponseStatus.InvalidRequest,
                errorCode: "JSON_DESERIALIZATION_ERROR",
                errorMessage: $"Nevalidan JSON format poruke: {ex.Message}");
        }

        // 1. Invariant 86: UNKNOWN_IPC_PROTOCOL_VERSION_IS_NEVER_SILENTLY_DOWNGRADED
        if (request.ProtocolVersion != IpcRequestEnvelope.CurrentProtocolVersion)
        {
            return IpcResponseEnvelope.CreateError(
                requestId: request.RequestId,
                serviceInstanceId: _serviceInstanceId,
                status: IpcResponseStatus.UnsupportedProtocol,
                errorCode: "UNSUPPORTED_PROTOCOL_VERSION",
                errorMessage: $"Protokol verzija {request.ProtocolVersion} nije podržana (očekivana verzija: {IpcRequestEnvelope.CurrentProtocolVersion}).");
        }

        // 2. Invariant 87: UNKNOWN_COMMAND_IS_REJECTED_NOT_GUESSED
        //    Invariant 89: IPC_EXPOSES_EXPLICIT_COMMANDS_NEVER_ARBITRARY_SERVICE_EXECUTION
        if (!AllowlistedCommands.Contains(request.CommandName))
        {
            return IpcResponseEnvelope.CreateError(
                requestId: request.RequestId,
                serviceInstanceId: _serviceInstanceId,
                status: IpcResponseStatus.UnsupportedCommand,
                errorCode: "UNKNOWN_COMMAND",
                errorMessage: $"Nepoznata ili nedozvoljena komanda '{request.CommandName}'.");
        }

        // 3. Invariant 92: RETRIED_STATE_CHANGING_REQUEST_NEVER_CAUSES_DUPLICATE_EFFECT
        if (StateChangingCommands.Contains(request.CommandName) && _idempotencyCache.TryGetValue(request.RequestId, out var cachedResponse))
        {
            return cachedResponse;
        }

        // 4. Dynamic session owner resolution (M3): Resolve immediately prior to evaluation
        var effectiveOwner = !string.IsNullOrWhiteSpace(sessionOwnerPrincipalRef)
            ? sessionOwnerPrincipalRef
            : _sessionOwnerResolver.GetSessionOwner(request.SessionId);

        // 5. Authorization check
        var authDecision = _authPolicy.Evaluate(request, peerIdentity, effectiveOwner);
        if (!authDecision.IsAllowed)
        {
            var authStatus = authDecision.Outcome == AuthorizationOutcome.Unknown
                ? IpcResponseStatus.Unauthorized
                : IpcResponseStatus.Forbidden;

            var deniedResponse = IpcResponseEnvelope.CreateError(
                requestId: request.RequestId,
                serviceInstanceId: _serviceInstanceId,
                status: authStatus,
                errorCode: "ACCESS_DENIED",
                errorMessage: string.Join("; ", authDecision.ReasonCodes));

            RecordAuditIfStateChanging(request, peerIdentity, receivedAtUtc, authDecision, "Denied", "ACCESS_DENIED");
            return deniedResponse;
        }

        // 6. Lookup handler
        if (!_handlers.TryGetValue(request.CommandName, out var handler))
        {
            return IpcResponseEnvelope.CreateError(
                requestId: request.RequestId,
                serviceInstanceId: _serviceInstanceId,
                status: IpcResponseStatus.UnsupportedCommand,
                errorCode: "HANDLER_NOT_IMPLEMENTED",
                errorMessage: $"Handler za komandu '{request.CommandName}' nije registrovan.");
        }

        // 7. Execute handler safely
        try
        {
            // Invariant 96: Non-critical operations can respect cancellation, but committed ones complete
            var response = await handler(request, peerIdentity, cancellationToken).ConfigureAwait(false);

            if (StateChangingCommands.Contains(request.CommandName))
            {
                _idempotencyCache[request.RequestId] = response;
                RecordAuditIfStateChanging(request, peerIdentity, receivedAtUtc, authDecision, response.Status.ToString(), response.ErrorCode);

                // If StartSession succeeded, record the caller's PrincipalRef as authoritative immutable session owner
                if (request.CommandName == "StartSession" && response.Status == IpcResponseStatus.Success)
                {
                    var sessionId = !string.IsNullOrWhiteSpace(request.SessionId)
                        ? request.SessionId
                        : Guid.NewGuid().ToString("N");

                    _sessionOwnerResolver.RecordSessionOwner(sessionId, peerIdentity.PrincipalRef);
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            var internalErr = IpcResponseEnvelope.CreateError(
                requestId: request.RequestId,
                serviceInstanceId: _serviceInstanceId,
                status: IpcResponseStatus.InternalError,
                errorCode: "INTERNAL_SERVICE_ERROR",
                errorMessage: "Došlo je do unutrašnje greške pri obradi komande.");

            RecordAuditIfStateChanging(request, peerIdentity, receivedAtUtc, authDecision, "InternalError", ex.GetType().Name);
            return internalErr;
        }
    }

    private void RecordAuditIfStateChanging(
        IpcRequestEnvelope request,
        PlatformPeerIdentity peerIdentity,
        DateTimeOffset receivedAtUtc,
        CommandAuthorizationDecision authDecision,
        string outcome,
        string? failureCode)
    {
        if (!StateChangingCommands.Contains(request.CommandName))
        {
            return;
        }

        var completedAtUtc = DateTimeOffset.UtcNow;
        var audit = new ControlCommandObserved(
            CommandEventId: $"cmd-evt-{Guid.NewGuid():N}",
            RequestId: request.RequestId,
            CommandName: request.CommandName,
            SessionId: request.SessionId,
            PeerIdentityRef: peerIdentity.PrincipalRef,
            ReceivedAtUtc: receivedAtUtc,
            CompletedAtUtc: completedAtUtc,
            AuthorizationDecisionRef: authDecision.PolicyRef,
            Outcome: outcome,
            FailureCode: failureCode);

        lock (_auditLock)
        {
            _auditLog.Add(audit);
        }
    }
}
