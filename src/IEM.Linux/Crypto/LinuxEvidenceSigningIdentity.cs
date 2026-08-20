using System.Security.Cryptography;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;

namespace IEM.Linux.Crypto;

/// <summary>
/// Linux persistent signing identity backed by an ECDSA P-256 (NIST P-256) PKCS#8 private key.
/// Suite: ECDSA P-256 + SHA-256 (RFC 3279 DER sequence signature).
/// </summary>
public sealed class LinuxEvidenceSigningIdentity : IEvidenceSigningIdentity
{
    private readonly ECDsa _ecdsa;

    public string KeyId { get; }
    public SignatureSuite Suite { get; } = SignatureSuite.EcdsaP256Sha256;
    public byte[] PublicKey { get; }
    public KeyProtectionClaim Protection { get; }
    public LinuxSigningIdentityScope Scope { get; }

    public LinuxEvidenceSigningIdentity(
        ECDsa ecdsa,
        KeyProtectionClaim protection,
        LinuxSigningIdentityScope scope)
    {
        _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
        Protection = protection ?? throw new ArgumentNullException(nameof(protection));
        Scope = scope;

        PublicKey = _ecdsa.ExportSubjectPublicKeyInfo();
        KeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(PublicKey));
    }

    public Task<byte[]> SignHashAsync(byte[] sha256Hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sha256Hash);

        var signature = _ecdsa.SignHash(sha256Hash, DSASignatureFormat.Rfc3279DerSequence);
        return Task.FromResult(signature);
    }

    public void Dispose()
    {
        _ecdsa.Dispose();
    }
}
