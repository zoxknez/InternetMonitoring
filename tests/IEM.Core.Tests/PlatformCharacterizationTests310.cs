using IEM.Core.Ipc;
using IEM.Core.Model;
using IEM.Storage;
using IEM.Storage.Layout;

namespace IEM.Core.Tests;

/// <summary>
/// Characterization tests freezing current 3.0 (CURRENT_BEHAVIOR) platform contracts and quirks.
/// Invariants / Phase 3.1-0 baseline validation.
/// These tests capture exactly how 3.0 behaves today so that future refactorings (3.1-1 onwards)
/// do not inadvertently change Windows semantics.
/// </summary>
public sealed class PlatformCharacterizationTests310
{
    private readonly IpcAuthorizationPolicy _policy = IpcAuthorizationPolicy.Default;

    /// <summary>
    /// Characterizes 3.0 policy version and default allowlist behavior.
    /// </summary>
    [Fact]
    public void Policy_version_is_1_in_3_0_baseline()
    {
        Assert.Equal(1, _policy.PolicyVersion);
        Assert.False(string.IsNullOrWhiteSpace(_policy.PolicyHash));
    }

    /// <summary>
    /// CURRENT_BEHAVIOR != TARGET_3_1
    /// In 3.0, if sessionOwnerPrincipalRef is empty or null, any authenticated peer is ALLOWED
    /// to invoke StopSession / FinalizeSession / RetryTimestamp / CreateExport.
    /// (In 3.1, this will fail-closed per Invariant 264 MISSING_SESSION_OWNER_FAILS_CLOSED).
    /// </summary>
    [Fact]
    public void Characterize_3_0_empty_session_owner_allows_control_commands_CURRENT_BEHAVIOR()
    {
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-ARBITRARY-USER");
        var req = new IpcRequestEnvelope
        {
            RequestId = "req-char-1",
            CommandName = "StopSession",
            SessionId = "session-test",
        };

        // When sessionOwnerPrincipalRef is null or empty in 3.0:
        var decision = _policy.Evaluate(req, peer, sessionOwnerPrincipalRef: null);

        // Characterize: 3.0 CURRENT_BEHAVIOR evaluates to Allowed
        Assert.True(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
    }

    /// <summary>
    /// CURRENT_BEHAVIOR != TARGET_3_1
    /// In 3.0, admin role evaluation checks substring "Admin" or "root" in SupplementaryClaims.
    /// (In 3.1, this will require exact canonical claim "role:admin" per Invariant 265).
    /// </summary>
    [Fact]
    public void Characterize_3_0_admin_substring_claim_matching_CURRENT_BEHAVIOR()
    {
        // Peer with claim containing "DomainAdmins"
        var peerWithSubstr = PlatformPeerIdentity.CreateWindows(
            "S-1-5-21-999",
            claims: ["DomainAdmins"]);

        var req = new IpcRequestEnvelope
        {
            RequestId = "req-char-2",
            CommandName = "FinalizeSession",
            SessionId = "session-test",
        };

        // Different owner, but caller has substring "Admin" in supplementary claim
        var decision = _policy.Evaluate(req, peerWithSubstr, sessionOwnerPrincipalRef: "WindowsSid:S-1-5-21-DIFFERENT-OWNER");

        // Characterize: 3.0 CURRENT_BEHAVIOR evaluates to Allowed via substring match
        Assert.True(decision.IsAllowed);
        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
    }

    /// <summary>
    /// Characterizes that unknown peer identity fails closed in 3.0.
    /// </summary>
    [Fact]
    public void Unknown_peer_fails_closed_in_3_0_baseline()
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
    /// Characterizes read-only commands authorization for any authenticated peer in 3.0.
    /// </summary>
    [Theory]
    [InlineData("GetServiceStatus")]
    [InlineData("GetActiveSession")]
    [InlineData("GetSessionStatus")]
    [InlineData("StartSession")]
    public void Standard_user_authorized_for_read_and_start_in_3_0(string commandName)
    {
        var peer = PlatformPeerIdentity.CreateWindows("S-1-5-21-STANDARD-USER");
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
    /// Characterizes PlatformPeerIdentity schemes and principal reference formatting.
    /// </summary>
    [Fact]
    public void Characterize_PlatformPeerIdentity_schemes()
    {
        var winPeer = PlatformPeerIdentity.CreateWindows("S-1-5-21-TEST");
        Assert.Equal(PeerIdentityScheme.WindowsSid, winPeer.Scheme);
        Assert.Equal("S-1-5-21-TEST", winPeer.PrincipalId);

        var unixPeer = PlatformPeerIdentity.CreateUnix(uid: 1000);
        Assert.Equal(PeerIdentityScheme.UnixUid, unixPeer.Scheme);
        Assert.Equal("1000", unixPeer.PrincipalId);

        var unknownPeer = PlatformPeerIdentity.Unknown;
        Assert.Equal(PeerIdentityScheme.Generic, unknownPeer.Scheme);
        Assert.Equal("unknown", unknownPeer.PrincipalId);
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
