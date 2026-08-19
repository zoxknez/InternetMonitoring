using System.Text.Json;
using IEM.Core.Ipc;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Linux IPC, UDS lifecycle, and Identity extraction invariants.
/// Invariants 83-96, 261-268 (Phase 3.1-3).
/// </summary>
public sealed class LinuxUnixDomainSocketTransportTests
{
    [Fact]
    public void Canonical_PrincipalRef_is_strictly_unix_uid_or_windows_sid()
    {
        var unixIdentity = PlatformPeerIdentity.CreateUnix(uid: 1000, gid: 1000, pid: 1234, claims: [PlatformPeerIdentity.RoleOperator]);
        Assert.Equal("unix:1000", unixIdentity.PrincipalRef);
        Assert.Equal(PeerIdentityScheme.UnixUid, unixIdentity.Scheme);
        Assert.True(unixIdentity.IsOperator);
        Assert.False(unixIdentity.IsAdmin);

        var winIdentity = PlatformPeerIdentity.CreateWindows("S-1-5-21-12345", claims: [PlatformPeerIdentity.RoleAdmin, PlatformPeerIdentity.RoleOperator]);
        Assert.Equal("windows:S-1-5-21-12345", winIdentity.PrincipalRef);
        Assert.Equal(PeerIdentityScheme.WindowsSid, winIdentity.Scheme);
        Assert.True(winIdentity.IsAdmin);
        Assert.True(winIdentity.IsOperator);
    }

    [Fact]
    public void Root_user_uid_0_receives_both_admin_and_operator_roles()
    {
        var rootIdentity = PlatformPeerIdentity.CreateUnix(uid: 0, gid: 0, pid: 1, claims: [PlatformPeerIdentity.RoleAdmin, PlatformPeerIdentity.RoleOperator]);
        Assert.Equal("unix:0", rootIdentity.PrincipalRef);
        Assert.True(rootIdentity.IsAdmin);
        Assert.True(rootIdentity.IsOperator);

        var policy = IpcAuthorizationPolicy.Default;
        var startReq = new IpcRequestEnvelope { RequestId = "r-root-1", CommandName = "StartSession" };
        var stopReq = new IpcRequestEnvelope { RequestId = "r-root-2", CommandName = "StopSession", SessionId = "ses-1" };

        // Root can start session
        var startDec = policy.Evaluate(startReq, rootIdentity);
        Assert.True(startDec.IsAllowed);

        // Root can stop any user's session via Admin override
        var stopDec = policy.Evaluate(stopReq, rootIdentity, sessionOwnerPrincipalRef: "unix:1001");
        Assert.True(stopDec.IsAllowed);
    }

    [Fact]
    public async Task Payload_identity_spoofing_is_strictly_ignored_by_dispatcher()
    {
        var ownerResolver = new InMemorySessionOwnerResolver();
        var dispatcher = new IpcCommandDispatcher("srv-test", IpcAuthorizationPolicy.Default, ownerResolver);

        dispatcher.RegisterHandler("StopSession", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, "srv-test", "{\"stopped\": true}")));

        // Real peer from kernel: normal unprivileged operator unix:1002
        var genuinePeer = PlatformPeerIdentity.CreateUnix(uid: 1002, claims: [PlatformPeerIdentity.RoleOperator]);

        // Malicious request payload claiming uid 0, root, and role:admin
        var maliciousPayload = new
        {
            uid = 0,
            role = "role:admin",
            isAdmin = true,
            callerSid = "S-1-5-32-544-ADMIN",
            principalRef = "unix:0"
        };

        var req = new IpcRequestEnvelope
        {
            RequestId = "req-spoof-1",
            CommandName = "StopSession",
            SessionId = "session-owned-by-1001",
            Payload = JsonSerializer.Serialize(maliciousPayload),
        };

        var reqBytes = JsonSerializer.SerializeToUtf8Bytes(req);

        // Dispatch against session owned by unix:1001
        var response = await dispatcher.DispatchFrameAsync(reqBytes, genuinePeer, sessionOwnerPrincipalRef: "unix:1001");

        // Must strictly be Forbidden (403 / ACCESS_DENIED)
        Assert.Equal(IpcResponseStatus.Forbidden, response.Status);
        Assert.Equal("ACCESS_DENIED", response.ErrorCode);
    }

    [Fact]
    public async Task Cross_connection_dynamic_session_ownership_enforcement()
    {
        var ownerResolver = new InMemorySessionOwnerResolver();
        var dispatcher = new IpcCommandDispatcher("srv-test", IpcAuthorizationPolicy.Default, ownerResolver);

        dispatcher.RegisterHandler("StartSession", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, "srv-test", "{\"sessionStarted\": true}")));

        dispatcher.RegisterHandler("StopSession", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, "srv-test", "{\"sessionStopped\": true}")));

        dispatcher.RegisterHandler("FinalizeSession", (req, peer, ct) =>
            Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, "srv-test", "{\"sessionFinalized\": true}")));

        var userA = PlatformPeerIdentity.CreateUnix(uid: 1001, claims: [PlatformPeerIdentity.RoleOperator]);
        var userB = PlatformPeerIdentity.CreateUnix(uid: 1002, claims: [PlatformPeerIdentity.RoleOperator]);
        var admin = PlatformPeerIdentity.CreateUnix(uid: 0, claims: [PlatformPeerIdentity.RoleAdmin, PlatformPeerIdentity.RoleOperator]);

        var sessionId = "multi-user-session-99";

        // 1. User A starts session
        var startReq = new IpcRequestEnvelope { RequestId = "r-start", CommandName = "StartSession", SessionId = sessionId };
        var startRes = await dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(startReq), userA);
        Assert.Equal(IpcResponseStatus.Success, startRes.Status);
        Assert.Equal("unix:1001", ownerResolver.GetSessionOwner(sessionId));

        // 2. User B tries to finalize session -> Denied
        var finReqB = new IpcRequestEnvelope { RequestId = "r-fin-b", CommandName = "FinalizeSession", SessionId = sessionId };
        var finResB = await dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(finReqB), userB);
        Assert.Equal(IpcResponseStatus.Forbidden, finResB.Status);

        // 3. User A finalizes own session -> Success
        var finReqA = new IpcRequestEnvelope { RequestId = "r-fin-a", CommandName = "FinalizeSession", SessionId = sessionId };
        var finResA = await dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(finReqA), userA);
        Assert.Equal(IpcResponseStatus.Success, finResA.Status);

        // 4. Admin stops session -> Success
        var stopReqAdmin = new IpcRequestEnvelope { RequestId = "r-stop-admin", CommandName = "StopSession", SessionId = sessionId };
        var stopResAdmin = await dispatcher.DispatchFrameAsync(JsonSerializer.SerializeToUtf8Bytes(stopReqAdmin), admin);
        Assert.Equal(IpcResponseStatus.Success, stopResAdmin.Status);
    }
}
