using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IEM.Storage;

/// <summary>
/// A standing instruction to run one monitoring session.
/// <para>
/// This file is what makes the service's behaviour predictable across restarts. Without
/// it, a service set to start automatically would have no way to tell "continue the test
/// I was asked for" from "the machine rebooted after that test finished" - and would
/// quietly begin a fresh two-day session every time the computer came back on.
/// </para>
/// <para>
/// The request is created when someone asks for a test, survives reboots alongside the
/// session it belongs to, and is removed once that session reaches its planned end.
/// </para>
/// </summary>
public sealed record SessionRequest(
    TimeSpan Duration,
    string? Interface,
    DateTimeOffset RequestedAt)
{
    private const string FileName = "zahtev.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string outputRoot) => Path.Combine(outputRoot, FileName);

    /// <summary>Reads the pending request, or null if there is none.</summary>
    public static SessionRequest? Read(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

        var path = PathFor(outputRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredRequest>(File.ReadAllText(path), JsonOptions);
            if (stored is null)
            {
                return null;
            }

            var duration = string.Equals(stored.Duration, "infinite", StringComparison.Ordinal)
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.TryParse(stored.Duration, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : TimeSpan.FromHours(48);

            return new SessionRequest(duration, stored.Interface, stored.RequestedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt request is treated as no request. Guessing at what someone meant
            // and then running for two days on that guess would be worse.
            return null;
        }
    }

    public void Write(string outputRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        Directory.CreateDirectory(outputRoot);

        var stored = new StoredRequest(
            Duration == Timeout.InfiniteTimeSpan ? "infinite" : Duration.ToString("c", CultureInfo.InvariantCulture),
            Interface,
            RequestedAt);

        File.WriteAllText(
            PathFor(outputRoot),
            JsonSerializer.Serialize(stored, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Removes the request once its session has run to its planned end.</summary>
    public static void Clear(string outputRoot)
    {
        var path = PathFor(outputRoot);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Leaving a stale request behind is recoverable; throwing here would turn a
            // successfully completed session into a failure.
        }
    }

    private sealed record StoredRequest(string Duration, string? Interface, DateTimeOffset RequestedAt);
}
