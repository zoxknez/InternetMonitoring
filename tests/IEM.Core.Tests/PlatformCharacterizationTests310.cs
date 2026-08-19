using IEM.Core.Ipc;
using IEM.Core.Model;
using IEM.Storage;
using IEM.Storage.Layout;

namespace IEM.Core.Tests;

/// <summary>
/// Characterization and verification tests for IPC policy invariants (Phase 3.1-0 baseline through Phase 3.1-3).
/// Invariants 83-96 and 261-268.
/// </summary>
public sealed class PlatformCharacterizationTests310
{
    private readonly IpcAuthorizationPolicy _policy = IpcAuthorizationPolicy.Default;

    /// <summary>
    /// Verifies policy version 2 in 3.1-3 with structured hash.
    /// </summary>
    [Fact]
    public void Policy_version_is_2_in_3_1_3()
    {
        Assert.Equal(2, IpcAuthorizationPolicy.PolicyVersion);
        Assert.False(string.IsNullOrWhiteSpace(_policy.PolicyHash));
    }

    /// <summary>
    /// Invariant 264 & M1: Missing session owner strictly fails closed (Denied) in 3.1-3.
    /// </summary>
    [Fact]
    public void Missing_session_owner_fails_closed_in_3_1_3()
    {
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-ARBITRARY-USER", claims: ["role:operator"]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-char-1",
            CommandName = "StopSession",
            SessionId = "session-test",
        };

        // When sessionOwnerPrincipalRef is null or empty in 3.1-3:
        var decision = _policy.Evaluate(req, peer, sessionOwnerPrincipalRef: null);

        // Strictly fails closed (Denied)
        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    /// <summary>
    /// Invariant 265 & M1: Substring matches ("Admin", "root") are removed. Exact "role:admin" required.
    /// </summary>
    [Fact]
    public void Admin_substring_claim_matching_is_denied_without_exact_canonical_role()
    {
        // Peer with claim containing "DomainAdmins" but lacking "role:admin"
        var peerWithSubstr = PlatformPeerIdentity.CreateWindows(
            "S-1-5-21-999",
            claims: ["DomainAdmins", "role:operator"]);

        var req = new IpcRequestEnvelope
        {
            RequestId = "req-char-2",
            CommandName = "FinalizeSession",
            SessionId = "session-test",
        };

        // Different owner, and caller only has substring "Admin" in supplementary claim
        var decision = _policy.Evaluate(req, peerWithSubstr, sessionOwnerPrincipalRef: "windows:S-1-5-21-DIFFERENT-OWNER");

        // Evaluates to Denied
        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    /// <summary>
    /// Invariant 90: Unknown peer identity fails closed.
    /// </summary>
    [Fact]
    public void Unknown_peer_fails_closed_in_3_1_3()
    {
        var unknownPeer = PlatformPeerIdentity.Unknown;
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-char-3",
            CommandName = "GetServiceStatus",
        };

        var decision = _policy.Evaluate(req, unknownPeer);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Unknown, decision.Outcome);
    }

    /// <summary>
    /// Invariant 84 & 85: Operators with canonical role:operator are authorized for read and start.
    /// </summary>
    [Theory]
    [InlineData("GetServiceStatus")]
    [InlineData("GetActiveSession")]
    [InlineData("GetSessionStatus")]
    [InlineData("StartSession")]
    public void Operator_authorized_for_read_and_start_in_3_1_3(string commandName)
    {
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-STANDARD-USER", claims: ["role:operator"]);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-char-4",
            CommandName = commandName,
        };

        var decision = _policy.Evaluate(req, peer);

        Assert.True(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
    }

    /// <summary>
    /// Invariant 85: Authenticated peer without canonical role is DENIED even for read.
    /// </summary>
    [Fact]
    public void Authenticated_peer_without_roles_is_denied_read_commands()
    {
        var peerWithoutRoles = PlatformPeerIdentity.CreateWindows("S-1-5-21-STANDARD-USER", claims: []);
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-char-5",
            CommandName = "GetServiceStatus",
        };

        var decision = _policy.Evaluate(req, peerWithoutRoles);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
    }

    /// <summary>
    /// M2: Verifies PlatformPeerIdentity canonical PrincipalRef format (unix:{uid}, windows:{sid}).
    /// </summary>
    [Fact]
    public void PlatformPeerIdentity_canonical_PrincipalRef_formatting()
    {
        var winPeer = PlatformPeerIdentity.CreateWindows("S-1-5-21-TEST");
        Assert.Equal(PeerIdentityScheme.WindowsSid, winPeer.Scheme);
        Assert.Equal("S-1-5-21-TEST", winPeer.PrincipalId);
        Assert.Equal("windows:S-1-5-21-TEST", winPeer.PrincipalRef);

        var unixPeer = PlatformPeerIdentity.CreateUnix(uid: 1000);
        Assert.Equal(PeerIdentityScheme.UnixUid, unixPeer.Scheme);
        Assert.Equal("1000", unixPeer.PrincipalId);
        Assert.Equal("unix:1000", unixPeer.PrincipalRef);

        var unknownPeer = PlatformPeerIdentity.Unknown;
        Assert.Equal(PeerIdentityScheme.Generic, unknownPeer.Scheme);
        Assert.Equal("unknown", unknownPeer.PrincipalId);
        Assert.Equal("unknown:unknown", unknownPeer.PrincipalRef);
    }

    /// <summary>
    /// Characterizes standard SessionLayoutDescriptor area folder names and layout version.
    /// </summary>
    [Fact]
    public void Characterize_SessionLayoutDescriptor_subdirectories()
    {
        var descriptor = SessionLayoutDescriptor.CreateStandard("test-session-123");
        Assert.Equal(2, descriptor.LayoutVersion);
        Assert.Equal("Raw", descriptor.RawRelativePath);
        Assert.Equal("Evidence", descriptor.EvidenceRelativePath);
        Assert.Equal("Derived", descriptor.DerivedRelativePath);
        Assert.Equal("Exports", descriptor.ExportsRelativePath);
    }
}
