namespace IEM.Core.Ipc;

public enum PeerIdentityScheme
{
    WindowsSid,
    UnixUid,
    Generic,
}

/// <summary>
/// Operational factual provenance of the connected peer identity derived from the transport.
/// Invariants:
/// 84. PLATFORM_PEER_IDENTITY_IS_AUTHENTICATION_PROVENANCE_NOT_AUTHORIZATION
/// 94. CALLER_IDENTITY_IS_DERIVED_FROM_TRANSPORT_NOT_CLIENT_PAYLOAD
/// 95. PLATFORM_CREDENTIAL_FORMAT_NEVER_CHANGES_COMMAND_AUTHORIZATION_SEMANTICS
/// </summary>
public sealed record PlatformPeerIdentity(
    PeerIdentityScheme Scheme,
    string PrincipalId,
    int? ProcessId,
    IReadOnlyList<string> SupplementaryClaims,
    DateTimeOffset CapturedAtUtc,
    string PlatformProvenance)
{
    public const string RoleAdmin = "role:admin";
    public const string RoleOperator = "role:operator";

    public string PrincipalRef => Scheme switch
    {
        PeerIdentityScheme.UnixUid => $"unix:{PrincipalId}",
        PeerIdentityScheme.WindowsSid => $"windows:{PrincipalId}",
        _ => $"unknown:{PrincipalId}"
    };

    public bool HasRole(string role) =>
        SupplementaryClaims.Contains(role, StringComparer.Ordinal);

    public bool IsAdmin => HasRole(RoleAdmin);
    public bool IsOperator => HasRole(RoleOperator);

    public static PlatformPeerIdentity CreateWindows(string sid, int? processId = null, IEnumerable<string>? claims = null) =>
        new(PeerIdentityScheme.WindowsSid, sid, processId, claims?.ToList() ?? new List<string>(), DateTimeOffset.UtcNow, "WindowsNamedPipe");

    public static PlatformPeerIdentity CreateUnix(int uid, int? gid = null, int? pid = null, IEnumerable<string>? claims = null)
    {
        var claimList = claims?.ToList() ?? new List<string>();
        if (gid.HasValue && !claimList.Contains($"gid:{gid.Value}"))
        {
            claimList.Add($"gid:{gid.Value}");
        }
        return new(PeerIdentityScheme.UnixUid, uid.ToString(), pid, claimList, DateTimeOffset.UtcNow, "UnixDomainSocket");
    }

    public static PlatformPeerIdentity Unknown =>
        new(PeerIdentityScheme.Generic, "unknown", null, Array.Empty<string>(), DateTimeOffset.UtcNow, "Unknown");
}

/// <summary>
/// Connection context provided by the platform transport to the command dispatcher.
/// Invariant 83: IPC_TRANSPORT_NEVER_DEFINES_COMMAND_SEMANTICS.
/// </summary>
public sealed class IpcConnectionContext
{
    public string ConnectionId { get; init; } = Guid.NewGuid().ToString("N");
    public PlatformPeerIdentity PeerIdentity { get; init; } = PlatformPeerIdentity.Unknown;
    public Stream Input { get; init; } = Stream.Null;
    public Stream Output { get; init; } = Stream.Null;
    public DateTimeOffset ConnectedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string TransportProvenance { get; init; } = string.Empty;
}

/// <summary>
/// Low-level transport contract. Transport only connects streams and provides peer identity.
/// </summary>
public interface IIpcTransport
{
    string TransportName { get; }

    Task RunAsync(
        Func<IpcConnectionContext, CancellationToken, Task> connectionHandler,
        CancellationToken cancellationToken);
}
