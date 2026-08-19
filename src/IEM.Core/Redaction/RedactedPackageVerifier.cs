namespace IEM.Core.Redaction;

/// <summary>
/// Verifies the cryptographic chain-of-derivation and integrity of a redacted evidence package.
/// Invariants:
/// 176. REDACTED_PACKAGE_ALWAYS_BINDS_TO_THE_ORIGINAL_MANIFEST_HASH
/// 177. ORIGINAL_SIGNATURE_IS_NEVER_REPRESENTED_AS_SIGNING_REDACTED_CONTENT
/// 183. REDACTED_PACKAGE_TAMPERING_NEVER_INVALIDATES_SOURCE_EVIDENCE
/// </summary>
public static class RedactedPackageVerifier
{
    public static RedactedVerificationResult Verify(
        RedactionManifest manifest,
        string expectedOriginalManifestSha256,
        RedactionPolicy policy,
        IReadOnlyDictionary<string, string> actualFileHashes,
        bool isDerivedSignatureValid)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(expectedOriginalManifestSha256);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(actualFileHashes);

        var discrepancies = new List<string>();

        // 1. Invariant 176: Check binding to original manifest
        if (!string.Equals(manifest.OriginalManifestSha256, expectedOriginalManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            discrepancies.Add($"Heš originalnog manifesta u redigovanom paketu ('{manifest.OriginalManifestSha256}') ne odgovara očekivanom ('{expectedOriginalManifestSha256}').");
            return new RedactedVerificationResult(
                Status: RedactedVerificationStatus.OriginalManifestMismatch,
                OriginalManifestSha256: expectedOriginalManifestSha256,
                DerivedManifestSha256: manifest.OriginalManifestSha256,
                PolicyHash: policy.PolicyHash,
                Discrepancies: discrepancies);
        }

        // 2. Invariant 178: Check policy hash
        if (!string.Equals(manifest.RedactionPolicyHash, policy.PolicyHash, StringComparison.OrdinalIgnoreCase))
        {
            discrepancies.Add($"Heš politike redakcije ('{manifest.RedactionPolicyHash}') ne odgovara politici ('{policy.PolicyHash}').");
            return new RedactedVerificationResult(
                Status: RedactedVerificationStatus.RedactionPolicyMismatch,
                OriginalManifestSha256: expectedOriginalManifestSha256,
                DerivedManifestSha256: manifest.OriginalManifestSha256,
                PolicyHash: policy.PolicyHash,
                Discrepancies: discrepancies);
        }

        // 3. File hash integrity
        foreach (var (filePath, expectedHash) in manifest.RedactedFileHashes)
        {
            if (!actualFileHashes.TryGetValue(filePath, out var actualHash) ||
                !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                discrepancies.Add($"Heš redigovanog fajla '{filePath}' je izmenjen ili ne postoji (Očekivano: '{expectedHash}', Stvarno: '{actualHash ?? "N/A"}').");
            }
        }

        if (discrepancies.Count > 0)
        {
            return new RedactedVerificationResult(
                Status: RedactedVerificationStatus.RedactedContentTampered,
                OriginalManifestSha256: expectedOriginalManifestSha256,
                DerivedManifestSha256: manifest.OriginalManifestSha256,
                PolicyHash: policy.PolicyHash,
                Discrepancies: discrepancies);
        }

        // 4. Invariant 188: Signature validity of derived package
        if (!isDerivedSignatureValid)
        {
            discrepancies.Add("Digitalni potpis redigovanog paketa nije validan.");
            return new RedactedVerificationResult(
                Status: RedactedVerificationStatus.SignatureInvalid,
                OriginalManifestSha256: expectedOriginalManifestSha256,
                DerivedManifestSha256: manifest.OriginalManifestSha256,
                PolicyHash: policy.PolicyHash,
                Discrepancies: discrepancies);
        }

        return new RedactedVerificationResult(
            Status: RedactedVerificationStatus.ValidRedactedDerivative,
            OriginalManifestSha256: expectedOriginalManifestSha256,
            DerivedManifestSha256: manifest.OriginalManifestSha256,
            PolicyHash: policy.PolicyHash,
            Discrepancies: Array.Empty<string>());
    }
}
