using System.Security.Cryptography;
using IEM.Evidence.Manifest;

namespace IEM.Evidence.Crypto;

/// <summary>
/// Status of cryptographic manifest signature verification.
/// </summary>
public enum SignatureVerificationStatus
{
    /// <summary>Signature mathematically verifies over the manifest and public key.</summary>
    Valid,

    /// <summary>Manifest SHA-256 hash does not match the ManifestSha256 recorded in the envelope.</summary>
    HashMismatch,

    /// <summary>KeyId does not match the SHA-256 hash of the public key.</summary>
    KeyIdMismatch,

    /// <summary>Public key cannot be imported or parsed.</summary>
    InvalidKeyFormat,

    /// <summary>Cryptographic ECDSA signature verification failed.</summary>
    InvalidSignature,
}

/// <summary>
/// Result of digital signature verification.
/// </summary>
public sealed record SignatureVerificationResult(
    SignatureVerificationStatus Status,
    string? Message = null)
{
    public bool IsValid => Status == SignatureVerificationStatus.Valid;

    public static SignatureVerificationResult Success() =>
        new(SignatureVerificationStatus.Valid);

    public static SignatureVerificationResult Failed(SignatureVerificationStatus status, string message) =>
        new(status, message);
}

/// <summary>
/// Verifies digital signatures over canonical evidence manifests per IEM 3.0 cryptographic contracts.
/// </summary>
public static class SignatureVerifier
{
    /// <summary>
    /// Verifies the digital signature envelope against the exact canonical manifest bytes.
    /// </summary>
    public static SignatureVerificationResult Verify(byte[] manifestBytes, SignatureEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(envelope);

        var computedManifestSha = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
        if (!string.Equals(computedManifestSha, envelope.ManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            return SignatureVerificationResult.Failed(
                SignatureVerificationStatus.HashMismatch,
                $"Otisak manifesta ({computedManifestSha}) ne odgovara otisku u omotnici ({envelope.ManifestSha256}).");
        }

        byte[] pubKeyBytes;
        try
        {
            pubKeyBytes = Convert.FromBase64String(envelope.PublicKeyBase64);
        }
        catch (Exception ex)
        {
            return SignatureVerificationResult.Failed(
                SignatureVerificationStatus.InvalidKeyFormat,
                $"Javni ključ nije ispravan Base64 string: {ex.Message}");
        }

        var expectedKeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(pubKeyBytes));
        if (!string.Equals(envelope.KeyId, expectedKeyId, StringComparison.OrdinalIgnoreCase))
        {
            return SignatureVerificationResult.Failed(
                SignatureVerificationStatus.KeyIdMismatch,
                $"KeyId ({envelope.KeyId}) ne odgovara izvedenom otisku javnog ključa ({expectedKeyId}).");
        }

        byte[] sigBytes;
        try
        {
            sigBytes = Convert.FromBase64String(envelope.SignatureBase64);
        }
        catch (Exception ex)
        {
            return SignatureVerificationResult.Failed(
                SignatureVerificationStatus.InvalidSignature,
                $"Potpis nije ispravan Base64 string: {ex.Message}");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);

            var hashBytes = SHA256.HashData(manifestBytes);
            var verified = ecdsa.VerifyHash(hashBytes, sigBytes, DSASignatureFormat.Rfc3279DerSequence);

            if (!verified)
            {
                return SignatureVerificationResult.Failed(
                    SignatureVerificationStatus.InvalidSignature,
                    "Kriptografska provera potpisa nije uspela.");
            }

            return SignatureVerificationResult.Success();
        }
        catch (Exception ex)
        {
            return SignatureVerificationResult.Failed(
                SignatureVerificationStatus.InvalidSignature,
                $"Greška tokom provere potpisa: {ex.Message}");
        }
    }
}
