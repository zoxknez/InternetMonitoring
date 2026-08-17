using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IEM.Storage.Evidence;

/// <summary>
/// Shared rules of the on-disk chain format.
/// <para>
/// Each line is a complete JSON object ending in its own hash, and that hash covers
/// everything before it including the previous line's hash. Change any earlier line and
/// every hash after it stops matching, so tampering cannot be localised or hidden.
/// </para>
/// <para>
/// The exact line layout is fixed rather than left to a serialiser. Verification recovers
/// the hashed bytes by stripping a known-length suffix, which means a verifier never has
/// to parse or re-serialise the payload - so a chain written today still verifies against
/// a build that has since added fields or changed types.
/// </para>
/// </summary>
public static class HashChain
{
    /// <summary>The hash the first entry chains from.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    internal const string HashFieldPrefix = ",\"h\":\"";

    /// <summary>Length of the <c>,"h":"…"}</c> suffix: 6 + 64 hex + 2.</summary>
    internal const int HashSuffixLength = 6 + 64 + 2;

    public static string ComputeHash(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));

    /// <summary>
    /// Recovers the bytes a line's hash was computed over, and the hash it claims.
    /// Returns false for a line that is not shaped like a chain entry, which is how a
    /// half-written final line from a crash gets recognised.
    /// </summary>
    internal static bool TrySplit(string line, out string body, out string claimedHash)
    {
        body = string.Empty;
        claimedHash = string.Empty;

        if (line.Length < HashSuffixLength + 2 || !line.EndsWith("\"}", StringComparison.Ordinal))
        {
            return false;
        }

        var suffixStart = line.Length - HashSuffixLength;
        if (!line.AsSpan(suffixStart, HashFieldPrefix.Length).SequenceEqual(HashFieldPrefix))
        {
            return false;
        }

        var hash = line.AsSpan(suffixStart + HashFieldPrefix.Length, 64);
        foreach (var c in hash)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        body = string.Concat(line.AsSpan(0, suffixStart), "}");
        claimedHash = new string(hash);
        return true;
    }

    internal static string BuildBody(EvidenceKind kind, long entryNumber, string previousHash, IEvidencePayload payload)
    {
        var buffer = new MemoryStream(512);

        // Indentation off and a fixed encoder: the bytes must be reproducible, and
        // "pretty" output would make the hash depend on formatting settings.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("k", kind.ToString());
            writer.WriteNumber("n", entryNumber);
            writer.WriteString("prev", previousHash);
            writer.WriteStartObject("p");
            payload.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
