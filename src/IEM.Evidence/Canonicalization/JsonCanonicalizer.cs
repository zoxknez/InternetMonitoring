using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace IEM.Evidence.Canonicalization;

/// <summary>
/// RFC 8785 (JSON Canonicalization Scheme - JCS) serializer.
/// <para>
/// Produces a deterministic, canonical UTF-8 byte representation of any JSON data
/// for cryptographic hashing, manifests, and digital signatures.
/// </para>
/// <para>
/// Conforms to RFC 8785:
/// 1. UTF-8 without BOM.
/// 2. Deterministic object property ordering by UTF-16 code unit ordinal sort.
/// 3. Minimal whitespace (no spaces or newlines).
/// 4. Canonical string escaping (only quotes, backslashes, and control characters U+0000..U+001F).
/// 5. Canonical number formatting.
/// </para>
/// </summary>
public static class JsonCanonicalizer
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Canonicalizes a JSON UTF-8 byte span or string into canonical RFC 8785 UTF-8 bytes.
    /// </summary>
    public static byte[] Canonicalize(byte[] utf8Json)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);
        using var doc = JsonDocument.Parse(utf8Json);
        return Canonicalize(doc.RootElement);
    }

    /// <summary>
    /// Canonicalizes a JSON string into canonical RFC 8785 UTF-8 bytes.
    /// </summary>
    public static byte[] Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        return Canonicalize(doc.RootElement);
    }

    /// <summary>
    /// Serializes an object to JSON and canonicalizes it per RFC 8785.
    /// </summary>
    public static byte[] Canonicalize<T>(T value, JsonSerializerOptions? options = null)
    {
        var rawUtf8 = JsonSerializer.SerializeToUtf8Bytes(value, options);
        using var doc = JsonDocument.Parse(rawUtf8);
        return Canonicalize(doc.RootElement);
    }

    /// <summary>
    /// Canonicalizes a parsed <see cref="JsonElement"/> into RFC 8785 UTF-8 bytes.
    /// </summary>
    public static byte[] Canonicalize(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteElement(element, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Canonicalizes a parsed <see cref="JsonElement"/> into an RFC 8785 UTF-8 string.
    /// </summary>
    public static string CanonicalizeToString(JsonElement element) =>
        Utf8NoBom.GetString(Canonicalize(element));

    private static void WriteElement(JsonElement element, IBufferWriter<byte> writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, writer);
                break;
            case JsonValueKind.Array:
                WriteArray(element, writer);
                break;
            case JsonValueKind.String:
                WriteString(element.GetString() ?? string.Empty, writer);
                break;
            case JsonValueKind.Number:
                WriteNumber(element, writer);
                break;
            case JsonValueKind.True:
                writer.Write(Utf8NoBom.GetBytes("true"));
                break;
            case JsonValueKind.False:
                writer.Write(Utf8NoBom.GetBytes("false"));
                break;
            case JsonValueKind.Null:
                writer.Write(Utf8NoBom.GetBytes("null"));
                break;
            case JsonValueKind.Undefined:
            default:
                throw new InvalidOperationException($"Nepodržan JSON tip za kanonizaciju: {element.ValueKind}");
        }
    }

    private static void WriteObject(JsonElement element, IBufferWriter<byte> writer)
    {
        writer.Write(Utf8NoBom.GetBytes("{"));

        // RFC 8785: Properties must be sorted lexicographically by UTF-16 code units (Ordinal)
        var properties = element.EnumerateObject().ToList();
        properties.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        var first = true;
        foreach (var property in properties)
        {
            if (!first)
            {
                writer.Write(Utf8NoBom.GetBytes(","));
            }
            first = false;

            WriteString(property.Name, writer);
            writer.Write(Utf8NoBom.GetBytes(":"));
            WriteElement(property.Value, writer);
        }

        writer.Write(Utf8NoBom.GetBytes("}"));
    }

    private static void WriteArray(JsonElement element, IBufferWriter<byte> writer)
    {
        writer.Write(Utf8NoBom.GetBytes("["));

        var first = true;
        foreach (var item in element.EnumerateArray())
        {
            if (!first)
            {
                writer.Write(Utf8NoBom.GetBytes(","));
            }
            first = false;

            WriteElement(item, writer);
        }

        writer.Write(Utf8NoBom.GetBytes("]"));
    }

    private static void WriteString(string value, IBufferWriter<byte> writer)
    {
        writer.Write(Utf8NoBom.GetBytes("\""));

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    writer.Write(Utf8NoBom.GetBytes("\\\""));
                    break;
                case '\\':
                    writer.Write(Utf8NoBom.GetBytes("\\\\"));
                    break;
                case '\b':
                    writer.Write(Utf8NoBom.GetBytes("\\b"));
                    break;
                case '\f':
                    writer.Write(Utf8NoBom.GetBytes("\\f"));
                    break;
                case '\n':
                    writer.Write(Utf8NoBom.GetBytes("\\n"));
                    break;
                case '\r':
                    writer.Write(Utf8NoBom.GetBytes("\\r"));
                    break;
                case '\t':
                    writer.Write(Utf8NoBom.GetBytes("\\t"));
                    break;
                default:
                    if (ch < 0x20)
                    {
                        var hex = $"\\u{(int)ch:x4}";
                        writer.Write(Utf8NoBom.GetBytes(hex));
                    }
                    else
                    {
                        var charBytes = Utf8NoBom.GetBytes(ch.ToString());
                        writer.Write(charBytes);
                    }
                    break;
            }
        }

        writer.Write(Utf8NoBom.GetBytes("\""));
    }

    private static void WriteNumber(JsonElement element, IBufferWriter<byte> writer)
    {
        // RFC 8785 section 3.2.2.3: Number formatting must follow ECMAScript 5.1 specification
        if (element.TryGetInt64(out var longVal))
        {
            writer.Write(Utf8NoBom.GetBytes(longVal.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        if (element.TryGetUInt64(out var ulongVal))
        {
            writer.Write(Utf8NoBom.GetBytes(ulongVal.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        var doubleVal = element.GetDouble();
        if (double.IsNaN(doubleVal) || double.IsInfinity(doubleVal))
        {
            throw new InvalidOperationException("NaN i Infinity nisu dozvoljeni u JSON brojevima.");
        }

        // Canonical floating point output format per ECMAScript / RFC 8785
        var formatted = FormatCanonicalDouble(doubleVal);
        writer.Write(Utf8NoBom.GetBytes(formatted));
    }

    private static string FormatCanonicalDouble(double value)
    {
        if (value == 0.0)
        {
            return "0";
        }

        // Standard ECMAScript 5.1 number representation
        var str = value.ToString("R", CultureInfo.InvariantCulture);
        if (str.Contains('E', StringComparison.OrdinalIgnoreCase))
        {
            // Normalize exponent representation e.g. 1e+05 -> 1e5, 1e-05 -> 1e-5
            var parts = str.Split(['E', 'e']);
            var mantissa = parts[0];
            var exp = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return $"{mantissa}e{(exp >= 0 ? "+" : "")}{exp}";
        }

        return str;
    }
}
