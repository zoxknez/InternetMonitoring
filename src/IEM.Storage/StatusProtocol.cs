using System.Text.Json;
using System.Text.Json.Serialization;

namespace IEM.Storage;

/// <summary>
/// The wire shape of the status channel, defined once for both ends.
/// <para>
/// It used to be written out twice - a request shape in the service, an envelope in the
/// window - which is how the two halves of a protocol drift apart without anyone noticing:
/// the tests that would have caught it could not be written, because neither half could be
/// exercised without a running service and a named pipe. Here it is ordinary code that
/// parses and serialises text, and the rules that matter - what an unknown command does,
/// what a missing version means, what happens to fields a reader has never heard of - are
/// testable in the same way as anything else.
/// </para>
/// <para>
/// One JSON object per line, request then response. Plain enough to drive from a script or
/// read in a text editor while diagnosing, which is the other reason it is shaped this way.
/// </para>
/// </summary>
public static class StatusProtocol
{
    /// <summary>
    /// Commands the service answers. Adding one does not raise the protocol version:
    /// readers ignore what they do not recognise, and an older window simply never asks.
    /// </summary>
    public static IReadOnlyList<string> Commands { get; } = ["STATUS", "LIVE", "SPEED", "PING", "HELLO"];

    /// <summary>Reply carries something the client can act on.</summary>
    public const string Supported = "SUPPORTED";

    /// <summary>The two ends do not speak the same protocol, which is not the same as a failure.</summary>
    public const string Incompatible = "INCOMPATIBLE";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        // States as names, not ordinals. A number here would force every reader to keep a
        // private copy of the enum, and would silently change meaning the day a state is
        // inserted in the middle.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Reads a request line, in either shape it may arrive in.
    /// <para>
    /// A bare word is accepted alongside the JSON object so the channel can be driven from a
    /// console while diagnosing. Anything unreadable becomes a request with no command, which
    /// the server answers rather than dropping - a client that gets no reply cannot tell that
    /// apart from the service being down, and sends its user to reinstall something that works.
    /// </para>
    /// </summary>
    public static StatusRequest ParseRequest(string? line)
    {
        var text = line?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return new StatusRequest(null, null);
        }

        if (!text.StartsWith('{'))
        {
            return new StatusRequest(text, null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            var command = root.TryGetProperty("command", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

            // Absent means a client older than versioning itself. Served rather than refused:
            // it can only be speaking version one, since there was nothing else to speak.
            var version = root.TryGetProperty("protocolVersion", out var declared) && declared.TryGetInt32(out var parsed)
                ? parsed
                : (int?)null;

            return new StatusRequest(command, version);
        }
        catch (JsonException)
        {
            return new StatusRequest(null, null);
        }
    }
}

/// <param name="ProtocolVersion">
/// Null from a client that predates versioning, which is treated as version one.
/// </param>
public sealed record StatusRequest(string? Command, int? ProtocolVersion)
{
    /// <summary>The command in the form the server matches on.</summary>
    public string? Normalised => Command?.ToUpperInvariant();

    /// <summary>Whether this end can usefully talk to whoever sent it.</summary>
    public bool SpeaksOurProtocol =>
        ProtocolVersion is not { } theirs || ServiceContract.SupportsProtocol(theirs);

    /// <summary>
    /// What to tell a client speaking another version.
    /// <para>
    /// Said plainly rather than left to fail on an unrecognised field: told "not running", a
    /// user reinstalls something that works perfectly well; told the two halves are different
    /// versions, they update the other half.
    /// </para>
    /// </summary>
    public string IncompatibilityMessage =>
        $"Interfejs govori verziju protokola {ProtocolVersion}, a servis {ServiceContract.ProtocolVersion}. " +
        $"Ažurirajte obe strane na istu verziju aplikacije (servis je {ServiceContract.AppVersion}).";

    /// <summary>The request as one line, with the version travelling on every request.</summary>
    public string ToLine() => JsonSerializer.Serialize(
        new { command = Command, protocolVersion = ProtocolVersion ?? ServiceContract.ProtocolVersion },
        StatusProtocol.Json);

    /// <summary>A request for a command, stamped with this build's protocol version.</summary>
    public static StatusRequest For(string command) =>
        new(command, ServiceContract.ProtocolVersion);
}

/// <param name="Status">
/// <see cref="StatusProtocol.Supported"/> on any answer the client can act on,
/// <see cref="StatusProtocol.Incompatible"/> when the two ends do not speak the same
/// protocol. Distinct from <see cref="Success"/>, which says whether the command worked - a
/// client needs to tell "that failed" apart from "we cannot usefully talk at all".
/// </param>
public sealed record StatusResponse(bool Success, object? Data, string? Message, string Status)
{
    public int ProtocolVersion => ServiceContract.ProtocolVersion;

    public string AppVersion => ServiceContract.AppVersion;

    public static StatusResponse Ok(object data) => new(true, data, null, StatusProtocol.Supported);

    public static StatusResponse Error(string message) => new(false, null, message, StatusProtocol.Supported);

    public static StatusResponse Refused(string message) => new(false, null, message, StatusProtocol.Incompatible);

    /// <summary>What an unknown command is answered with, rather than silence.</summary>
    public static StatusResponse Unknown(string? command) => Error(
        $"Nepoznata komanda '{command}'. Podržane: {string.Join(", ", StatusProtocol.Commands)}.");

    public string ToLine() => JsonSerializer.Serialize(this, StatusProtocol.Json);
}

/// <summary>
/// A reply as the reading end sees it.
/// <para>
/// Deliberately tolerant: <see cref="Status"/> is absent on a reply from a service that
/// predates versioning, and every field a newer service adds is ignored rather than fatal.
/// A window that refused to read a reply because it carried one field too many would report
/// a working service as unreachable.
/// </para>
/// </summary>
public sealed record StatusEnvelope<T>(
    bool Success,
    T? Data,
    string? Message,
    string? Status = null,
    int ProtocolVersion = 1,
    string? AppVersion = null)
{
    /// <summary>The two ends do not speak the same protocol, whatever else the reply says.</summary>
    public bool IsIncompatible =>
        string.Equals(Status, StatusProtocol.Incompatible, StringComparison.Ordinal);

    public static StatusEnvelope<T>? Parse(string line) =>
        JsonSerializer.Deserialize<StatusEnvelope<T>>(line, StatusProtocol.Json);
}
