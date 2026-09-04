using System.Text.Json;

namespace IEM.Core.Tests.Parity;

public sealed class ParityFixtureFormatException(string message) : Exception(message);

/// <summary>Which kind of parity a fixture asserts (roadmap 25.3).</summary>
public enum ParityFixtureKind
{
    /// <summary>Both projections must yield an identical CanonicalParityView; no divergences.</summary>
    Symmetric,

    /// <summary>One side sees less about the same event; every difference is listed with a reason.</summary>
    AsymmetricObservability,

    /// <summary>Deliberately hostile or borderline input (TOCTOU, skip, suspend).</summary>
    Adversarial,
}

/// <summary>One explicitly permitted difference between the Windows and Linux canonical views.</summary>
public sealed record AllowedDivergence(string Path, JsonElement Windows, JsonElement Linux, string Reason);

/// <summary>A per-fixture whitelist entry for a diagnostic field that leaked into the view.</summary>
public sealed record PathWhitelistEntry(string Path, string Reason);

/// <summary>
/// The parsed form of <c>tests/IEM.Core.Tests/Fixtures/Parity/v1/&lt;fixtureId&gt;.json</c>.
/// The three payload parts stay as raw JSON here; <c>ParityProjection</c> and
/// <c>CanonicalParityView</c> (later slices of 3.1-12) interpret them.
/// </summary>
public sealed record ParityFixture(
    int ParityFixtureVersion,
    string FixtureId,
    string Scenario,
    string Intent,
    ParityFixtureKind Kind,
    IReadOnlyList<string> Tags,
    JsonElement SemanticInput,
    JsonElement PlatformFactsWindows,
    JsonElement PlatformFactsLinux,
    JsonElement ExpectedCanonicalOutput,
    IReadOnlyList<AllowedDivergence> AllowedDivergences,
    IReadOnlyList<PathWhitelistEntry> PathWhitelist)
{
    public const int SupportedVersion = 1;

    public static ParityFixture Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ParityFixtureFormatException($"fixture is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ParityFixtureFormatException("fixture root must be a JSON object.");
            }

            var version = RequireInt(root, "parityFixtureVersion");
            if (version != SupportedVersion)
            {
                throw new ParityFixtureFormatException(
                    $"unsupported parityFixtureVersion {version}; this harness handles v{SupportedVersion} " +
                    "(a new format is a new directory - roadmap 25.2).");
            }

            var fixtureId = RequireString(root, "fixtureId");
            RequireFixtureIdShape(fixtureId);

            var kind = ParseKind(RequireString(root, "kind"));
            var divergences = ParseDivergences(root);
            var whitelist = ParseWhitelist(root);

            if (kind == ParityFixtureKind.Symmetric && divergences.Count > 0)
            {
                throw new ParityFixtureFormatException(
                    $"'{fixtureId}': kind=symmetric forbids allowedDivergences (roadmap 25.3).");
            }

            if (kind == ParityFixtureKind.AsymmetricObservability && divergences.Count == 0)
            {
                throw new ParityFixtureFormatException(
                    $"'{fixtureId}': kind=asymmetric-observability requires at least one allowedDivergence.");
            }

            var platformFacts = RequireObject(root, "platformFacts");

            return new ParityFixture(
                version,
                fixtureId,
                RequireString(root, "scenario"),
                RequireString(root, "intent"),
                kind,
                ParseTags(root),
                RequireObject(root, "semanticInput").Clone(),
                RequireObject(platformFacts, "windows").Clone(),
                RequireObject(platformFacts, "linux").Clone(),
                RequireObject(root, "expectedCanonicalOutput").Clone(),
                divergences,
                whitelist);
        }
    }

    private static ParityFixtureKind ParseKind(string raw) => raw switch
    {
        "symmetric" => ParityFixtureKind.Symmetric,
        "asymmetric-observability" => ParityFixtureKind.AsymmetricObservability,
        "adversarial" => ParityFixtureKind.Adversarial,
        _ => throw new ParityFixtureFormatException(
            $"kind must be symmetric | asymmetric-observability | adversarial, got \"{raw}\"."),
    };

    private static void RequireFixtureIdShape(string fixtureId)
    {
        var segments = fixtureId.Split('.');
        if (segments.Length is < 3 or > 4 ||
            segments[0] != "parity" ||
            segments.Any(segment => segment.Length == 0))
        {
            throw new ParityFixtureFormatException(
                $"fixtureId must be parity.<domain>.<name>[.<variant>], got \"{fixtureId}\".");
        }
    }

    private static IReadOnlyList<string> ParseTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            throw new ParityFixtureFormatException("fixture requires a \"tags\" array.");
        }

        return tags.EnumerateArray().Select(tag => tag.GetString() ?? string.Empty).ToArray();
    }

    private static IReadOnlyList<AllowedDivergence> ParseDivergences(JsonElement root)
    {
        if (!root.TryGetProperty("allowedDivergences", out var array))
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new ParityFixtureFormatException("allowedDivergences must be an array.");
        }

        var result = new List<AllowedDivergence>();
        foreach (var entry in array.EnumerateArray())
        {
            var path = RequireString(entry, "path");
            RequirePath(path);
            var reason = RequireString(entry, "reason");
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ParityFixtureFormatException($"allowedDivergences entry '{path}' needs a non-empty reason.");
            }

            if (!entry.TryGetProperty("windows", out var windows) || !entry.TryGetProperty("linux", out var linux))
            {
                throw new ParityFixtureFormatException(
                    $"allowedDivergences entry '{path}' must state both a windows and a linux value.");
            }

            result.Add(new AllowedDivergence(path, windows.Clone(), linux.Clone(), reason));
        }

        return result;
    }

    private static IReadOnlyList<PathWhitelistEntry> ParseWhitelist(JsonElement root)
    {
        if (!root.TryGetProperty("pathWhitelist", out var array))
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new ParityFixtureFormatException("pathWhitelist must be an array.");
        }

        var result = new List<PathWhitelistEntry>();
        foreach (var entry in array.EnumerateArray())
        {
            var path = RequireString(entry, "path");
            RequirePath(path);
            var reason = RequireString(entry, "reason");
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ParityFixtureFormatException($"pathWhitelist entry '{path}' needs a non-empty reason.");
            }

            result.Add(new PathWhitelistEntry(path, reason));
        }

        return result;
    }

    /// <summary>
    /// A whitelist / divergence path is a JSON path rooted at <c>$</c>. <c>[*]</c> is allowed only
    /// as a <c>samples</c> or <c>incidents</c> array index; a trailing <c>.*</c> (any child) is
    /// forbidden (roadmap 25.8).
    /// </summary>
    internal static void RequirePath(string path)
    {
        if (!path.StartsWith("$.", StringComparison.Ordinal))
        {
            throw new ParityFixtureFormatException($"path must start with \"$.\", got \"{path}\".");
        }

        if (path.EndsWith(".*", StringComparison.Ordinal))
        {
            throw new ParityFixtureFormatException($"path \"{path}\" ends with \".*\"; any-child wildcards are forbidden.");
        }

        foreach (var segment in path.Split('.'))
        {
            if (!segment.Contains("[*]", StringComparison.Ordinal))
            {
                continue;
            }

            if (segment is not ("samples[*]" or "incidents[*]"))
            {
                throw new ParityFixtureFormatException(
                    $"path \"{path}\": [*] is only allowed on samples or incidents, not \"{segment}\".");
            }
        }
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new ParityFixtureFormatException($"fixture requires an object member \"{name}\".");
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ParityFixtureFormatException($"fixture requires a string member \"{name}\".");
        }

        return value.GetString() ?? string.Empty;
    }

    private static int RequireInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number))
        {
            throw new ParityFixtureFormatException($"fixture requires an integer member \"{name}\".");
        }

        return number;
    }
}
