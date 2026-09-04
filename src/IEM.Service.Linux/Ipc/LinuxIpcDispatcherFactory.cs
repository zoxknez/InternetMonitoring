using System.Text.Json;
using IEM.Core.Ipc;
using IEM.Service.Runtime;

namespace IEM.Service.Linux.Ipc;

/// <summary>
/// Factory that builds the authoritative IpcCommandDispatcher wired to Linux runtime workers.
/// </summary>
public static class LinuxIpcDispatcherFactory
{
    public static IpcCommandDispatcher Create(
        IMonitorSessionController monitorController,
        ISpeedStatusSource speedWorker,
        ISessionOwnerResolver? sessionOwnerResolver = null)
    {
        ArgumentNullException.ThrowIfNull(monitorController);
        ArgumentNullException.ThrowIfNull(speedWorker);

        var dispatcher = new IpcCommandDispatcher(
            serviceInstanceId: Guid.NewGuid().ToString("N"),
            authPolicy: IpcAuthorizationPolicy.Default,
            sessionOwnerResolver: sessionOwnerResolver);

        // 1. GetServiceStatus
        dispatcher.RegisterHandler("GetServiceStatus", (request, peer, ct) =>
        {
            var statusObj = new
            {
                Status = monitorController.Status,
                SpeedStatus = speedWorker.Status,
                Snapshot = monitorController.Live,
                CallerPrincipal = peer.PrincipalRef,
                Roles = peer.SupplementaryClaims
            };

            var json = JsonSerializer.Serialize(statusObj);
            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(request.RequestId, dispatcher.ServiceInstanceId, json));
        });

        // 2. GetActiveSession
        dispatcher.RegisterHandler("GetActiveSession", (request, peer, ct) =>
        {
            var activeSessionId = monitorController.Status.SessionId;
            var result = new
            {
                SessionId = activeSessionId,
                State = monitorController.Status,
                Owner = dispatcher.SessionOwnerResolver.GetSessionOwner(activeSessionId),
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(result)));
        });

        // 3. GetSessionStatus
        dispatcher.RegisterHandler("GetSessionStatus", (request, peer, ct) =>
        {
            var status = new
            {
                SessionId = request.SessionId,
                Status = monitorController.Status,
                Live = monitorController.Live,
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(status)));
        });

        // 4. StartSession
        dispatcher.RegisterHandler("StartSession", async (request, peer, ct) =>
        {
            var sessionId = !string.IsNullOrWhiteSpace(request.SessionId)
                ? request.SessionId
                : $"S{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..31];

            StartSessionCommandPayload payload;
            try
            {
                payload = string.IsNullOrWhiteSpace(request.Payload)
                    ? new StartSessionCommandPayload()
                    : JsonSerializer.Deserialize<StartSessionCommandPayload>(request.Payload) ??
                      throw new JsonException("Prazan payload.");
            }
            catch (JsonException ex)
            {
                return IpcResponseEnvelope.CreateError(
                    request.RequestId,
                    dispatcher.ServiceInstanceId,
                    IpcResponseStatus.InvalidRequest,
                    "INVALID_START_PAYLOAD",
                    $"StartSession payload nije validan: {ex.Message}");
            }

            if (!MonitorSettings.TryParseDuration(payload.Duration, out var duration))
            {
                return IpcResponseEnvelope.CreateError(
                    request.RequestId,
                    dispatcher.ServiceInstanceId,
                    IpcResponseStatus.InvalidRequest,
                    "INVALID_DURATION",
                    "Trajanje mora biti, na primer, 90m, 48h, 7d ili infinite.");
            }

            var control = await monitorController.StartSessionAsync(
                sessionId,
                duration,
                payload.InterfaceName,
                peer.PrincipalRef,
                ct).ConfigureAwait(false);

            return ToResponse(dispatcher, request, control);
        });

        // 5. StopSession
        dispatcher.RegisterHandler("StopSession", async (request, peer, ct) =>
        {
            var control = await monitorController.PauseSessionAsync(request.SessionId, ct).ConfigureAwait(false);
            return ToResponse(dispatcher, request, control);
        });

        // 6. FinalizeSession
        dispatcher.RegisterHandler("FinalizeSession", async (request, peer, ct) =>
        {
            var control = await monitorController.FinalizeSessionAsync(request.SessionId, ct).ConfigureAwait(false);
            return ToResponse(dispatcher, request, control);
        });

        // 7. RetryTimestamp
        dispatcher.RegisterHandler("RetryTimestamp", (request, peer, ct) =>
        {
            return Task.FromResult(IpcResponseEnvelope.CreateError(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                IpcResponseStatus.Rejected,
                "TIMESTAMP_RETRY_NOT_AVAILABLE",
                "Ponovni vremenski pečat još nije dostupan u ovoj verziji."));
        });

        // 8. CreateExport
        dispatcher.RegisterHandler("CreateExport", (request, peer, ct) =>
        {
            return Task.FromResult(IpcResponseEnvelope.CreateError(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                IpcResponseStatus.Rejected,
                "EXPORT_NOT_AVAILABLE",
                "Izvoz preko servisnog IPC-a još nije dostupan u ovoj verziji."));
        });

        return dispatcher;
    }

    private static IpcResponseEnvelope ToResponse(
        IpcCommandDispatcher dispatcher,
        IpcRequestEnvelope request,
        SessionControlResult result)
    {
        if (result.Accepted)
        {
            return IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(new
                {
                    result.SessionId,
                    Accepted = true,
                    result.Message,
                }),
                result.SessionId);
        }

        var (status, code) = result.Outcome switch
        {
            SessionControlOutcome.Conflict => (IpcResponseStatus.Conflict, "SESSION_CONFLICT"),
            SessionControlOutcome.NotFound => (IpcResponseStatus.NotFound, "SESSION_NOT_FOUND"),
            SessionControlOutcome.InvalidRequest => (IpcResponseStatus.InvalidRequest, "INVALID_SESSION_REQUEST"),
            _ => (IpcResponseStatus.InternalError, "UNEXPECTED_CONTROL_OUTCOME"),
        };

        return IpcResponseEnvelope.CreateError(
            request.RequestId,
            dispatcher.ServiceInstanceId,
            status,
            code,
            result.Message);
    }
}
