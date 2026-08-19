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
        MonitorWorker monitorWorker,
        SpeedWorker speedWorker,
        ISessionOwnerResolver? sessionOwnerResolver = null)
    {
        ArgumentNullException.ThrowIfNull(monitorWorker);
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
                Status = monitorWorker.Status.ToString(),
                SpeedStatus = speedWorker.Status.ToString(),
                Snapshot = monitorWorker.Live,
                CallerPrincipal = peer.PrincipalRef,
                Roles = peer.SupplementaryClaims
            };

            var json = JsonSerializer.Serialize(statusObj);
            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(request.RequestId, dispatcher.ServiceInstanceId, json));
        });

        // 2. GetActiveSession
        dispatcher.RegisterHandler("GetActiveSession", (request, peer, ct) =>
        {
            var activeSessionId = dispatcher.SessionOwnerResolver.GetSessionOwner();
            var result = new
            {
                SessionId = request.SessionId,
                State = monitorWorker.Status.ToString(),
                Owner = activeSessionId,
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
                Status = monitorWorker.Status.ToString(),
                Live = monitorWorker.Live,
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(status)));
        });

        // 4. StartSession
        dispatcher.RegisterHandler("StartSession", (request, peer, ct) =>
        {
            var sessionId = !string.IsNullOrWhiteSpace(request.SessionId)
                ? request.SessionId
                : Guid.NewGuid().ToString("N");

            var result = new
            {
                SessionId = sessionId,
                Started = true,
                Owner = peer.PrincipalRef
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(result)));
        });

        // 5. StopSession
        dispatcher.RegisterHandler("StopSession", (request, peer, ct) =>
        {
            var result = new
            {
                SessionId = request.SessionId,
                Stopped = true,
                StoppedBy = peer.PrincipalRef
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(result)));
        });

        // 6. FinalizeSession
        dispatcher.RegisterHandler("FinalizeSession", (request, peer, ct) =>
        {
            var result = new
            {
                SessionId = request.SessionId,
                Finalized = true,
                FinalizedBy = peer.PrincipalRef
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(result)));
        });

        // 7. RetryTimestamp
        dispatcher.RegisterHandler("RetryTimestamp", (request, peer, ct) =>
        {
            var result = new
            {
                SessionId = request.SessionId,
                Retried = true,
                RetriedBy = peer.PrincipalRef
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(result)));
        });

        // 8. CreateExport
        dispatcher.RegisterHandler("CreateExport", (request, peer, ct) =>
        {
            var result = new
            {
                SessionId = request.SessionId,
                ExportCreated = true,
                ExportedBy = peer.PrincipalRef
            };

            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(
                request.RequestId,
                dispatcher.ServiceInstanceId,
                JsonSerializer.Serialize(result)));
        });

        return dispatcher;
    }
}
