using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IEM.Storage.Evidence;

/// <param name="Payload">The <c>p</c> object, still as JSON.</param>
public sealed record ChainEntry(EvidenceKind Kind, long EntryNumber, JsonElement Payload);

/// <summary>
/// Reads a chain back.
/// <para>
/// Kept separate from <see cref="ChainVerifier"/> on purpose. The verifier must never
/// parse a payload, so that a log written by an older build still verifies after the
/// payload schema has moved on. Reading is a different job with different rules: it
/// understands the schema and tolerates fields it does not recognise.
/// </para>
/// </summary>
public static class ChainReader
{
    /// <summary>
    /// Enumerates entries, skipping anything malformed.
    /// <para>
    /// Skipping rather than throwing is deliberate: a truncated final line is the normal
    /// aftermath of a crash, and refusing to read the log because of it would deny access
    /// to the very evidence that survived.
    /// </para>
    /// </summary>
    public static IEnumerable<ChainEntry> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            yield break;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            ChainEntry? entry;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (!root.TryGetProperty("k", out var kindElement) ||
                    !Enum.TryParse<EvidenceKind>(kindElement.GetString(), out var kind) ||
                    !root.TryGetProperty("p", out var payload))
                {
                    continue;
                }

                var number = root.TryGetProperty("n", out var n) &&
                             n.ValueKind == JsonValueKind.Number &&
                             n.TryGetInt64(out var parsed)
                    ? parsed
                    : -1;

                // Cloned so the element outlives the JsonDocument being disposed.
                entry = new ChainEntry(kind, number, payload.Clone());
            }
            catch (JsonException)
            {
                continue;
            }

            yield return entry;
        }
    }

    /// <summary>Reads the opening entry, or null if the log has none.</summary>
    public static SessionStartRecord? ReadSessionStart(string path)
    {
        foreach (var entry in Read(path))
        {
            if (entry.Kind != EvidenceKind.SessionStart)
            {
                continue;
            }

            var payload = entry.Payload;
            var schema = payload.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var v)
                ? v
                : IEM.Core.Model.EvidenceModelVersion.LegacySchemaVersion;

            return new SessionStartRecord(
                GetString(payload, "sessionId") ?? string.Empty,
                GetString(payload, "toolVersion") ?? string.Empty,
                ParseDate(GetString(payload, "startedUtc")),
                ParseDuration(GetString(payload, "plannedDuration")),
                GetString(payload, "machine") ?? string.Empty,
                GetString(payload, "interface") ?? string.Empty,
                GetString(payload, "gateway"),
                GetString(payload, "interfaceId"),
                schema);
        }

        return null;
    }

    /// <summary>
    /// Whether the log carries a closing entry.
    /// <para>
    /// This is what distinguishes a session that ended from one that was interrupted, and
    /// it is read from the chain rather than the database because the chain is the record
    /// and the database is only an index of it.
    /// </para>
    /// </summary>
    public static bool IsClosed(string path) => Read(path).Any(e => e.Kind == EvidenceKind.SessionEnd);

    /// <summary>Wall-clock time of the last sample, used to size the interruption gap.</summary>
    public static DateTimeOffset? LastSampleUtc(string path)
    {
        DateTimeOffset? last = null;

        foreach (var entry in Read(path))
        {
            if (entry.Kind == EvidenceKind.Sample && GetString(entry.Payload, "utc") is { } utc)
            {
                last = ParseDate(utc);
            }
        }

        return last;
    }

    /// <summary>Highest sample sequence, so numbering continues rather than restarting.</summary>
    public static long LastSampleSequence(string path)
    {
        long last = 0;

        foreach (var entry in Read(path))
        {
            if (entry.Kind == EvidenceKind.Sample &&
                entry.Payload.TryGetProperty("n", out var n) &&
                n.TryGetInt64(out var value))
            {
                last = Math.Max(last, value);
            }
        }

        return last;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset ParseDate(string? value) =>
        value is not null && DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : default;

    private static TimeSpan ParseDuration(string? value)
    {
        if (string.Equals(value, "infinite", StringComparison.Ordinal))
        {
            return Timeout.InfiniteTimeSpan;
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : TimeSpan.Zero;
    }
}

/// <summary>The opening entry, read back.</summary>
public sealed record SessionStartRecord(
    string SessionId,
    string ToolVersion,
    DateTimeOffset StartedUtc,
    TimeSpan PlannedDuration,
    string MachineName,
    string InterfaceName,
    string? GatewayAddress,
    string? InterfaceId = null,
    int SchemaVersion = IEM.Core.Model.EvidenceModelVersion.LegacySchemaVersion);
