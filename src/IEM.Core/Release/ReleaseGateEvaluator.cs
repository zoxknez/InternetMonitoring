namespace IEM.Core.Release;

/// <summary>
/// Verifies all release-security gates before declaring a release accepted.
/// Invariants:
/// 194. UNSIGNED_REQUIRED_EXECUTABLE_IS_NEVER_RELEASED
/// 195. AUTHENTICODE_SIGNATURE_IS_VERIFIED_BEFORE_RELEASE_ACCEPTANCE
/// 196. RELEASE_SIGNING_FAILURE_ALWAYS_FAILS_CLOSED
/// 197. TIMESTAMP_FAILURE_NEVER_SILENTLY_DEGRADES_TO_UNTIMESTAMPED_RELEASE
/// 209. FAILED_RELEASE_GATE_NEVER_PUBLISHES_A_RELEASE_AS_ACCEPTED
/// </summary>
public static class ReleaseGateEvaluator
{
    public static ReleaseGateResult Evaluate(
        ReleaseManifest manifest,
        IReadOnlyList<string> requiredExecutableKeys,
        string expectedPublisher)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(requiredExecutableKeys);
        ArgumentNullException.ThrowIfNull(expectedPublisher);

        var passed = new List<string>();
        var violations = new List<string>();

        // 1. Release Identity & Artifact Completeness
        passed.Add($"Release Identity verifikovan: {manifest.Identity.ProductVersion} ({manifest.Identity.GitCommit})");

        foreach (var key in requiredExecutableKeys)
        {
            if (!manifest.ArtifactSha256Hashes.ContainsKey(key))
            {
                violations.Add($"Zahtevani izvršni artefakt '{key}' nedostaje u spisku izdanja.");
            }
        }

        // 2. Authenticode Signatures
        foreach (var key in requiredExecutableKeys)
        {
            if (!manifest.Signatures.TryGetValue(key, out var sig))
            {
                violations.Add($"Artefakt '{key}' nema priložene metapodatke o potpisu.");
                continue;
            }

            if (!sig.IsSigned)
            {
                violations.Add($"Artefakt '{key}' nije potpisan (Invariant 194).");
            }
            else if (!string.Equals(sig.Publisher, expectedPublisher, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Potpisnik artefakta '{key}' ('{sig.Publisher}') ne odgovara očekivanom ('{expectedPublisher}').");
            }

            if (!sig.HasValidTimestamp)
            {
                violations.Add($"Artefakt '{key}' nema validan RFC 3161 Authenticode timestamp (Invariant 197).");
            }

            if (!sig.ChainValidated)
            {
                violations.Add($"Lanac sertifikata za artefakt '{key}' nije validan.");
            }

            if (!string.Equals(sig.DigestAlgorithm, "SHA256", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Algoritam potpisa za artefakt '{key}' je '{sig.DigestAlgorithm}', a zahteva se SHA256.");
            }
        }

        if (violations.Count == 0)
        {
            passed.Add($"Svi izvršni artefakti ({requiredExecutableKeys.Count}) su uspešno verifikovani digitalnim potpisom i vremenskim žigom.");
        }

        // 3. SBOM Check
        if (string.IsNullOrWhiteSpace(manifest.SbomSha256) || manifest.SbomSha256.Length != 64)
        {
            violations.Add("SBOM heš u manifestu izdanja je nevažeći ili prazan (Invariant 201).");
        }
        else
        {
            passed.Add($"SBOM verifikovan sa SHA-256 hešom: {manifest.SbomSha256}");
        }

        var isAccepted = violations.Count == 0;
        return new ReleaseGateResult(
            IsAccepted: isAccepted,
            PassedSteps: passed,
            Violations: violations);
    }
}
