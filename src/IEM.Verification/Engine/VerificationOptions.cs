using System.Security.Cryptography.X509Certificates;

namespace IEM.Verification.Engine;

/// <summary>
/// Execution options for independent package verification.
/// </summary>
public sealed class VerificationOptions
{
    /// <summary>
    /// If true, guarantees 0 network requests (0 DNS, 0 HTTP, 0 OCSP/AIA network calls).
    /// Invariant 31: OFFLINE_VERIFICATION_NEVER_SILENTLY_USES_NETWORK.
    /// </summary>
    public bool Offline { get; set; } = false;

    /// <summary>
    /// Expected KeyId (e.g. "sha256:abcd..."). If set, signature verification will check
    /// that the package was signed by this specific installation key.
    /// </summary>
    public string? ExpectedKeyId { get; set; }

    /// <summary>
    /// Path to trusted SubjectPublicKeyInfo DER/PEM file for key pinning.
    /// </summary>
    public string? TrustedKeyPath { get; set; }

    /// <summary>
    /// Optional extra certificate collection to use as trusted roots or intermediates.
    /// </summary>
    public X509Certificate2Collection? ExtraCertificates { get; set; }
}
