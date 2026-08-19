using System.Text.Json;
using IEM.Core.Ipc;

namespace IEM.Core.Tests;

/// <summary>
/// Comprehensive deterministic test suite for IpcAuthorizationPolicy Version 2 and Session Ownership Lifecycle.
/// Invariants 83-96 and 261-268 (Phase 3.1-3).
/// </summary>
public sealed class IpcAuthorizationPolicyV2Tests
{
    private readonly IpcAuthorizationPolicy _policy = IpcAuthorizationPolicy.Default;

    [Fact]
    public void Policy_version_is_strictly_const_2()
    {
        Assert.Equal(2, IpcAuthorizationPolicy.PolicyVersion);
    }

    [Fact]
    public void Policy_hash_is_deterministic_and_not_empty()
    {
        var hash1 = _policy.PolicyHash;
        var hash2 = _policy.PolicyHash;
        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 hex string
    }

    [Fact]
    public void Unknown_caller_strictly_fails_closed()
    {
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-1",
            CommandName = "GetServiceStatus",
        };

        var decision = _policy.Evaluate(req, PlatformPeerIdentity.Unknown);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Unknown, decision.Outcome);
    }

    [Fact]
    public void Authenticated_peer_without_roles_is_denied_all_commands()
    {
        var peerWithoutRoles = PlatformPeerIdentity.CreateUnix(uid: 1005, claims: []);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-2",
            CommandName = "GetServiceStatus",
        };

        var decision = _policy.Evaluate(req, peerWithoutRoles);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    [Theory]
    [InlineData("GetServiceStatus")]
    [InlineData("GetActiveSession")]
    [InlineData("GetSessionStatus")]
    [InlineData("StartSession")]
    public void Operator_is_allowed_for_read_and_start_commands(string commandName)
    {
        var operatorPeer = PlatformPeerIdentity.CreateUnix(uid: 1001, claims: [PlatformPeerIdentity.RoleOperator]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-3",
            CommandName = commandName,
            SessionId = "session-1",
        };

        var decision = _policy.Evaluate(req, operatorPeer);

        Assert.True(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
    }

    [Theory]
    [InlineData("StopSession")]
    [InlineData("FinalizeSession")]
    [InlineData("RetryTimestamp")]
    [InlineData("CreateExport")]
    public void Missing_or_empty_session_owner_fails_closed_denied_for_control_commands(string commandName)
    {
        var operatorPeer = PlatformPeerIdentity.CreateUnix(uid: 1001, claims: [PlatformPeerIdentity.RoleOperator]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-4",
            CommandName = commandName,
            SessionId = "session-1",
        };

        // When owner is null, empty string, or whitespace:
        var decNull = _policy.Evaluate(req, operatorPeer, sessionOwnerPrincipalRef: null);
        var decEmpty = _policy.Evaluate(req, operatorPeer, sessionOwnerPrincipalRef: "");
        var decSpace = _policy.Evaluate(req, operatorPeer, sessionOwnerPrincipalRef: "   ");

        Assert.False(decNull.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decNull.Outcome);

        Assert.False(decEmpty.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decEmpty.Outcome);

        Assert.False(decSpace.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decSpace.Outcome);
    }

    [Fact]
    public void Session_owner_is_allowed_to_control_session()
    {
        var ownerPeer = PlatformPeerIdentity.CreateUnix(uid: 1001, claims: [PlatformPeerIdentity.RoleOperator]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-5",
            CommandName = "StopSession",
            SessionId = "session-1",
        };

        var decision = _policy.Evaluate(req, ownerPeer, sessionOwnerPrincipalRef: "unix:1001");

        Assert.True(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
    }

    [Fact]
    public void Non_owner_operator_is_denied_session_control()
    {
        var otherOperatorPeer = PlatformPeerIdentity.CreateUnix(uid: 1002, claims: [PlatformPeerIdentity.RoleOperator]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-6",
            CommandName = "StopSession",
            SessionId = "session-1",
        };

        var decision = _policy.Evaluate(req, otherOperatorPeer, sessionOwnerPrincipalRef: "unix:1001");

        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Admin_override_allows_session_control_for_non_owner()
    {
        var adminPeer = PlatformPeerIdentity.CreateUnix(uid: 0, claims: [PlatformPeerIdentity.RoleAdmin, PlatformPeerIdentity.RoleOperator]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-7",
            CommandName = "StopSession",
            SessionId = "session-1",
        };

        var decision = _policy.Evaluate(req, adminPeer, sessionOwnerPrincipalRef: "unix:1001");

        Assert.True(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
    }

    [Theory]
    [InlineData("SuperAdministrator")]
    [InlineData("root-user")]
    [InlineData("role:adminXYZ")]
    [InlineData("Admin")]
    [InlineData("root")]
    public void Substrings_and_near_matches_do_not_grant_admin_privileges(string fakeClaim)
    {
        var fakeAdminPeer = PlatformPeerIdentity.CreateUnix(uid: 1003, claims: [fakeClaim, PlatformPeerIdentity.RoleOperator]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-8",
            CommandName = "StopSession",
            SessionId = "session-1",
        };

        var decision = _policy.Evaluate(req, fakeAdminPeer, sessionOwnerPrincipalRef: "unix:1001");

        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public void Owner_comparison_is_strictly_ordinal()
    {
        var peer = PlatformPeerIdentity.CreateUnix(uid: 1001, claims: [PlatformPeerIdentity.RoleOperator]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-9",
            CommandName = "StopSession",
            SessionId = "session-1",
        };

        // unix:1001 vs UNIX:1001 (must not match because Scheme is lowercase canonical)
        var decision = _policy.Evaluate(req, peer, sessionOwnerPrincipalRef: "UNIX:1001");

        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    [Fact]
    public async Task Dynamic_session_owner_lifecycle_in_dispatcher()
    {
        var ownerResolver = new InMemorySessionOwnerResolver();
        var dispatcher = new IpcCommandDispatcher("srv-test", _policy, ownerResolver);

        dispatcher.RegisterHandler("StartSession", (req, peer, ct) =>
        {
            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, "srv-test", "{\"started\":true}"));
        });

        dispatcher.RegisterHandler("StopSession", (req, peer, ct) =>
        {
            return Task.FromResult(IpcResponseEnvelope.CreateSuccess(req.RequestId, "srv-test", "{\"stopped\":true}"));
        });

        var userA = PlatformPeerIdentity.CreateUnix(uid: 1001, claims: [PlatformPeerIdentity.RoleOperator]);
        var userB = PlatformPeerIdentity.CreateUnix(uid: 1002, claims: [PlatformPeerIdentity.RoleOperator]);
        var admin = PlatformPeerIdentity.CreateUnix(uid: 0, claims: [PlatformPeerIdentity.RoleAdmin, PlatformPeerIdentity.RoleOperator]);

        var sessionId = "dynamic-session-1";

        // 1. User A starts session
        var startReq = new IpcRequestEnvelope
        {
            RequestId = "req-start-1",
            CommandName = "StartSession",
            SessionId = sessionId
        };
        var startBytes = JsonSerializer.SerializeToUtf8Bytes(startReq);
        var startRes = await dispatcher.DispatchFrameAsync(startBytes, userA);

        Assert.Equal(IpcResponseStatus.Success, startRes.Status);
        Assert.Equal("unix:1001", ownerResolver.GetSessionOwner(sessionId));

        // 2. User B tries to stop User A's session -> Denied
        var stopReqB = new IpcRequestEnvelope
        {
            RequestId = "req-stop-b",
            CommandName = "StopSession",
            SessionId = sessionId
        };
        var stopBytesB = JsonSerializer.SerializeToUtf8Bytes(stopReqB);
        var stopResB = await dispatcher.DispatchFrameAsync(stopBytesB, userB);

        Assert.Equal(IpcResponseStatus.Forbidden, stopResB.Status);
        Assert.Equal("ACCESS_DENIED", stopResB.ErrorCode);

        // 3. User A stops own session -> Success
        var stopReqA = new IpcRequestEnvelope
        {
            RequestId = "req-stop-a",
            CommandName = "StopSession",
            SessionId = sessionId
        };
        var stopBytesA = JsonSerializer.SerializeToUtf8Bytes(stopReqA);
        var stopResA = await dispatcher.DispatchFrameAsync(stopBytesA, userA);

        Assert.Equal(IpcResponseStatus.Success, stopResA.Status);

        // 4. Admin stops session -> Success via Admin override
        var stopReqAdmin = new IpcRequestEnvelope
        {
            RequestId = "req-stop-admin",
            CommandName = "StopSession",
            SessionId = sessionId
        };
        var stopBytesAdmin = JsonSerializer.SerializeToUtf8Bytes(stopReqAdmin);
        var stopResAdmin = await dispatcher.DispatchFrameAsync(stopBytesAdmin, admin);

        Assert.Equal(IpcResponseStatus.Success, stopResAdmin.Status);
    }
}
