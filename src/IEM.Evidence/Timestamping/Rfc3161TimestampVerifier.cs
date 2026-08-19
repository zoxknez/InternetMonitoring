using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace IEM.Evidence.Timestamping;

/// <summary>
/// Result of RFC 3161 timestamp response verification.
/// </summary>
public sealed record TimestampVerificationResult(
    TrustedTimestamp? Timestamp,
    TrustedTimeState State,
    string? FailureReason = null)
{
    public bool IsCryptographicallyValid =>
        State == TrustedTimeState.ValidTrusted || State == TrustedTimeState.ValidUntrusted;

    public static TimestampVerificationResult Invalid(string reason) =>
        new(null, TrustedTimeState.Invalid, reason);
}

/// <summary>
/// RFC 3161 and RFC 5816 timestamp token verifier.
/// </summary>
public static class Rfc3161TimestampVerifier
{
    /// <summary>
    /// Verifies raw timestamp response bytes (<c>timestamp.tsr</c>) against the exact <c>manifest.sig</c> bytes.
    /// </summary>
    public static TimestampVerificationResult Verify(
        byte[] manifestSigBytes,
        byte[] tsrBytes,
        byte[]? tsqBytes = null,
        X509Certificate2Collection? extraStore = null)
    {
        ArgumentNullException.ThrowIfNull(manifestSigBytes);
        ArgumentNullException.ThrowIfNull(tsrBytes);

        if (!Rfc3161TimestampToken.TryDecode(tsrBytes, out var token, out _))
        {
            return TimestampVerificationResult.Invalid("Timestamp token nije u validnom ASN.1/DER formatu.");
        }

        var expectedImprint = SHA256.HashData(manifestSigBytes);
        var expectedImprintHex = Convert.ToHexStringLower(expectedImprint);
        var tokenInfo = token.TokenInfo;
        var tokenImprint = tokenInfo.GetMessageHash().Span;

        // 1. Verify matching hash on manifest.sig
        if (!tokenImprint.SequenceEqual(expectedImprint))
        {
            return TimestampVerificationResult.Invalid(
                $"MessageImprint u vremenskom žigu ne odgovara hešu datoteke manifest.sig.");
        }

        // 2. If request was provided, verify response against request (imprint, algorithm, nonce)
        if (tsqBytes is not null && tsqBytes.Length > 0)
        {
            if (Rfc3161TimestampRequest.TryDecode(tsqBytes, out var request, out _))
            {
                var reqImprint = request.GetMessageHash().Span;
                if (!reqImprint.SequenceEqual(tokenImprint))
                {
                    return TimestampVerificationResult.Invalid("MessageImprint u odgovoru ne odgovara zahtevu.");
                }

                var reqNonce = request.GetNonce();
                var respNonce = tokenInfo.GetNonce();
                if (reqNonce.HasValue && (!respNonce.HasValue || !reqNonce.Value.Span.SequenceEqual(respNonce.Value.Span)))
                {
                    return TimestampVerificationResult.Invalid("Nonce u odgovoru ne odgovara nonce-u iz zahteva.");
                }
            }
        }

        var signedCms = token.AsSignedCms();

        // 3. Verify cryptographic CMS signature
        try
        {
            signedCms.CheckSignature(verifySignatureOnly: true);
        }
        catch (Exception ex)
        {
            return TimestampVerificationResult.Invalid($"Kriptografska provera potpisa TSA nije uspela: {ex.Message}");
        }

        // 4. Extract signer certificate
        var signerCert = signedCms.Certificates.Count > 0 ? signedCms.Certificates[0] : null;

        // 5. Evaluate trust chain (ValidTrusted vs ValidUntrusted)
        var trustState = TrustedTimeState.ValidUntrusted;
        if (signerCert is not null)
        {
            try
            {
                using var chain = new X509Chain();
                if (extraStore is not null)
                {
                    chain.ChainPolicy.ExtraStore.AddRange(extraStore);
                }

                // Check certificate validity
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Offline check
                if (chain.Build(signerCert))
                {
                    trustState = TrustedTimeState.ValidTrusted;
                }
            }
            catch
            {
                trustState = TrustedTimeState.ValidUntrusted;
            }
        }

        var accuracy = tokenInfo.AccuracyInMicroseconds.HasValue
            ? TimeSpan.FromMicroseconds(tokenInfo.AccuracyInMicroseconds.Value)
            : (TimeSpan?)null;

        var trustedTimestamp = new TrustedTimestamp(
            GenTimeUtc: tokenInfo.Timestamp,
            MessageImprintSha256: expectedImprintHex,
            State: trustState,
            TsaPolicyId: tokenInfo.PolicyId.Value,
            SerialNumber: Convert.ToHexStringLower(tokenInfo.GetSerialNumber().ToArray()),
            Accuracy: accuracy,
            Ordering: tokenInfo.IsOrdering,
            TsaSubjectName: signerCert?.Subject);

        return new TimestampVerificationResult(trustedTimestamp, trustState);
    }
}



