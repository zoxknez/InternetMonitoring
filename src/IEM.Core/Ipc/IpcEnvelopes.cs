using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IEM.Core.Ipc;

public enum IpcResponseStatus
{
    Success,
    Rejected,
    Unauthorized,
    Forbidden,
    InvalidRequest,
    UnsupportedProtocol,
    UnsupportedCommand,
    Conflict,
    NotFound,
    InternalError,
}

/// <summary>
/// Authoritative request envelope for IPC commands.
/// Invariants:
/// 86. UNKNOWN_IPC_PROTOCOL_VERSION_IS_NEVER_SILENTLY_DOWNGRADED
/// 87. UNKNOWN_COMMAND_IS_REJECTED_NOT_GUESSED
/// 89. IPC_EXPOSES_EXPLICIT_COMMANDS_NEVER_ARBITRARY_SERVICE_EXECUTION
/// </summary>
public sealed record IpcRequestEnvelope
{
    public const int CurrentProtocolVersion = 1;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("commandName")]
    public string CommandName { get; init; } = string.Empty;

    [JsonPropertyName("sentAtUtc")]
    public DateTimeOffset SentAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    [JsonPropertyName("clientInstanceId")]
    public string? ClientInstanceId { get; init; }
}

/// <summary>
/// Authoritative response envelope for IPC commands.
/// </summary>
public sealed record IpcResponseEnvelope
{
    public const int CurrentProtocolVersion = 1;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public IpcResponseStatus Status { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    [JsonPropertyName("serviceInstanceId")]
    public string ServiceInstanceId { get; init; } = string.Empty;

    public static IpcResponseEnvelope CreateSuccess(string requestId, string serviceInstanceId, string? payload = null) =>
        new()
        {
            RequestId = requestId,
            Status = IpcResponseStatus.Success,
            Payload = payload,
            ServiceInstanceId = serviceInstanceId,
        };

    public static IpcResponseEnvelope CreateError(string requestId, string serviceInstanceId, IpcResponseStatus status, string errorCode, string errorMessage) =>
        new()
        {
            RequestId = requestId,
            Status = status,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            ServiceInstanceId = serviceInstanceId,
        };
}

/// <summary>
/// Length-prefixed binary framing for explicit, bounded IPC message delivery.
/// Invariant 88: IPC_MESSAGE_BOUNDARY_IS_EXPLICIT_AND_BOUNDED.
/// </summary>
public static class IpcMessageFraming
{
    public const int MaxMessageBytes = 1_048_576; // 1 MB upper bound

    public static async Task WriteFrameAsync(Stream stream, byte[] payloadBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payloadBytes);

        if (payloadBytes.Length > MaxMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBytes), $"Poruka prelazi maksimalni limit od {MaxMessageBytes} bajtova.");
        }

        var lengthHeader = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthHeader, payloadBytes.Length);

        await stream.WriteAsync(lengthHeader, ct).ConfigureAwait(false);
        if (payloadBytes.Length > 0)
        {
            await stream.WriteAsync(payloadBytes, ct).ConfigureAwait(false);
        }
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lengthHeader = new byte[4];
        var readHeader = 0;
        while (readHeader < 4)
        {
            var r = await stream.ReadAsync(lengthHeader.AsMemory(readHeader, 4 - readHeader), ct).ConfigureAwait(false);
            if (r == 0)
            {
                throw new EndOfStreamException("IPC stream je zatvoren pre nego što je pročitan header dužine.");
            }
            readHeader += r;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(lengthHeader);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidOperationException($"Nevalidna dužina poruke: {length} (dozvoljeno: 1 do {MaxMessageBytes}).");
        }

        var payload = new byte[length];
        var readPayload = 0;
        while (readPayload < length)
        {
            var r = await stream.ReadAsync(payload.AsMemory(readPayload, length - readPayload), ct).ConfigureAwait(false);
            if (r == 0)
            {
                throw new EndOfStreamException("IPC stream je neočekivano zatvoren usred čitanja poruke.");
            }
            readPayload += r;
        }

        return payload;
    }
}
