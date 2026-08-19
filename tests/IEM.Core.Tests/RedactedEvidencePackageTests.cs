using System.Security.Cryptography;
using System.Text;
using IEM.Core.Redaction;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and acceptance tests for Phase 3.0-16: Redacted Evidence Package.
/// Invariants 174-190.
/// </summary>
public sealed class RedactedEvidencePackageTests
{
    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Deterministic_redaction_same_input_same_output_Invariant_179()
    {
        var input = "Router BSSID: 00:1A:2B:3C:4D:5E on local subnet 192.168.1.1.";
        var policy = RedactionPolicy.StandardPrivacy;

        var entries1 = new List<RedactionEntry>();
        var out1 = RedactionEngine.RedactText(input, policy, "derived/report.json", entries1);

        var entries2 = new List<RedactionEntry>();
        var out2 = RedactionEngine.RedactText(input, policy, "derived/report.json", entries2);

        Assert.Equal(out1, out2);
        Assert.Equal(entries1.Count, entries2.Count);
        Assert.Contains("XX:XX:XX:XX:XX:XX", out1);
        Assert.Contains("192.168.1.1", out1); // StandardPrivacy preserves private IP, masks MAC
    }

    [Fact]
    public void StrictAnonymization_masks_both_mac_and_private_ip()
    {
        var input = "Gateway 192.168.1.1 has MAC 00:1A:2B:3C:4D:5E.";
        var policy = RedactionPolicy.StrictAnonymization;

        var entries = new List<RedactionEntry>();
        var redacted = RedactionEngine.RedactText(input, policy, "derived/report.json", entries);

        Assert.Contains("192.168.X.X", redacted);
        Assert.DoesNotContain("00:1A:2B:3C:4D:5E", redacted);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Redaction_metadata_stores_field_hash_not_plaintext_Invariant_182()
    {
        var input = "Target MAC: AA:BB:CC:DD:EE:FF";
        var policy = RedactionPolicy.StandardPrivacy;

        var entries = new List<RedactionEntry>();
        RedactionEngine.RedactText(input, policy, "derived/report.json", entries);

        Assert.Single(entries);
        var entry = entries[0];
        Assert.NotNull(entry.FieldHashBefore);
        Assert.Equal(64, entry.FieldHashBefore.Length); // Valid SHA-256 hex string
        Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", entry.FieldHashBefore);
    }

    [Fact]
    public void Redacted_package_verifier_succeeds_on_valid_derivative_Invariant_188()
    {
        var originalManifestHash = Hash("canonical_manifest_123");
        var policy = RedactionPolicy.StandardPrivacy;
        var files = new Dictionary<string, string>
        {
            ["derived/report.json"] = Hash("redacted_content"),
            ["redaction-manifest.json"] = Hash("redaction_meta"),
        };

        var manifest = RedactionEngine.CreateRedactionManifest(
            originalSessionId: "ses-100",
            originalManifestSha256: originalManifestHash,
            policy: policy,
            entries: Array.Empty<RedactionEntry>(),
            redactedFiles: files);

        var result = RedactedPackageVerifier.Verify(
            manifest,
            expectedOriginalManifestSha256: originalManifestHash,
            policy: policy,
            actualFileHashes: files,
            isDerivedSignatureValid: true);

        Assert.Equal(RedactedVerificationStatus.ValidRedactedDerivative, result.Status);
        Assert.Empty(result.Discrepancies);
    }

    [Fact]
    public void Mismatch_original_manifest_fails_verification_with_OriginalManifestMismatch_Invariant_176()
    {
        var originalManifestHash = Hash("canonical_manifest_123");
        var wrongOriginalHash = Hash("canonical_manifest_OTHER");
        var policy = RedactionPolicy.StandardPrivacy;
        var files = new Dictionary<string, string> { ["derived/report.json"] = Hash("content") };

        var manifest = RedactionEngine.CreateRedactionManifest(
            originalSessionId: "ses-100",
            originalManifestSha256: wrongOriginalHash,
            policy: policy,
            entries: Array.Empty<RedactionEntry>(),
            redactedFiles: files);

        var result = RedactedPackageVerifier.Verify(
            manifest,
            expectedOriginalManifestSha256: originalManifestHash,
            policy: policy,
            actualFileHashes: files,
            isDerivedSignatureValid: true);

        Assert.Equal(RedactedVerificationStatus.OriginalManifestMismatch, result.Status);
        Assert.NotEmpty(result.Discrepancies);
    }

    [Fact]
    public void Tampering_redacted_file_fails_verification_with_RedactedContentTampered_Invariant_183()
    {
        var originalManifestHash = Hash("canonical_manifest_123");
        var policy = RedactionPolicy.StandardPrivacy;
        var files = new Dictionary<string, string> { ["derived/report.json"] = Hash("expected_content") };

        var manifest = RedactionEngine.CreateRedactionManifest(
            originalSessionId: "ses-100",
            originalManifestSha256: originalManifestHash,
            policy: policy,
            entries: Array.Empty<RedactionEntry>(),
            redactedFiles: files);

        var tamperedFiles = new Dictionary<string, string> { ["derived/report.json"] = Hash("tampered_content") };

        var result = RedactedPackageVerifier.Verify(
            manifest,
            expectedOriginalManifestSha256: originalManifestHash,
            policy: policy,
            actualFileHashes: tamperedFiles,
            isDerivedSignatureValid: true);

        Assert.Equal(RedactedVerificationStatus.RedactedContentTampered, result.Status);
        Assert.NotEmpty(result.Discrepancies);
    }

    [Fact]
    public void Invalid_signature_fails_verification_with_SignatureInvalid_Invariant_177()
    {
        var originalManifestHash = Hash("canonical_manifest_123");
        var policy = RedactionPolicy.StandardPrivacy;
        var files = new Dictionary<string, string> { ["derived/report.json"] = Hash("content") };

        var manifest = RedactionEngine.CreateRedactionManifest(
            originalSessionId: "ses-100",
            originalManifestSha256: originalManifestHash,
            policy: policy,
            entries: Array.Empty<RedactionEntry>(),
            redactedFiles: files);

        var result = RedactedPackageVerifier.Verify(
            manifest,
            expectedOriginalManifestSha256: originalManifestHash,
            policy: policy,
            actualFileHashes: files,
            isDerivedSignatureValid: false);

        Assert.Equal(RedactedVerificationStatus.SignatureInvalid, result.Status);
    }
}
