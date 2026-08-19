using System.Security.Cryptography;
using IEM.Evidence.Manifest;

namespace IEM.Evidence.Crypto;

/// <summary>
/// Signs a canonical manifest and writes the atomic <see cref="SignatureEnvelope"/> (<c>manifest.sig</c>).
/// </summary>
public static class ManifestSigner
{
    /// <summary>
    /// Signs <c>manifest.json</c> in the specified session directory and atomically writes <c>manifest.sig</c>.
    /// </summary>
    public static async Task<SignatureEnvelope> SignManifestAtomicallyAsync(
        string sessionDirectory,
        IEvidenceSigningIdentity identity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        ArgumentNullException.ThrowIfNull(identity);

        var manifestPath = Path.Combine(sessionDirectory, EvidenceManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Manifest evidencije ne postoji: {manifestPath}");
        }

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
        var hashBytes = SHA256.HashData(manifestBytes);
        var manifestSha256Hex = Convert.ToHexStringLower(hashBytes);

        var signatureBytes = await identity.SignHashAsync(hashBytes, ct).ConfigureAwait(false);

        var envelope = new SignatureEnvelope(
            EnvelopeVersion: SignatureEnvelope.CurrentEnvelopeVersion,
            ManifestSha256: manifestSha256Hex,
            KeyId: identity.KeyId,
            SignatureSuite: identity.Suite,
            PublicKeyBase64: Convert.ToBase64String(identity.PublicKey),
            KeyProtection: identity.Protection,
            SignatureBase64: Convert.ToBase64String(signatureBytes),
            SignedUtc: DateTimeOffset.UtcNow);

        // Self-verification check before publishing
        var verification = SignatureVerifier.Verify(manifestBytes, envelope);
        if (!verification.IsValid)
        {
            throw new InvalidOperationException($"Kreirana omotnica potpisa nije prošla samoproveru: {verification.Message}");
        }

        var envelopeCanonicalBytes = envelope.ToCanonicalBytes();

        var targetSigPath = Path.Combine(sessionDirectory, SignatureEnvelope.FileName);
        var tempSigPath = Path.Combine(sessionDirectory, SignatureEnvelope.TempFileName);

        await File.WriteAllBytesAsync(tempSigPath, envelopeCanonicalBytes, ct).ConfigureAwait(false);

        if (File.Exists(targetSigPath))
        {
            File.Delete(targetSigPath);
        }

        File.Move(tempSigPath, targetSigPath);

        return envelope;
    }
}
