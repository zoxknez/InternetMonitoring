using System.Net.Sockets;
using System.Text.Json;
using IEM.Core.Ipc;

namespace IEM.App.Linux.Hosting;

internal sealed class LinuxIpcClient(string socketPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IpcResponseEnvelope> SendAsync(
        string command,
        string? sessionId,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var request = new IpcRequestEnvelope
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CommandName = command,
            SessionId = sessionId,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload, JsonOptions),
            ClientInstanceId = $"linux-ui-{Environment.ProcessId}",
        };

        await IpcMessageFraming.WriteFrameAsync(
            stream,
            JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions),
            timeout.Token).ConfigureAwait(false);

        var responseBytes = await IpcMessageFraming.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<IpcResponseEnvelope>(responseBytes, JsonOptions)
            ?? throw new IOException("Servis je vratio prazan IPC odgovor.");

        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            throw new IOException("Servis je vratio odgovor za drugi zahtev.");
        }

        return response;
    }

    public static T DeserializePayload<T>(IpcResponseEnvelope response) where T : notnull
    {
        if (response.Status != IpcResponseStatus.Success)
        {
            throw new LinuxIpcException(
                response.ErrorCode ?? response.Status.ToString(),
                response.ErrorMessage ?? "Servis je odbio zahtev.");
        }

        return JsonSerializer.Deserialize<T>(response.Payload ?? string.Empty, JsonOptions)
            ?? throw new IOException("Servisni odgovor ne sadrži očekivane podatke.");
    }
}

internal sealed class LinuxIpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
