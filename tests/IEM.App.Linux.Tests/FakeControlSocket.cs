using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using IEM.Core;
using IEM.Core.Ipc;
using IEM.Service.Runtime;

namespace IEM.App.Linux.Tests;

/// <summary>
/// A stand-in for the installed service's <c>control.sock</c>. It speaks the real IPC framing
/// and records every command name it is asked to run, so a test can prove what the shell does
/// and - the point of the 3.1-10 gate - what it never sends.
/// </summary>
internal sealed class FakeControlSocket : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Socket _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly Task _acceptLoop;

    public FakeControlSocket()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"iem-{Guid.NewGuid():N}"[..18] + ".sock");

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(Path));
        _listener.Listen(16);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    public string Path { get; }

    public IReadOnlyList<string> ReceivedCommands => _commands.ToArray();

    public bool Received(string command) => _commands.Contains(command);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(connection, cancellationToken), CancellationToken.None);
        }
    }

    private async Task ServeAsync(Socket connection, CancellationToken cancellationToken)
    {
        using (connection)
        await using (var stream = new NetworkStream(connection, ownsSocket: false))
        {
            try
            {
                var frame = await IpcMessageFraming.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<IpcRequestEnvelope>(frame, Json)
                    ?? throw new JsonException("empty request");
                _commands.Enqueue(request.CommandName);

                var response = Respond(request);
                await IpcMessageFraming.WriteFrameAsync(
                    stream,
                    JsonSerializer.SerializeToUtf8Bytes(response, Json),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or OperationCanceledException or JsonException ||
                cancellationToken.IsCancellationRequested)
            {
                // A client that hangs up mid-frame during teardown is expected.
            }
        }
    }

    private static IpcResponseEnvelope Respond(IpcRequestEnvelope request) => request.CommandName switch
    {
        "GetServiceStatus" => IpcResponseEnvelope.CreateSuccess(
            request.RequestId,
            "fake-service",
            JsonSerializer.Serialize(
                new
                {
                    Status = ServiceStatus.Idle,
                    SpeedStatus = SpeedStatus.Idle,
                    Snapshot = MonitorSnapshot.Empty,
                },
                Json)),

        "StartSession" => IpcResponseEnvelope.CreateSuccess(
            request.RequestId, "fake-service", "{}", sessionId: "S-fake-0001"),

        "FinalizeSession" => IpcResponseEnvelope.CreateSuccess(
            request.RequestId, "fake-service", "{}"),

        _ => IpcResponseEnvelope.CreateError(
            request.RequestId,
            "fake-service",
            IpcResponseStatus.UnsupportedCommand,
            "UNSUPPORTED_COMMAND",
            request.CommandName),
    };

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown race - nothing to salvage.
        }

        _shutdown.Dispose();

        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
