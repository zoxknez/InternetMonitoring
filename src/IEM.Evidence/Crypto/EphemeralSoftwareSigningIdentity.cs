using System.Security.Cryptography;
using IEM.Evidence.Manifest;

namespace IEM.Evidence.Crypto;

/// <summary>
/// Cross-platform software-based signing identity for testing, ephemeral sessions, and fallback.
/// </summary>
public sealed class EphemeralSoftwareSigningIdentity : IEvidenceSigningIdentity
{
    private readonly ECDsa _ecdsa;
    private readonly bool _ownsKey;

    public string KeyId { get; }
    public SignatureSuite Suite { get; } = SignatureSuite.EcdsaP256Sha256;
    public byte[] PublicKey { get; }
    public KeyProtectionClaim Protection { get; }

    public EphemeralSoftwareSigningIdentity(
        ECDsa? ecdsa = null,
        KeyProtectionLevel protectionLevel = KeyProtectionLevel.SoftwareProtected,
        string provider = "SoftwareKey",
        string? details = null)
    {
        if (ecdsa is null)
        {
            _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _ownsKey = true;
        }
        else
        {
            _ecdsa = ecdsa;
            _ownsKey = false;
        }

        PublicKey = _ecdsa.ExportSubjectPublicKeyInfo();
        KeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(PublicKey));
        Protection = new KeyProtectionClaim(protectionLevel, KeyProtectionEvidence.ProviderReported, provider, details);
    }

    public Task<byte[]> SignHashAsync(byte[] sha256Hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sha256Hash);
        var signature = _ecdsa.SignHash(sha256Hash, DSASignatureFormat.Rfc3279DerSequence);
        return Task.FromResult(signature);
    }

    public void Dispose()
    {
        if (_ownsKey)
        {
            _ecdsa.Dispose();
        }
    }
}
