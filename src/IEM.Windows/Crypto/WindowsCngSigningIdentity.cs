using System.Runtime.Versioning;
using System.Security.Cryptography;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;

namespace IEM.Windows.Crypto;

/// <summary>
/// Windows CNG (Cryptography Next Generation) signing identity backed by TPM or Software KSP.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCngSigningIdentity : IEvidenceSigningIdentity
{
    private readonly CngKey _cngKey;

    public string KeyId { get; }
    public SignatureSuite Suite { get; } = SignatureSuite.EcdsaP256Sha256;
    public byte[] PublicKey { get; }
    public KeyProtectionClaim Protection { get; }

    public WindowsCngSigningIdentity(CngKey cngKey, KeyProtectionClaim protection)
    {
        _cngKey = cngKey ?? throw new ArgumentNullException(nameof(cngKey));
        Protection = protection ?? throw new ArgumentNullException(nameof(protection));

        using var ecdsa = new ECDsaCng(_cngKey);
        PublicKey = ecdsa.ExportSubjectPublicKeyInfo();
        KeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(PublicKey));
    }

    public Task<byte[]> SignHashAsync(byte[] sha256Hash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sha256Hash);

        using var ecdsa = new ECDsaCng(_cngKey);
        var signature = ecdsa.SignHash(sha256Hash, DSASignatureFormat.Rfc3279DerSequence);
        return Task.FromResult(signature);
    }

    public void Dispose()
    {
        _cngKey.Dispose();
    }
}
