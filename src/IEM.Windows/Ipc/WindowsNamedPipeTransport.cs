using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using IEM.Core.Ipc;

namespace IEM.Windows.Ipc;

/// <summary>
/// Windows Named Pipe transport implementation with authenticated caller SID extraction.
/// Invariants:
/// 83. IPC_TRANSPORT_NEVER_DEFINES_COMMAND_SEMANTICS
/// 84. PLATFORM_PEER_IDENTITY_IS_AUTHENTICATION_PROVENANCE_NOT_AUTHORIZATION
/// 94. CALLER_IDENTITY_IS_DERIVED_FROM_TRANSPORT_NOT_CLIENT_PAYLOAD
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNamedPipeTransport : IIpcTransport
{
    public const string DefaultPipeName = "IEM_Service_Pipe";
    private readonly string _pipeName;

    public WindowsNamedPipeTransport(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    public string TransportName => "WindowsNamedPipe";

    public async Task RunAsync(
        Func<IpcConnectionContext, CancellationToken, Task> connectionHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionHandler);

        while (!cancellationToken.IsCancellationRequested)
        {
            var pipeSecurity = CreatePipeSecurity();

            var serverStream = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 65536,
                outBufferSize: 65536,
                pipeSecurity: pipeSecurity);

            try
            {
                await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Derive peer identity from the authenticated Windows pipe connection (Invariant 94)
                var peerIdentity = DerivePeerIdentity(serverStream);

                var context = new IpcConnectionContext
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    PeerIdentity = peerIdentity,
                    Input = serverStream,
                    Output = serverStream,
                    ConnectedAtUtc = DateTimeOffset.UtcNow,
                    TransportProvenance = TransportName,
                };

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (serverStream)
                        {
                            await connectionHandler(context, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // Connection closed or faulted
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                serverStream.Dispose();
                break;
            }
            catch
            {
                serverStream.Dispose();
            }
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var ps = new PipeSecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(users, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        return ps;
    }

    private static PlatformPeerIdentity DerivePeerIdentity(NamedPipeServerStream pipe)
    {
        PlatformPeerIdentity? resolvedIdentity = null;

        try
        {
            pipe.RunAsClient(() =>
            {
                var identity = WindowsIdentity.GetCurrent();
                if (identity.User is not null)
                {
                    var sid = identity.User.Value;
                    var principal = new WindowsPrincipal(identity);
                    var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;

                    var claims = new List<string> { PlatformPeerIdentity.RoleOperator };
                    if (isAdmin)
                    {
                        claims.Add(PlatformPeerIdentity.RoleAdmin);
                    }

                    resolvedIdentity = PlatformPeerIdentity.CreateWindows(sid, processId: null, claims);
                }
            });
        }
        catch
        {
            // Impersonation query failed
        }

        if (resolvedIdentity is not null)
        {
            return resolvedIdentity;
        }

        // Secondary fallback using GetImpersonationUserName if RunAsClient was restricted
        try
        {
            var userName = pipe.GetImpersonationUserName();
            if (!string.IsNullOrWhiteSpace(userName))
            {
                var account = new NTAccount(userName);
                var sid = account.Translate(typeof(SecurityIdentifier)).Value;
                var claims = new List<string> { PlatformPeerIdentity.RoleOperator };
                return PlatformPeerIdentity.CreateWindows(sid, processId: null, claims);
            }
        }
        catch
        {
        }

        // Invariant 90 & M6: Never fabricate a fake SID. Fail closed on unknown peer.
        return PlatformPeerIdentity.Unknown;
    }
}
