using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using IEM.Evidence.Canonicalization;

namespace IEM.Evidence.Manifest;

/// <summary>
/// Immutable, canonical manifest describing the exact contents, raw chain, derived ledger,
/// and forensic file inventory of a completed IEM session.
/// </summary>
public sealed record EvidenceManifest(
    int ManifestSchemaVersion,
    string Canonicalization,
    DateTimeOffset CreatedUtc,
    ManifestSessionInfo Session,
    ManifestEvidenceSummary Evidence,
    IReadOnlyList<ManifestFileEntry> Files,
    ManifestAcquisitionContext AcquisitionContext)
{
    public const int CurrentSchemaVersion = 1;
    public const string CanonicalizationStandard = "RFC8785-JCS";
    public const string FileName = "manifest.json";
    public const string TempFileName = "manifest.json.tmp";

    /// <summary>
    /// Computes the exact canonical UTF-8 bytes for this manifest per RFC 8785.
    /// </summary>
    public byte[] ToCanonicalBytes()
    {
        return JsonCanonicalizer.Canonicalize(this, JsonOptions);
    }

    /// <summary>
    /// Computes the SHA-256 hash of the canonical manifest bytes.
    /// </summary>
    public string ComputeManifestSha256()
    {
        var bytes = ToCanonicalBytes();
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public sealed record ManifestSessionInfo(
    string SessionId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    int EvidenceSchemaVersion,
    string ApplicationVersion);

public sealed record ManifestEvidenceSummary(
    ManifestRawChainRef RawChain,
    ManifestFileRef? DerivedLedger,
    ManifestFileRef? InterpretationCatalog,
    string? LegalContextHash);

public sealed record ManifestRawChainRef(
    string RelativePath,
    string FinalChainHash,
    long RecordCount);

public sealed record ManifestFileRef(
    string RelativePath,
    string Sha256);

public sealed record ManifestFileEntry(
    string RelativePath,
    long Size,
    string Sha256);

public sealed record ManifestAcquisitionContext(
    string Platform,
    IReadOnlyDictionary<string, string> ProviderProvenance);
