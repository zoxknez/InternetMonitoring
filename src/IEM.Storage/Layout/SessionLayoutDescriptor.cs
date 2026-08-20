using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IEM.Storage.Layout;

/// <summary>
/// Versioned session storage layout descriptor (layout.json).
/// Invariants:
/// 67. SESSION_STORAGE_LAYOUT_IS_VERSIONED_AND_EXPLICIT
/// 73. LEGACY_SESSION_LAYOUT_IS_NEVER_MIGRATED_IN_PLACE
/// </summary>
public sealed record SessionLayoutDescriptor
{
    public const string FileName = "layout.json";
    public const int CurrentLayoutVersion = 2;

    [JsonPropertyName("layoutVersion")]
    public int LayoutVersion { get; init; } = CurrentLayoutVersion;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("rawRelativePath")]
    public string RawRelativePath { get; init; } = "Raw";

    [JsonPropertyName("derivedRelativePath")]
    public string DerivedRelativePath { get; init; } = "Derived";

    [JsonPropertyName("evidenceRelativePath")]
    public string EvidenceRelativePath { get; init; } = "Evidence";

    [JsonPropertyName("exportsRelativePath")]
    public string ExportsRelativePath { get; init; } = "Exports";

    [JsonPropertyName("storagePolicyVersion")]
    public int StoragePolicyVersion { get; init; } = 1;

    [JsonPropertyName("storagePolicyHash")]
    public string StoragePolicyHash { get; init; } = string.Empty;

    public static SessionLayoutDescriptor CreateStandard(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var descriptor = $"v={CurrentLayoutVersion};raw=Raw;derived=Derived;evidence=Evidence;exports=Exports;spv=1";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));

        return new SessionLayoutDescriptor
        {
            LayoutVersion = CurrentLayoutVersion,
            SessionId = sessionId,
            RawRelativePath = "Raw",
            DerivedRelativePath = "Derived",
            EvidenceRelativePath = "Evidence",
            ExportsRelativePath = "Exports",
            StoragePolicyVersion = 1,
            StoragePolicyHash = hash,
        };
    }

    public byte[] ToCanonicalBytes()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        return JsonSerializer.SerializeToUtf8Bytes(this, options);
    }

    public static SessionLayoutDescriptor? FromBytes(byte[] utf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(utf8Bytes);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        return JsonSerializer.Deserialize<SessionLayoutDescriptor>(utf8Bytes, options);
    }

    public static SessionLayoutDescriptor? FromCanonicalBytes(byte[] utf8Bytes) => FromBytes(utf8Bytes);
}
