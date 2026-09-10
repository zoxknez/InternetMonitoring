using System.Security.Cryptography;
using System.Text.Json.Nodes;
using IEM.Evidence.Canonicalization;

namespace IEM.Core.Tests.Parity;

/// <summary>
/// Produces the invariant-260 hash: explicitly allowed differences become the same stable
/// marker on both sides, while temporary diagnostic whitelist paths are removed.
/// </summary>
public static class ParityHashNormalizer
{
    public static string Sha256(CanonicalParityView view, ParityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(fixture);

        var root = JsonNode.Parse(view.CanonicalJson)
            ?? throw new InvalidOperationException("Canonical parity view serialized to null.");

        for (var index = 0; index < fixture.AllowedDivergences.Count; index++)
        {
            Replace(root, fixture.AllowedDivergences[index].Path, new JsonObject
            {
                ["t"] = "diverged",
                ["id"] = $"allowedDivergences[{index}]",
            });
        }

        foreach (var whitelist in fixture.PathWhitelist)
        {
            Remove(root, whitelist.Path);
        }

        var canonical = JsonCanonicalizer.Canonicalize(root.ToJsonString());
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static void Replace(JsonNode root, string path, JsonNode replacement) =>
        VisitParents(root, Parse(path), 0, (parent, property) => parent[property] = replacement.DeepClone());

    private static void Remove(JsonNode root, string path) =>
        VisitParents(root, Parse(path), 0, (parent, property) => parent.Remove(property));

    private static void VisitParents(
        JsonNode? current,
        IReadOnlyList<PathPart> parts,
        int offset,
        Action<JsonObject, string> action)
    {
        if (current is null || offset >= parts.Count)
        {
            return;
        }

        var part = parts[offset];
        if (offset == parts.Count - 1)
        {
            if (current is JsonObject parent && part.Index is null && parent.ContainsKey(part.Property))
            {
                action(parent, part.Property);
            }
            return;
        }

        if (current is not JsonObject obj || obj[part.Property] is not { } child)
        {
            return;
        }

        if (part.Index is null)
        {
            VisitParents(child, parts, offset + 1, action);
            return;
        }

        if (child is not JsonArray array)
        {
            return;
        }

        if (part.Index == "*")
        {
            foreach (var item in array)
            {
                VisitParents(item, parts, offset + 1, action);
            }
            return;
        }

        if (int.TryParse(part.Index, out var index) && index >= 0 && index < array.Count)
        {
            VisitParents(array[index], parts, offset + 1, action);
        }
    }

    private static IReadOnlyList<PathPart> Parse(string path)
    {
        var result = new List<PathPart>();
        foreach (var raw in path[2..].Split('.'))
        {
            var bracket = raw.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0)
            {
                result.Add(new PathPart(raw, null));
                continue;
            }

            result.Add(new PathPart(raw[..bracket], raw[(bracket + 1)..^1]));
        }
        return result;
    }

    private sealed record PathPart(string Property, string? Index);
}
