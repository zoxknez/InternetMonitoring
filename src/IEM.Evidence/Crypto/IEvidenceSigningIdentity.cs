using IEM.Evidence.Manifest;

namespace IEM.Evidence.Crypto;

/// <summary>
/// Atomic cryptographic signing identity.
/// <para>
/// Binds the KeyId, SignatureSuite, PublicKey, KeyProtection, and digital signing operation
/// into one cohesive, non-separable identity.
/// </para>
/// </summary>
public interface IEvidenceSigningIdentity : IDisposable
{
    /// <summary>
    /// Deterministic identifier derived as "sha256:" + Hex(SHA256(SubjectPublicKeyInfoDer)).
    /// </summary>
    string KeyId { get; }

    /// <summary>Cryptographic suite used by this identity.</summary>
    SignatureSuite Suite { get; }

    /// <summary>Public key bytes in SubjectPublicKeyInfo DER format.</summary>
    byte[] PublicKey { get; }

    /// <summary>Protection provenance claim for this key.</summary>
    KeyProtectionClaim Protection { get; }

    /// <summary>
    /// Signs a pre-computed SHA-256 hash using ECDSA P-256, outputting an RFC 3279 DER-encoded sequence.
    /// </summary>
    /// <param name="sha256Hash">The 32-byte SHA-256 hash of the manifest canonical bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<byte[]> SignHashAsync(byte[] sha256Hash, CancellationToken ct = default);
}

/// <summary>
/// Supplies or provisions the persistent installation signing identity.
/// </summary>
public interface IEvidenceKeyProvider
{
    /// <summary>
    /// Gets the existing persistent installation identity, or provisions a new one on first run.
    /// </summary>
    Task<IEvidenceSigningIdentity> GetOrCreateIdentityAsync(CancellationToken ct = default);
}

/// <summary>
/// Thrown when an existing signing identity cannot be opened or accessed,
/// preventing silent key rotation per Invariant 22.
/// </summary>
public sealed class SigningIdentityUnavailableException : Exception
{
    public SigningIdentityUnavailableException(string message) : base(message)
    {
    }

    public SigningIdentityUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
