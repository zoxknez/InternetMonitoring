using System.Text.Json;
using IEM.Core;
using IEM.Core.Ipc;
using IEM.Service.Linux.Ipc;
using IEM.Service.Runtime;

namespace IEM.Core.Tests;

public sealed class LinuxIpcDispatcherFactoryTests
{
    private static readonly PlatformPeerIdentity Operator =
        PlatformPeerIdentity.CreateUnix(1000, claims: [PlatformPeerIdentity.RoleOperator]);

    [Fact]
    public async Task StartSession_delegates_to_runtime_and_records_authoritative_owner()
    {
        var controller = new FakeMonitorController();
        var owners = new InMemorySessionOwnerResolver();
        var dispatcher = LinuxIpcDispatcherFactory.Create(controller, FakeSpeedStatusSource.Instance, owners);
        var request = new IpcRequestEnvelope
        {
            RequestId = "start-1",
            CommandName = "StartSession",
            SessionId = "session-1",
            Payload = JsonSerializer.Serialize(new StartSessionCommandPayload
            {
                Duration = "90m",
                InterfaceName = "eth0",
            }),
        };

        var response = await DispatchAsync(dispatcher, request, Operator);

        Assert.Equal(IpcResponseStatus.Success, response.Status);
        Assert.Equal("session-1", response.SessionId);
        Assert.Equal("session-1", controller.StartedSessionId);
        Assert.Equal(TimeSpan.FromMinutes(90), controller.StartedDuration);
        Assert.Equal("eth0", controller.StartedInterface);
        Assert.Equal("unix:1000", controller.StartedOwner);
        Assert.Equal("unix:1000", owners.GetSessionOwner("session-1"));
    }

    [Fact]
    public async Task StopSession_delegates_to_runtime_instead_of_claiming_synthetic_success()
    {
        var controller = new FakeMonitorController();
        var owners = new InMemorySessionOwnerResolver();
        owners.RecordSessionOwner("session-1", Operator.PrincipalRef);
        var dispatcher = LinuxIpcDispatcherFactory.Create(controller, FakeSpeedStatusSource.Instance, owners);
        var request = new IpcRequestEnvelope
        {
            RequestId = "stop-1",
            CommandName = "StopSession",
            SessionId = "session-1",
        };

        var response = await DispatchAsync(dispatcher, request, Operator);

        Assert.Equal(IpcResponseStatus.Success, response.Status);
        Assert.Equal("session-1", controller.PausedSessionId);
    }

    [Fact]
    public async Task Runtime_conflict_is_exposed_as_protocol_conflict()
    {
        var controller = new FakeMonitorController
        {
            StartResult = new SessionControlResult(
                SessionControlOutcome.Conflict,
                "active-session",
                "Sesija je već aktivna."),
        };
        var dispatcher = LinuxIpcDispatcherFactory.Create(
            controller,
            FakeSpeedStatusSource.Instance,
            new InMemorySessionOwnerResolver());
        var request = new IpcRequestEnvelope
        {
            RequestId = "start-conflict",
            CommandName = "StartSession",
            SessionId = "new-session",
            Payload = "{\"duration\":\"48h\"}",
        };

        var response = await DispatchAsync(dispatcher, request, Operator);

        Assert.Equal(IpcResponseStatus.Conflict, response.Status);
        Assert.Equal("SESSION_CONFLICT", response.ErrorCode);
    }

    [Theory]
    [InlineData("not-json", "INVALID_START_PAYLOAD")]
    [InlineData("{\"duration\":\"yesterday\"}", "INVALID_DURATION")]
    public async Task Invalid_start_payload_is_rejected_before_runtime(string payload, string expectedCode)
    {
        var controller = new FakeMonitorController();
        var dispatcher = LinuxIpcDispatcherFactory.Create(
            controller,
            FakeSpeedStatusSource.Instance,
            new InMemorySessionOwnerResolver());
        var request = new IpcRequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CommandName = "StartSession",
            SessionId = "session-1",
            Payload = payload,
        };

        var response = await DispatchAsync(dispatcher, request, Operator);

        Assert.Equal(IpcResponseStatus.InvalidRequest, response.Status);
        Assert.Equal(expectedCode, response.ErrorCode);
        Assert.Null(controller.StartedSessionId);
    }

    [Fact]
    public async Task Unimplemented_export_fails_honestly()
    {
        var owners = new InMemorySessionOwnerResolver();
        owners.RecordSessionOwner("session-1", Operator.PrincipalRef);
        var dispatcher = LinuxIpcDispatcherFactory.Create(
            new FakeMonitorController(),
            FakeSpeedStatusSource.Instance,
            owners);
        var request = new IpcRequestEnvelope
        {
            RequestId = "export-1",
            CommandName = "CreateExport",
            SessionId = "session-1",
        };

        var response = await DispatchAsync(dispatcher, request, Operator);

        Assert.Equal(IpcResponseStatus.Rejected, response.Status);
        Assert.Equal("EXPORT_NOT_AVAILABLE", response.ErrorCode);
    }

    private static Task<IpcResponseEnvelope> DispatchAsync(
        IpcCommandDispatcher dispatcher,
        IpcRequestEnvelope request,
        PlatformPeerIdentity peer) =>
        dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(request), peer);

    private sealed class FakeMonitorController : IMonitorSessionController
    {
        public ServiceStatus Status { get; set; } = ServiceStatus.Idle;
        public MonitorSnapshot Live { get; set; } = MonitorSnapshot.Empty;

        public SessionControlResult StartResult { get; set; } =
            new(SessionControlOutcome.Accepted, "session-1", "accepted");

        public string? StartedSessionId { get; private set; }
        public TimeSpan StartedDuration { get; private set; }
        public string? StartedInterface { get; private set; }
        public string? StartedOwner { get; private set; }
        public string? PausedSessionId { get; private set; }

        public Task<SessionControlResult> StartSessionAsync(
            string sessionId,
            TimeSpan duration,
            string? interfaceName,
            string ownerPrincipalRef,
            CancellationToken cancellationToken)
        {
            StartedSessionId = sessionId;
            StartedDuration = duration;
            StartedInterface = interfaceName;
            StartedOwner = ownerPrincipalRef;
            return Task.FromResult(StartResult with
            {
                SessionId = StartResult.Accepted ? sessionId : StartResult.SessionId,
            });
        }

        public Task<SessionControlResult> PauseSessionAsync(
            string? sessionId,
            CancellationToken cancellationToken)
        {
            PausedSessionId = sessionId;
            return Task.FromResult(new SessionControlResult(
                SessionControlOutcome.Accepted,
                sessionId,
                "accepted"));
        }

        public Task<SessionControlResult> FinalizeSessionAsync(
            string? sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionControlResult(
                SessionControlOutcome.Accepted,
                sessionId,
                "accepted"));
    }

    private sealed class FakeSpeedStatusSource : ISpeedStatusSource
    {
        public static readonly FakeSpeedStatusSource Instance = new();
        public SpeedStatus Status => SpeedStatus.Idle;
    }
}
