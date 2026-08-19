namespace IEM.Evidence.Crypto;

/// <summary>
/// The protection level under which the private signing key is stored.
/// </summary>
public enum KeyProtectionLevel
{
    /// <summary>Stored in and protected by a hardware Trusted Platform Module (TPM 2.0).</summary>
    TpmBacked,

    /// <summary>Stored on an external cryptographic hardware token / smart card.</summary>
    HardwareToken,

    /// <summary>Stored using OS-protected software cryptographic storage (e.g., Software KSP).</summary>
    SoftwareProtected,
}

/// <summary>
/// How the key protection claim was established.
/// </summary>
public enum KeyProtectionEvidence
{
    /// <summary>Reported by the operating system cryptographic provider.</summary>
    ProviderReported,

    /// <summary>Cryptographically proven by a hardware attestation statement.</summary>
    HardwareAttested,
}

/// <summary>
/// Provenance claim regarding private key storage and isolation.
/// </summary>
public sealed record KeyProtectionClaim(
    KeyProtectionLevel Protection,
    KeyProtectionEvidence Evidence,
    string Provider,
    string? Details = null);
