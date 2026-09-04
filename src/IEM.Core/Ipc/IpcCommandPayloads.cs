using System.Text.Json.Serialization;

namespace IEM.Core.Ipc;

/// <summary>
/// Version-one payload for <c>StartSession</c>. Duration uses the same invariant text
/// accepted by the runtime (for example <c>48h</c>, <c>7d</c>, or <c>infinite</c>).
/// </summary>
public sealed record StartSessionCommandPayload
{
    [JsonPropertyName("duration")]
    public string Duration { get; init; } = "48h";

    [JsonPropertyName("interfaceName")]
    public string? InterfaceName { get; init; }
}
