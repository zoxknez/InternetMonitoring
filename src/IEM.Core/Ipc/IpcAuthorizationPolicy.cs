using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Ipc;

public enum AuthorizationOutcome
{
    Allowed,
    Denied,
    Unknown,
}

public sealed record CommandAuthorizationDecision(
    string RequestId,
    string PrincipalRef,
    string CommandName,
    string? SessionId,
    AuthorizationOutcome Outcome,
    IReadOnlyList<string> ReasonCodes,
    string PolicyRef)
{
    public bool IsAllowed => Outcome == AuthorizationOutcome.Allowed;
}

/// <summary>
/// Platform-neutral authorization policy governing commands over IPC.
/// Invariants:
/// 84. PLATFORM_PEER_IDENTITY_IS_AUTHENTICATION_PROVENANCE_NOT_AUTHORIZATION
/// 85. TRANSPORT_ACCESS_NEVER_IMPLIES_COMMAND_AUTHORIZATION
/// 90. UNKNOWN_CALLER_AUTHORIZATION_FAILS_CLOSED
/// 91. AUTHORIZED_COMMAND_NEVER_BYPASSES_SESSION_STATE_INVARIANTS
/// </summary>
public sealed class IpcAuthorizationPolicy
{
    public int PolicyVersion { get; init; } = 1;

    public string PolicyHash => ComputeHash();

    private string ComputeHash()
    {
        var descriptor = $"v={PolicyVersion};auth=ExplicitAllowlist";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }

    public static readonly IpcAuthorizationPolicy Default = new();

    public CommandAuthorizationDecision Evaluate(
        IpcRequestEnvelope request,
        PlatformPeerIdentity peerIdentity,
        string? sessionOwnerPrincipalRef = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(peerIdentity);

        var reasons = new List<string>();
        var principalRef = $"{peerIdentity.Scheme}:{peerIdentity.PrincipalId}";

        // 1. Invariant 90: UNKNOWN_CALLER_AUTHORIZATION_FAILS_CLOSED
        if (peerIdentity.Scheme == PeerIdentityScheme.Generic || string.IsNullOrWhiteSpace(peerIdentity.PrincipalId) || peerIdentity.PrincipalId == "unknown")
        {
            reasons.Add("Identitet pozivaoca nije utvrđen (Unknown peer) -> Fail closed.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Unknown, reasons, PolicyHash);
        }

        // 2. Read-only commands
        if (request.CommandName is "GetServiceStatus" or "GetActiveSession" or "GetSessionStatus")
        {
            reasons.Add($"Autentifikovani korisnik '{principalRef}' ima pravo čitanja statusa.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Allowed, reasons, PolicyHash);
        }

        // 3. StartSession
        if (request.CommandName is "StartSession")
        {
            reasons.Add($"Autentifikovani lokalni korisnik '{principalRef}' ima pravo pokretanja sesije.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Allowed, reasons, PolicyHash);
        }

        // 4. Session-controlling commands: StopSession, FinalizeSession, RetryTimestamp, CreateExport
        if (request.CommandName is "StopSession" or "FinalizeSession" or "RetryTimestamp" or "CreateExport")
        {
            var isOwner = !string.IsNullOrEmpty(sessionOwnerPrincipalRef) &&
                          string.Equals(sessionOwnerPrincipalRef, principalRef, StringComparison.OrdinalIgnoreCase);

            var isAdmin = peerIdentity.SupplementaryClaims.Any(c => c.Contains("Admin", StringComparison.OrdinalIgnoreCase) || c.Contains("root", StringComparison.OrdinalIgnoreCase));

            if (isOwner || isAdmin || string.IsNullOrEmpty(sessionOwnerPrincipalRef))
            {
                reasons.Add(isOwner ? $"Vlasnik sesije '{principalRef}' je autorizovan." : $"Administrator '{principalRef}' je autorizovan.");
                return new CommandAuthorizationDecision(
                    request.RequestId, principalRef, request.CommandName, request.SessionId,
                    AuthorizationOutcome.Allowed, reasons, PolicyHash);
            }

            reasons.Add($"Korisnik '{principalRef}' nije vlasnik sesije '{sessionOwnerPrincipalRef}' niti administrator.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Denied, reasons, PolicyHash);
        }

        // 5. Invariant 91: Modification of sealed evidence or arbitrary execution does not exist
        reasons.Add($"Komanda '{request.CommandName}' nije dozvoljena ili podržana u autorizacionoj matrici.");
        return new CommandAuthorizationDecision(
            request.RequestId, principalRef, request.CommandName, request.SessionId,
            AuthorizationOutcome.Denied, reasons, PolicyHash);
    }
}
