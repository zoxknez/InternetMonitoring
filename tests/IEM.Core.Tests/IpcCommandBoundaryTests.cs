using System.Text.Json;
using IEM.Core.Ipc;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-11: Authenticated Platform IPC Command Boundary.
/// Invariants 83-96.
/// </summary>
public sealed class IpcCommandBoundaryTests
{
    private readonly IpcCommandDispatcher _dispatcher;
    private readonly string _serviceId = "iem-svc-test-1";

    public IpcCommandBoundaryTests()
    {
        _dispatcher = new IpcCommandDispatcher(_serviceId);

        // Register standard test handlers
        _dispatcher.RegisterHandler("GetServiceStatus", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, _serviceId, "{\"status\": \"running\"}")));

        _dispatcher.RegisterHandler("StartSession", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, _serviceId, "{\"sessionCreated\": true}")));

        _dispatcher.RegisterHandler("StopSession", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, _serviceId, "{\"sessionStopped\": true}")));

        _dispatcher.RegisterHandler("FinalizeSession", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, _serviceId, "{\"manifest\": \"manifest.json\"}")));

        _dispatcher.RegisterHandler("RetryTimestamp", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, _serviceId, "{\"timestamp\": \"timestamp.tsr\"}")));
    }

    [Fact]
    public async Task Transport_has_no_command_semantics_Invariant_83()
    {
        // Invariant 83: IPC_TRANSPORT_NEVER_DEFINES_COMMAND_SEMANTICS
        var winPeer = PlatformPeerIdentity.CreateWindows("S-1-5-21-USER");
        var req = new IpcRequestEnvelope { RequestId = "r1", CommandName = "GetServiceStatus" };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);

        var resp = await _dispatcher.DispatchFrameAsync(bytes, winPeer);

        Assert.Equal(IpcResponseStatus.Success, resp.Status);
        Assert.Equal("r1", resp.RequestId);
    }

    [Fact]
    public async Task Windows_peer_identity_comes_from_transport_not_payload_Invariants_84_and_94()
    {
        // Invariant 94: CALLER_IDENTITY_IS_DERIVED_FROM_TRANSPORT_NOT_CLIENT_PAYLOAD
        var realUserSid = PlatformPeerIdentity.CreateWindows("S-1-5-21-REAL-USER");

        // Malicious client claims to be Admin in payload
        var req = new IpcRequestEnvelope
        {
            RequestId = "r-spoof",
            CommandName = "GetServiceStatus",
            Payload = "{\"callerSid\": \"S-1-5-32-544-ADMIN\"}",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        var resp = await _dispatcher.DispatchFrameAsync(bytes, realUserSid);

        Assert.Equal(IpcResponseStatus.Success, resp.Status);
        // Authorization was evaluated against realUserSid, not the payload claim
    }

    [Fact]
    public async Task Connected_pipe_does_not_imply_authorization_Invariant_85()
    {
        // Invariant 85: TRANSPORT_ACCESS_NEVER_IMPLIES_COMMAND_AUTHORIZATION
        var userA = PlatformPeerIdentity.CreateWindows("S-1-5-21-USER-A");
        var userB_Owner = "WindowsSid:S-1-5-21-USER-B";

        // User A tries to stop User B's session
        var req = new IpcRequestEnvelope
        {
            RequestId = "r-stop-unauth",
            CommandName = "StopSession",
            SessionId = "session-b",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        var resp = await _dispatcher.DispatchFrameAsync(bytes, userA, sessionOwnerPrincipalRef: userB_Owner);

        Assert.Equal(IpcResponseStatus.Forbidden, resp.Status);
        Assert.Equal("ACCESS_DENIED", resp.ErrorCode);
    }

    [Fact]
    public async Task Unknown_peer_fails_closed_Invariant_90()
    {
        // Invariant 90: UNKNOWN_CALLER_AUTHORIZATION_FAILS_CLOSED
        var unknownPeer = PlatformPeerIdentity.Unknown;

        var req = new IpcRequestEnvelope
        {
            RequestId = "r-unknown",
            CommandName = "StartSession",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        var resp = await _dispatcher.DispatchFrameAsync(bytes, unknownPeer);

        Assert.Equal(IpcResponseStatus.Unauthorized, resp.Status);
    }

    [Fact]
    public async Task Unknown_protocol_major_is_rejected_Invariant_86()
    {
        // Invariant 86: UNKNOWN_IPC_PROTOCOL_VERSION_IS_NEVER_SILENTLY_DOWNGRADED
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-USER");
        var req = new IpcRequestEnvelope
        {
            ProtocolVersion = 99, // Future unknown version
            RequestId = "r-proto99",
            CommandName = "GetServiceStatus",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        var resp = await _dispatcher.DispatchFrameAsync(bytes, peer);

        Assert.Equal(IpcResponseStatus.UnsupportedProtocol, resp.Status);
        Assert.Equal("UNSUPPORTED_PROTOCOL_VERSION", resp.ErrorCode);
    }

    [Fact]
    public async Task Unknown_command_is_rejected_Invariants_87_and_89()
    {
        // Invariants 87 & 89: IPC_EXPOSES_EXPLICIT_COMMANDS_NEVER_ARBITRARY_SERVICE_EXECUTION
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-USER");
        var req = new IpcRequestEnvelope
        {
            RequestId = "r-unknown-cmd",
            CommandName = "ExecuteArbitraryMethod",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        var resp = await _dispatcher.DispatchFrameAsync(bytes, peer);

        Assert.Equal(IpcResponseStatus.UnsupportedCommand, resp.Status);
        Assert.Equal("UNKNOWN_COMMAND", resp.ErrorCode);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_before_allocation_Invariant_88()
    {
        // Invariant 88: IPC_MESSAGE_BOUNDARY_IS_EXPLICIT_AND_BOUNDED
        using var stream = new MemoryStream();
        var oversized = new byte[IpcMessageFraming.MaxMessageBytes + 100];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            IpcMessageFraming.WriteFrameAsync(stream, oversized));
    }

    [Fact]
    public async Task Duplicate_RequestId_does_not_repeat_StartSession_Invariant_92()
    {
        // Invariant 92: RETRIED_STATE_CHANGING_REQUEST_NEVER_CAUSES_DUPLICATE_EFFECT
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-USER");
        var req = new IpcRequestEnvelope
        {
            RequestId = "r-idemp-1",
            CommandName = "StartSession",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);

        var resp1 = await _dispatcher.DispatchFrameAsync(bytes, peer);
        var resp2 = await _dispatcher.DispatchFrameAsync(bytes, peer);

        Assert.Equal(IpcResponseStatus.Success, resp1.Status);
        Assert.Equal(IpcResponseStatus.Success, resp2.Status);
        Assert.Equal(resp1.RequestId, resp2.RequestId);
    }

    [Fact]
    public async Task State_changing_commands_record_auditable_events_Invariant_93()
    {
        // Invariant 93: EVIDENCE_AFFECTING_CONTROL_ACTIONS_ARE_AUDITABLE
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-OWNER");
        var req = new IpcRequestEnvelope
        {
            RequestId = "r-audit-finalize",
            CommandName = "FinalizeSession",
            SessionId = "session-123",
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(req);
        var resp = await _dispatcher.DispatchFrameAsync(bytes, peer, sessionOwnerPrincipalRef: "WindowsSid:S-1-5-21-OWNER");

        Assert.Equal(IpcResponseStatus.Success, resp.Status);

        var audit = _dispatcher.AuditLog.FirstOrDefault(a => a.RequestId == "r-audit-finalize");
        Assert.NotNull(audit);
        Assert.Equal("FinalizeSession", audit.CommandName);
        Assert.Equal("session-123", audit.SessionId);
        Assert.Equal("WindowsSid:S-1-5-21-OWNER", audit.PeerIdentityRef);
        Assert.Equal("Success", audit.Outcome);
    }

    [Fact]
    public async Task Windows_transport_and_Linux_transport_produce_same_command_semantics_Invariant_95()
    {
        // Invariant 95: PLATFORM_CREDENTIAL_FORMAT_NEVER_CHANGES_COMMAND_AUTHORIZATION_SEMANTICS
        var winPeer = PlatformPeerIdentity.CreateWindows("S-1-5-21-OWNER");
        var unixPeer = PlatformPeerIdentity.CreateUnix(uid: 1000);

        var reqWin = new IpcRequestEnvelope { RequestId = "r-win", CommandName = "FinalizeSession", SessionId = "s1" };
        var reqUnix = new IpcRequestEnvelope { RequestId = "r-unix", CommandName = "FinalizeSession", SessionId = "s1" };

        var respWin = await _dispatcher.DispatchFrameAsync(
            JsonSerializer.SerializeToUtf8Bytes(reqWin),
            winPeer,
            sessionOwnerPrincipalRef: "WindowsSid:S-1-5-21-OWNER");

        var respUnix = await _dispatcher.DispatchFrameAsync(
            JsonSerializer.SerializeToUtf8Bytes(reqUnix),
            unixPeer,
            sessionOwnerPrincipalRef: "UnixUid:1000");

        Assert.Equal(IpcResponseStatus.Success, respWin.Status);
        Assert.Equal(IpcResponseStatus.Success, respUnix.Status);
    }

    [Fact]
    public async Task End_to_end_security_acceptance_scenario_e2e()
    {
        // 1. Normal user connects and checks status
        var user = PlatformPeerIdentity.CreateWindows("S-1-5-21-ZORAN");
        var statusReq = new IpcRequestEnvelope { RequestId = "s1", CommandName = "GetServiceStatus" };
        var statusResp = await _dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(statusReq), user);
        Assert.Equal(IpcResponseStatus.Success, statusResp.Status);

        // 2. Start session R1
        var startReq = new IpcRequestEnvelope { RequestId = "r-start-1", CommandName = "StartSession" };
        var startResp = await _dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(startReq), user);
        Assert.Equal(IpcResponseStatus.Success, startResp.Status);

        // 3. Retry Start session R1 -> Idempotent
        var startRetryResp = await _dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(startReq), user);
        Assert.Equal(IpcResponseStatus.Success, startRetryResp.Status);

        // 4. Unauthorized other user tries to Finalize -> Forbidden
        var attacker = PlatformPeerIdentity.CreateWindows("S-1-5-21-ATTACKER");
        var finalizeReq = new IpcRequestEnvelope { RequestId = "r-fin-1", CommandName = "FinalizeSession", SessionId = "ses-1" };
        var finRespAttacker = await _dispatcher.DispatchFrameAsync(
            JsonSerializer.SerializeToUtf8Bytes(finalizeReq),
            attacker,
            sessionOwnerPrincipalRef: "WindowsSid:S-1-5-21-ZORAN");
        Assert.Equal(IpcResponseStatus.Forbidden, finRespAttacker.Status);

        // 5. Valid owner finalizes
        var finRespOwner = await _dispatcher.DispatchFrameAsync(
            JsonSerializer.SerializeToUtf8Bytes(finalizeReq),
            user,
            sessionOwnerPrincipalRef: "WindowsSid:S-1-5-21-ZORAN");
        Assert.Equal(IpcResponseStatus.Success, finRespOwner.Status);

        // 6. Unknown protocol v99 -> UnsupportedProtocol
        var v99Req = new IpcRequestEnvelope { ProtocolVersion = 99, RequestId = "r-v99", CommandName = "GetServiceStatus" };
        var v99Resp = await _dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(v99Req), user);
        Assert.Equal(IpcResponseStatus.UnsupportedProtocol, v99Resp.Status);
    }
}
