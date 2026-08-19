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
/// Platform-neutral authorization policy governing commands over IPC (Version 2).
/// Invariants:
/// 84. PLATFORM_PEER_IDENTITY_IS_AUTHENTICATION_PROVENANCE_NOT_AUTHORIZATION
/// 85. TRANSPORT_ACCESS_NEVER_IMPLIES_COMMAND_AUTHORIZATION
/// 90. UNKNOWN_CALLER_AUTHORIZATION_FAILS_CLOSED
/// 91. AUTHORIZED_COMMAND_NEVER_BYPASSES_SESSION_STATE_INVARIANTS
/// 94. CALLER_IDENTITY_IS_DERIVED_FROM_TRANSPORT_NOT_CLIENT_PAYLOAD
/// 95. PLATFORM_CREDENTIAL_FORMAT_NEVER_CHANGES_COMMAND_AUTHORIZATION_SEMANTICS
/// </summary>
public sealed class IpcAuthorizationPolicy
{
    public const int PolicyVersion = 2;

    public string PolicyHash => ComputeHash();

    private static string ComputeHash()
    {
        const string descriptor = "v=2;matrix=StrictCanonicalRoles;roles=role:operator,role:admin;layer5=FailClosedMissingOwner;comparison=Ordinal";
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
        var principalRef = peerIdentity.PrincipalRef;

        // Layer 2 / Invariant 90: UNKNOWN_CALLER_AUTHORIZATION_FAILS_CLOSED
        if (peerIdentity.Scheme == PeerIdentityScheme.Generic ||
            string.IsNullOrWhiteSpace(peerIdentity.PrincipalId) ||
            peerIdentity.PrincipalId == "unknown")
        {
            reasons.Add("Identitet pozivaoca nije utvrđen (Unknown peer) -> Fail closed.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Unknown, reasons, PolicyHash);
        }

        // Layer 3: Canonical role requirement (Filesystem admission != command allow)
        var isOperator = peerIdentity.IsOperator;
        var isAdmin = peerIdentity.IsAdmin;

        if (!isOperator && !isAdmin)
        {
            reasons.Add($"Autentifikovani pozivalac '{principalRef}' ne poseduje potrebne kanonske role (role:operator / role:admin) -> Odbijeno.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Denied, reasons, PolicyHash);
        }

        // Layer 4: Read-only status inspection commands
        if (request.CommandName is "GetServiceStatus" or "GetActiveSession" or "GetSessionStatus")
        {
            reasons.Add($"Korisnik '{principalRef}' sa ulogom {(isAdmin ? "role:admin" : "role:operator")} je autorizovan za čitanje statusa.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Allowed, reasons, PolicyHash);
        }

        // Layer 4: StartSession
        if (request.CommandName is "StartSession")
        {
            reasons.Add($"Korisnik '{principalRef}' sa ulogom {(isAdmin ? "role:admin" : "role:operator")} je autorizovan za pokretanje sesije.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Allowed, reasons, PolicyHash);
        }

        // Layer 5: Session mutation commands: StopSession, FinalizeSession, RetryTimestamp, CreateExport
        if (request.CommandName is "StopSession" or "FinalizeSession" or "RetryTimestamp" or "CreateExport")
        {
            // Missing session owner must FAIL CLOSED
            if (string.IsNullOrWhiteSpace(sessionOwnerPrincipalRef))
            {
                reasons.Add("Vlasnik sesije nije zabeležen ili nedostaje -> Odbijeno (Fail closed).");
                return new CommandAuthorizationDecision(
                    request.RequestId, principalRef, request.CommandName, request.SessionId,
                    AuthorizationOutcome.Denied, reasons, PolicyHash);
            }

            // Session owner check (strict ordinal string comparison per roadmap)
            var isOwner = string.Equals(sessionOwnerPrincipalRef, principalRef, StringComparison.Ordinal);

            if (isOwner)
            {
                reasons.Add($"Vlasnik sesije '{principalRef}' je autorizovan za upravljanje sesijom.");
                return new CommandAuthorizationDecision(
                    request.RequestId, principalRef, request.CommandName, request.SessionId,
                    AuthorizationOutcome.Allowed, reasons, PolicyHash);
            }

            if (isAdmin)
            {
                reasons.Add($"Administrator '{principalRef}' (role:admin) je autorizovan putem administratorskog prekoračenja.");
                return new CommandAuthorizationDecision(
                    request.RequestId, principalRef, request.CommandName, request.SessionId,
                    AuthorizationOutcome.Allowed, reasons, PolicyHash);
            }

            reasons.Add($"Korisnik '{principalRef}' nije vlasnik sesije '{sessionOwnerPrincipalRef}' niti administrator.");
            return new CommandAuthorizationDecision(
                request.RequestId, principalRef, request.CommandName, request.SessionId,
                AuthorizationOutcome.Denied, reasons, PolicyHash);
        }

        // Layer 4 catch-all: Unknown command denied
        reasons.Add($"Komanda '{request.CommandName}' nije dozvoljena u autorizacionoj matrici.");
        return new CommandAuthorizationDecision(
            request.RequestId, principalRef, request.CommandName, request.SessionId,
            AuthorizationOutcome.Denied, reasons, PolicyHash);
    }
}
