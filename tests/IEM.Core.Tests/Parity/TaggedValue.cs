using System.Text.Json;

namespace IEM.Core.Tests.Parity;

/// <summary>
/// The five-plus-one states a parity fixture must keep apart (roadmap 25.4, invariant 256):
/// a real value, "checked but not determined" (<c>null</c>), tri-state <c>unknown</c>, a
/// capability that is <c>unavailable</c>, a probe that was <c>skipped</c>, and a field that
/// is <c>absent</c> from this cycle. A bare JSON <c>null</c> is a format error, never one of
/// these.
/// </summary>
public enum TaggedValueTag
{
    Value,
    Null,
    Unknown,
    Unavailable,
    Skipped,
    Absent,
}

/// <summary>
/// A single tagged value: <c>{ "t": "v", "v": ... }</c> or <c>{ "t": "unknown" }</c> etc.
/// </summary>
public sealed record TaggedValue(TaggedValueTag Tag, JsonElement? Value = null, string? Code = null)
{
    public bool IsValueEqualTo(bool expected) =>
        Tag == TaggedValueTag.Value &&
        Value is { ValueKind: JsonValueKind.True or JsonValueKind.False } element &&
        element.GetBoolean() == expected;

    public bool IsValueEqualTo(string expected) =>
        Tag == TaggedValueTag.Value &&
        Value is { ValueKind: JsonValueKind.String } element &&
        string.Equals(element.GetString(), expected, StringComparison.Ordinal);

    public string Describe() => Tag switch
    {
        TaggedValueTag.Value => Value?.ValueKind switch
        {
            JsonValueKind.String => $"\"{Value.Value.GetString()}\"",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => Value.Value.GetRawText(),
            _ => Value?.GetRawText() ?? "v",
        },
        TaggedValueTag.Unavailable => $"unavailable({Code ?? "?"})",
        _ => Tag.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Parses a tagged value. A property that is missing entirely maps to <see cref="TaggedValueTag.Absent"/>;
    /// a JSON <c>null</c>, an object without <c>t</c>, or an unknown tag is a <see cref="ParityFixtureFormatException"/>.
    /// </summary>
    public static TaggedValue Parse(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element))
        {
            return new TaggedValue(TaggedValueTag.Absent);
        }

        return ParseElement(element, propertyName);
    }

    public static TaggedValue ParseElement(JsonElement element, string context)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            throw new ParityFixtureFormatException(
                $"'{context}': a bare JSON null is forbidden - use {{ \"t\": \"null\" }} (roadmap 25.4).");
        }

        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("t", out var tag) ||
            tag.ValueKind != JsonValueKind.String)
        {
            throw new ParityFixtureFormatException(
                $"'{context}': a tagged value must be an object carrying a string \"t\".");
        }

        return tag.GetString() switch
        {
            "v" => element.TryGetProperty("v", out var value)
                ? new TaggedValue(TaggedValueTag.Value, value.Clone())
                : throw new ParityFixtureFormatException($"'{context}': tag \"v\" requires a \"v\" member."),
            "null" => new TaggedValue(TaggedValueTag.Null),
            "unknown" => new TaggedValue(TaggedValueTag.Unknown),
            "unavailable" => new TaggedValue(
                TaggedValueTag.Unavailable,
                Code: element.TryGetProperty("code", out var code) ? code.GetString() : null),
            "skipped" => new TaggedValue(TaggedValueTag.Skipped),
            "absent" => new TaggedValue(TaggedValueTag.Absent),
            var other => throw new ParityFixtureFormatException(
                $"'{context}': unknown tagged-value tag \"{other}\"."),
        };
    }
}
