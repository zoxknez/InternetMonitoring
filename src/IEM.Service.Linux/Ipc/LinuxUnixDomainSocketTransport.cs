using System.Net.Sockets;
using System.Runtime.InteropServices;
using IEM.Core.Ipc;
using IEM.Service.Linux.Lifecycle;
using Microsoft.Extensions.Logging;

namespace IEM.Service.Linux.Ipc;

/// <summary>
/// Production Linux AF_UNIX pathname transport on /run/internet-evidence-monitor/control.sock.
/// Implements safe bind, stale socket cleanup, permission enforcement (0660 iem:iem-users),
/// and kernel-level peer authentication.
/// Invariants 83, 84, 85, 94, 95, 261-268.
/// </summary>
public sealed class LinuxUnixDomainSocketTransport : IIpcTransport, IDisposable
{
    public const string DefaultSocketPath = "/run/internet-evidence-monitor/control.sock";
    public const string DefaultRuntimeDir = "/run/internet-evidence-monitor";
    public const string TargetGroupName = "iem-users";

    private readonly string _socketPath;
    private readonly string _runtimeDir;
    private readonly ILogger<LinuxUnixDomainSocketTransport>? _logger;
    private Socket? _listener;

    public LinuxUnixDomainSocketTransport(
        string socketPath = DefaultSocketPath,
        string runtimeDir = DefaultRuntimeDir,
        ILogger<LinuxUnixDomainSocketTransport>? logger = null)
    {
        _socketPath = socketPath;
        _runtimeDir = runtimeDir;
        _logger = logger;
    }

    public string TransportName => "LinuxUnixDomainSocket";
    public string SocketPath => _socketPath;

    public async Task RunAsync(
        Func<IpcConnectionContext, CancellationToken, Task> connectionHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionHandler);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _logger?.LogWarning("LinuxUnixDomainSocketTransport nije pokrenut jer okruženje nije Linux.");
            return;
        }

        // 1. Safe Pre-Bind & Stale Cleanup (§11.2 & §11.3)
        PrepareAndBindSocket();

        if (_listener is null)
        {
            throw new InvalidOperationException("Neuspešna inicijalizacija osluškivača na " + _socketPath);
        }

        _logger?.LogInformation("IPC kontrolni soket je aktivan na {SocketPath} (0660 iem:{Group})", _socketPath, TargetGroupName);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);

                // Derive peer identity from kernel credentials (Invariant 94)
                var peerIdentity = LinuxPeerIdentityResolver.Resolve(clientSocket);

                var stream = new NetworkStream(clientSocket, ownsSocket: true);

                var context = new IpcConnectionContext
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    PeerIdentity = peerIdentity,
                    Input = stream,
                    Output = stream,
                    ConnectedAtUtc = DateTimeOffset.UtcNow,
                    TransportProvenance = TransportName,
                };

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (stream)
                        {
                            await connectionHandler(context, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogDebug(ex, "Konekcija {ConnectionId} ({PrincipalRef}) je zatvorena uz grešku.",
                            context.ConnectionId, context.PeerIdentity.PrincipalRef);
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (cancellationToken.IsCancellationRequested) break;
                _logger?.LogError(ex, "Greška prilikom prihvatanja IPC konekcije.");
            }
        }
    }

    private void PrepareAndBindSocket()
    {
        // 1. Validate parent runtime directory
        var runtimePrep = LinuxRuntimeDirectoryPreparer.Prepare(_runtimeDir, posix: null, _logger);
        if (!runtimePrep.IsValid)
        {
            throw new InvalidOperationException($"Bezbednosna greška pre kreiranja soketa: {runtimePrep.Error}");
        }

        // 2. Safe Stale Cleanup (§11.3)
        if (File.Exists(_socketPath))
        {
            var lstatBuf = new byte[256];
            if (LinuxSocketInterop.lstat(_socketPath, lstatBuf) != 0)
            {
                throw new InvalidOperationException($"Neuspešan lstat nad postojećim fajlom '{_socketPath}' -> Fail closed.");
            }

            // On 64-bit Linux (x86_64 / arm64), st_mode is at offset 24, st_uid at offset 28
            var mode = BitConverter.ToUInt32(lstatBuf, 24);
            var ownerUid = BitConverter.ToUInt32(lstatBuf, 28);
            var currentUid = RealPosixEnvironment.Instance.GetCurrentUid();

            var isSocket = (mode & LinuxSocketInterop.S_IFMT) == LinuxSocketInterop.S_IFSOCK;
            var isSymlink = (mode & LinuxSocketInterop.S_IFMT) == LinuxSocketInterop.S_IFLNK;

            if (!isSocket || isSymlink || ownerUid != currentUid)
            {
                throw new InvalidOperationException($"Putanja '{_socketPath}' postoji ali nije regularan soket u vlasništvu procesa (isSock={isSocket}, isLnk={isSymlink}, owner={ownerUid} != {currentUid}) -> Fail closed.");
            }

            // Test if another active daemon is listening
            if (IsActiveDaemonListening(_socketPath))
            {
                throw new InvalidOperationException($"Drugi aktivan proces već osluškuje na '{_socketPath}'. Odbija se preuzimanje soketa.");
            }

            // Stale dead socket: unlink ONLY this verified inode
            if (LinuxSocketInterop.unlink(_socketPath) != 0)
            {
                throw new InvalidOperationException($"Neuspešno brisanje zastarelog soketa '{_socketPath}'.");
            }
        }

        // 3. Bind
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));

        // 4. Post-bind permissions (0660 iem:iem-users)
        LinuxSocketInterop.chmod(_socketPath, 0x1B0); // 0660 in octal (0110 110 000 = 432 = 0x1B0)

        var grpPtr = LinuxSocketInterop.getgrnam(TargetGroupName);
        if (grpPtr != IntPtr.Zero)
        {
            var gidOffset = 2 * IntPtr.Size;
            var targetGid = Marshal.ReadInt32(grpPtr, gidOffset);
            if (targetGid >= 0)
            {
                LinuxSocketInterop.chown(_socketPath, -1, targetGid);
            }
        }

        // 5. Listen
        _listener.Listen(128);
    }

    private static bool IsActiveDaemonListening(string socketPath)
    {
        try
        {
            using var testSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            testSocket.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true; // Another process responded
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable or SocketError.HostUnreachable)
        {
            return false; // Dead / stale socket
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _listener?.Dispose();
            _listener = null;
        }
        catch
        {
        }
    }
}
