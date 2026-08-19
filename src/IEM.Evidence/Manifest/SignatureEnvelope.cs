using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using IEM.Evidence.Canonicalization;
using IEM.Evidence.Crypto;

namespace IEM.Evidence.Manifest;

/// <summary>
/// Atomic signature envelope binding the manifest SHA-256 hash, key identity, signature suite,
/// public key, key protection claim, and digital signature over the canonical manifest.
/// </summary>
public sealed record SignatureEnvelope(
    int EnvelopeVersion,
    string ManifestSha256,
    string KeyId,
    SignatureSuite SignatureSuite,
    string PublicKeyBase64,
    KeyProtectionClaim KeyProtection,
    string SignatureBase64,
    DateTimeOffset SignedUtc)

{
    public const int CurrentEnvelopeVersion = 1;
    public const string FileName = "manifest.sig";
    public const string TempFileName = "manifest.sig.tmp";

    /// <summary>
    /// Computes the exact canonical RFC 8785 UTF-8 bytes for this signature envelope.
    /// </summary>
    public byte[] ToCanonicalBytes()
    {
        return JsonCanonicalizer.Canonicalize(this, JsonOptions);
    }

    /// <summary>
    /// Computes the SHA-256 message imprint over the canonical envelope bytes,
    /// ready for RFC 3161 trusted timestamping (3.0-4).
    /// </summary>
    public byte[] ComputeTimestampMessageImprint()
    {
        return SHA256.HashData(ToCanonicalBytes());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
