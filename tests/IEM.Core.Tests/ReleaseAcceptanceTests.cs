using System.Security.Cryptography;
using System.Text;
using IEM.Core.Redaction;
using IEM.Core.Release;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and end-to-end acceptance tests for Phase 3.0-17: Installation, Release Integrity, Authenticode, SBOM, and Lifecycle.
/// Invariants 191-210.
/// </summary>
public sealed class ReleaseAcceptanceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "iem-release-tests", Guid.NewGuid().ToString("N"));

    public ReleaseAcceptanceTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static string Hash(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static ReleaseIdentity CreateIdentity() => new(
        ProductVersion: "3.0.0",
        InformationalVersion: "3.0.0+commit.abcdef1",
        GitCommit: "abcdef1234567890",
        BuildId: "build-20260819.1",
        BuildConfiguration: "Release",
        BuildTimestampUtc: DateTimeOffset.Parse("2026-08-19T02:00:00Z"),
        ReleaseChannel: "Stable",
        Architecture: "x64");

    private static ReleaseManifest CreateValidManifest(ReleaseIdentity identity)
    {
        var files = new Dictionary<string, string>
        {
            ["InternetEvidenceMonitor.exe"] = Hash("app_binary"),
            ["InternetEvidenceMonitor.Service.exe"] = Hash("service_binary"),
            ["InternetEvidenceMonitor.Setup.exe"] = Hash("setup_binary"),
        };

        var signatures = new Dictionary<string, AuthenticodeSignatureState>
        {
            ["InternetEvidenceMonitor.exe"] = new("InternetEvidenceMonitor.exe", true, "Internet Evidence Monitor Project", "THUMB1", true, DateTimeOffset.UtcNow),
            ["InternetEvidenceMonitor.Service.exe"] = new("InternetEvidenceMonitor.Service.exe", true, "Internet Evidence Monitor Project", "THUMB1", true, DateTimeOffset.UtcNow),
            ["InternetEvidenceMonitor.Setup.exe"] = new("InternetEvidenceMonitor.Setup.exe", true, "Internet Evidence Monitor Project", "THUMB1", true, DateTimeOffset.UtcNow),
        };

        var sbom = SbomGenerator.Generate(identity, new List<SbomComponent>
        {
            new("IEM.Core", "3.0.0", "nuget", "IEM Project", "MIT", Hash("iem_core")),
            new("IEM.Windows", "3.0.0", "nuget", "IEM Project", "MIT", Hash("iem_win")),
        });

        return new ReleaseManifest(
            Identity: identity,
            ArtifactSha256Hashes: files,
            Signatures: signatures,
            SbomSha256: sbom.SbomSha256,
            GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ReleaseGate_accepts_valid_signed_and_timestamped_release()
    {
        var id = CreateIdentity();
        var manifest = CreateValidManifest(id);
        var required = new[] { "InternetEvidenceMonitor.exe", "InternetEvidenceMonitor.Service.exe", "InternetEvidenceMonitor.Setup.exe" };

        var result = ReleaseGateEvaluator.Evaluate(manifest, required, "Internet Evidence Monitor Project");

        Assert.True(result.IsAccepted);
        Assert.Empty(result.Violations);
        Assert.Contains(result.PassedSteps, s => s.Contains("digitalnim potpisom"));
    }

    [Fact]
    public void ReleaseGate_fails_closed_when_executable_is_unsigned_Invariant_194()
    {
        var id = CreateIdentity();
        var manifest = CreateValidManifest(id);

        // Tamper signature state of Service
        var modifiedSigs = new Dictionary<string, AuthenticodeSignatureState>(manifest.Signatures)
        {
            ["InternetEvidenceMonitor.Service.exe"] = new("InternetEvidenceMonitor.Service.exe", false, null, null, false, null),
        };

        var unsignedManifest = manifest with { Signatures = modifiedSigs };
        var required = new[] { "InternetEvidenceMonitor.exe", "InternetEvidenceMonitor.Service.exe", "InternetEvidenceMonitor.Setup.exe" };

        var result = ReleaseGateEvaluator.Evaluate(unsignedManifest, required, "Internet Evidence Monitor Project");

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Violations, v => v.Contains("nije potpisan"));
    }

    [Fact]
    public void ReleaseGate_fails_closed_when_timestamp_is_missing_Invariant_197()
    {
        var id = CreateIdentity();
        var manifest = CreateValidManifest(id);

        var modifiedSigs = new Dictionary<string, AuthenticodeSignatureState>(manifest.Signatures)
        {
            ["InternetEvidenceMonitor.Setup.exe"] = new("InternetEvidenceMonitor.Setup.exe", true, "Internet Evidence Monitor Project", "THUMB1", false, null),
        };

        var noTsManifest = manifest with { Signatures = modifiedSigs };
        var required = new[] { "InternetEvidenceMonitor.Setup.exe" };

        var result = ReleaseGateEvaluator.Evaluate(noTsManifest, required, "Internet Evidence Monitor Project");

        Assert.False(result.IsAccepted);
        Assert.Contains(result.Violations, v => v.Contains("nema validan RFC 3161 Authenticode timestamp"));
    }

    [Fact]
    public void Installer_lifecycle_preserves_user_evidence_on_upgrade_Invariant_204()
    {
        var installDir = Path.Combine(_tempRoot, "ProgramFiles");
        var userEvidenceDir = Path.Combine(_tempRoot, "AppData");

        // 1. Initial install
        InstallerAcceptanceSimulator.SimulateInstall(installDir, "3.0.0");
        var evidenceFile = InstallerAcceptanceSimulator.SimulateRecordSession(userEvidenceDir, "ses-1", "sample evidence raw payload");

        Assert.True(File.Exists(evidenceFile));

        // 2. Upgrade to 3.0.1
        InstallerAcceptanceSimulator.SimulateUpgrade(installDir, userEvidenceDir, "3.0.1");

        // Invariant 204: evidence file MUST remain untouched
        Assert.True(File.Exists(evidenceFile));
        Assert.Equal("sample evidence raw payload", File.ReadAllText(evidenceFile));
        Assert.Contains("v3.0.1", File.ReadAllText(Path.Combine(installDir, "InternetEvidenceMonitor.exe")));
    }

    [Fact]
    public void Installer_lifecycle_preserves_user_evidence_on_uninstall_Invariant_205()
    {
        var installDir = Path.Combine(_tempRoot, "ProgramFiles");
        var userEvidenceDir = Path.Combine(_tempRoot, "AppData");

        InstallerAcceptanceSimulator.SimulateInstall(installDir, "3.0.0");
        var evidenceFile = InstallerAcceptanceSimulator.SimulateRecordSession(userEvidenceDir, "ses-1", "critical forensic evidence");

        // Uninstall
        InstallerAcceptanceSimulator.SimulateUninstall(installDir, userEvidenceDir);

        // Binaries are deleted
        Assert.False(Directory.Exists(installDir));

        // Invariant 205: User evidence in userEvidenceDir is strictly preserved!
        Assert.True(File.Exists(evidenceFile));
        Assert.Equal("critical forensic evidence", File.ReadAllText(evidenceFile));
    }

    [Fact]
    public void Ultimate_end_to_end_acceptance_scenario_3_0_17()
    {
        var installDir = Path.Combine(_tempRoot, "ProgramFiles");
        var userEvidenceDir = Path.Combine(_tempRoot, "AppData");

        // 1. Fresh Install
        InstallerAcceptanceSimulator.SimulateInstall(installDir, "3.0.0");
        Assert.True(File.Exists(Path.Combine(installDir, "InternetEvidenceMonitor.exe")));
        Assert.True(File.Exists(Path.Combine(installDir, "InternetEvidenceMonitor.Service.exe")));

        // 2. Run monitoring session & record evidence
        var payload = "Target 1.1.1.1 replied in 14.2ms. BSSID: 00:1A:2B:3C:4D:5E.";
        var evidenceFile = InstallerAcceptanceSimulator.SimulateRecordSession(userEvidenceDir, "session-e2e", payload);
        Assert.True(File.Exists(evidenceFile));

        // 3. Create Redacted Derivative
        var policy = RedactionPolicy.StandardPrivacy;
        var entries = new List<RedactionEntry>();
        var redactedContent = RedactionEngine.RedactText(payload, policy, "session-e2e/raw", entries);
        Assert.Contains("XX:XX:XX:XX:XX:XX", redactedContent);

        var originalManifestHash = Hash("original_manifest");
        var redactedFiles = new Dictionary<string, string> { ["session-e2e/raw"] = Hash(redactedContent) };
        var redactionManifest = RedactionEngine.CreateRedactionManifest(
            originalSessionId: "session-e2e",
            originalManifestSha256: originalManifestHash,
            policy: policy,
            entries: entries,
            redactedFiles: redactedFiles);

        // 4. Verify Redacted Derivative
        var redVerification = RedactedPackageVerifier.Verify(
            redactionManifest,
            originalManifestHash,
            policy,
            redactedFiles,
            isDerivedSignatureValid: true);
        Assert.Equal(RedactedVerificationStatus.ValidRedactedDerivative, redVerification.Status);

        // 5. Upgrade installation to 3.0.1
        InstallerAcceptanceSimulator.SimulateUpgrade(installDir, userEvidenceDir, "3.0.1");

        // 6. Verify original evidence persists unharmed
        Assert.True(File.Exists(evidenceFile));
        Assert.Equal(payload, File.ReadAllText(evidenceFile));

        // 7. Uninstall
        InstallerAcceptanceSimulator.SimulateUninstall(installDir, userEvidenceDir);
        Assert.False(Directory.Exists(installDir));

        // 8. Confirm user evidence is preserved post-uninstall
        Assert.True(File.Exists(evidenceFile));
        Assert.Equal(payload, File.ReadAllText(evidenceFile));
    }
}
