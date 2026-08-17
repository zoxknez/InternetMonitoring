using System.Text.Json;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// Writes a payload the way the chain does and reads the result back as JSON.
/// <para>
/// The point is to exercise the writer and the reader against each other rather than
/// against a hand-written fixture. A fixture would keep passing after a field was renamed
/// on one side only, which is exactly the failure that would leave a session unreadable
/// with no test complaining.
/// </para>
/// </summary>
internal static class EvidenceRoundTrip
{
    public static JsonElement Through(IEvidencePayload payload)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            payload.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);

        // Cloned so it outlives the document being disposed.
        return document.RootElement.Clone();
    }
}
