namespace IEM.Evidence.Manifest;

/// <summary>
/// Cryptographic suite specification defining signature algorithm, hash, and binary encodings.
/// </summary>
public sealed record SignatureSuite(
    string Algorithm,
    string Hash,
    string PublicKeyFormat,
    string SignatureFormat)
{
    /// <summary>Mandatory standard baseline suite for IEM 3.0.</summary>
    public static readonly SignatureSuite EcdsaP256Sha256 = new(
        Algorithm: "ECDSA_P256",
        Hash: "SHA256",
        PublicKeyFormat: "SubjectPublicKeyInfoDer",
        SignatureFormat: "Rfc3279DerSequence");
}
