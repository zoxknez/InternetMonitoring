using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IEM.Evidence.Canonicalization;
using IEM.Evidence.Manifest;

namespace IEM.Core.Tests;

/// <summary>
/// Verifies the 3.0-2 canonical manifest and RFC 8785 JSON canonicalization scheme.
/// </summary>
public sealed class CanonicalManifestTests : IDisposable
{
    private readonly string _tempDir;

    public CanonicalManifestTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "iem-manifest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static EvidenceManifest CreateSampleManifest()
    {
        return new EvidenceManifest(
            ManifestSchemaVersion: 1,
            Canonicalization: "RFC8785-JCS",
            CreatedUtc: new DateTimeOffset(2026, 8, 19, 0, 0, 1, TimeSpan.Zero),
            Session: new ManifestSessionInfo(
                SessionId: "SES-2026-08-19-001",
                StartedUtc: new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
                FinishedUtc: new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero),
                EvidenceSchemaVersion: 4,
                ApplicationVersion: "3.0.0"),
            Evidence: new ManifestEvidenceSummary(
                RawChain: new ManifestRawChainRef(
                    RelativePath: "Raw/sesija.log",
                    FinalChainHash: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                    RecordCount: 100),
                DerivedLedger: null,
                InterpretationCatalog: null,
                LegalContextHash: null),
            Files:
            [
                new ManifestFileEntry("Izvestaj.html", 12345, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
                new ManifestFileEntry("Raw/sesija.log", 54321, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
            ],
            AcquisitionContext: new ManifestAcquisitionContext(
                Platform: "Linux",
                ProviderProvenance: new Dictionary<string, string>
                {
                    ["route"] = "Linux.Rtnetlink",
                    ["wifi"] = "NetworkManager.DBus",
                }));
    }

    [Fact]
    public void Same_model_always_produces_identical_canonical_bytes()
    {
        var manifest1 = CreateSampleManifest();
        var manifest2 = CreateSampleManifest();

        var bytes1 = manifest1.ToCanonicalBytes();
        var bytes2 = manifest2.ToCanonicalBytes();

        Assert.Equal(bytes1, bytes2);
        Assert.Equal(manifest1.ComputeManifestSha256(), manifest2.ComputeManifestSha256());
    }

    [Fact]
    public void Property_order_does_not_change_manifest_hash()
    {
        var jsonA = "{\"b\":\"value_b\",\"a\":\"value_a\"}";
        var jsonB = "{\"a\":\"value_a\",\"b\":\"value_b\"}";

        var canonicalA = JsonCanonicalizer.Canonicalize(jsonA);
        var canonicalB = JsonCanonicalizer.Canonicalize(jsonB);

        Assert.Equal(canonicalA, canonicalB);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(canonicalA)), Convert.ToHexStringLower(SHA256.HashData(canonicalB)));
    }

    [Fact]
    public void Current_culture_does_not_change_manifest()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("sr-Latn-RS");
            var bytesSerbian = CreateSampleManifest().ToCanonicalBytes();

            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var bytesUs = CreateSampleManifest().ToCanonicalBytes();

            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var bytesGerman = CreateSampleManifest().ToCanonicalBytes();

            Assert.Equal(bytesSerbian, bytesUs);
            Assert.Equal(bytesSerbian, bytesGerman);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Unicode_is_serialized_deterministically()
    {
        var json = "{\"letters\":\"čćžšđ ČĆŽŠĐ\"}";
        var canonical = JsonCanonicalizer.Canonicalize(json);
        var canonicalString = Encoding.UTF8.GetString(canonical);

        Assert.Equal("{\"letters\":\"čćžšđ ČĆŽŠĐ\"}", canonicalString);
    }

    [Fact]
    public void Windows_and_Linux_path_separator_do_not_change_manifest()
    {
        var rawSubdir = Path.Combine(_tempDir, "Raw");
        Directory.CreateDirectory(rawSubdir);

        var logPath = Path.Combine(rawSubdir, "sesija.log");
        File.WriteAllText(logPath, "line1\nline2\n");

        var inventory = ManifestBuilder.InventoryFiles(_tempDir);
        var entry = Assert.Single(inventory);

        Assert.Equal("Raw/sesija.log", entry.RelativePath);
        Assert.DoesNotContain("\\", entry.RelativePath);
    }

    [Fact]
    public void File_inventory_is_sorted_deterministically()
    {
        var fileB = Path.Combine(_tempDir, "b.txt");
        var fileA = Path.Combine(_tempDir, "a.txt");
        var fileC = Path.Combine(_tempDir, "c.txt");

        File.WriteAllText(fileB, "B");
        File.WriteAllText(fileA, "A");
        File.WriteAllText(fileC, "C");

        var inventory = ManifestBuilder.InventoryFiles(_tempDir);

        Assert.Equal(3, inventory.Count);
        Assert.Equal("a.txt", inventory[0].RelativePath);
        Assert.Equal("b.txt", inventory[1].RelativePath);
        Assert.Equal("c.txt", inventory[2].RelativePath);
    }

    [Fact]
    public void One_byte_change_in_evidence_changes_manifest_hash()
    {
        var file = Path.Combine(_tempDir, "data.txt");
        File.WriteAllText(file, "original");

        var manifest1 = ManifestBuilder.CreateManifest(_tempDir, "S1", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, "3.0.0");
        var hash1 = manifest1.ComputeManifestSha256();

        File.WriteAllText(file, "modified");

        var manifest2 = ManifestBuilder.CreateManifest(_tempDir, "S1", manifest1.Session.StartedUtc, manifest1.Session.FinishedUtc, "3.0.0");
        var hash2 = manifest2.ComputeManifestSha256();

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Manifest_does_not_include_itself_or_signature_or_timestamp()
    {
        File.WriteAllText(Path.Combine(_tempDir, "data.txt"), "data");
        File.WriteAllText(Path.Combine(_tempDir, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "manifest.json.tmp"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "manifest.sig"), "sig");
        File.WriteAllText(Path.Combine(_tempDir, "timestamp.tsr"), "tsr");

        var exportDir = Path.Combine(_tempDir, "Exports");
        Directory.CreateDirectory(exportDir);
        File.WriteAllText(Path.Combine(exportDir, "export.zip"), "zip");

        var inventory = ManifestBuilder.InventoryFiles(_tempDir);
        var entry = Assert.Single(inventory);

        Assert.Equal("data.txt", entry.RelativePath);
    }

    [Fact]
    public void Source_change_during_finalization_aborts_manifest()
    {
        var file = Path.Combine(_tempDir, "data.txt");
        File.WriteAllText(file, "initial data");

        var manifest = ManifestBuilder.CreateManifest(_tempDir, "S1", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, "3.0.0");

        // Mutate file size after manifest object creation to simulate TOCTOU violation
        File.WriteAllText(file, "mutated data that has different length");

        Assert.Throws<InvalidOperationException>(() =>
            ManifestBuilder.WriteManifestAtomically(_tempDir, manifest));

        Assert.False(File.Exists(Path.Combine(_tempDir, "manifest.json")));
    }

    [Fact]
    public void Partial_manifest_is_never_published()
    {
        File.WriteAllText(Path.Combine(_tempDir, "data.txt"), "consistent data");
        var manifest = ManifestBuilder.CreateManifest(_tempDir, "S1", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, "3.0.0");

        // Manifest write succeeds atomically
        var path = ManifestBuilder.WriteManifestAtomically(_tempDir, manifest);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(_tempDir, "manifest.json.tmp")));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(manifest.ToCanonicalBytes(), bytes);
    }

    [Fact]
    public void Golden_fixture_canonicalization_parity()
    {
        var inputPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Canonicalization", "input.json");
        if (!File.Exists(inputPath))
        {
            // Fallback for direct source test run
            inputPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Canonicalization", "input.json"));
        }

        var rawJson = File.ReadAllText(inputPath);
        var canonicalBytes = JsonCanonicalizer.Canonicalize(rawJson);
        var canonicalText = Encoding.UTF8.GetString(canonicalBytes);
        var hashHex = Convert.ToHexStringLower(SHA256.HashData(canonicalBytes));

        var expectedManifestPath = Path.Combine(Path.GetDirectoryName(inputPath)!, "expected-manifest.json");
        var expectedShaPath = Path.Combine(Path.GetDirectoryName(inputPath)!, "expected-sha256.txt");

        // Save golden expected if missing, or assert match
        if (!File.Exists(expectedManifestPath))
        {
            File.WriteAllBytes(expectedManifestPath, canonicalBytes);
            File.WriteAllText(expectedShaPath, hashHex);
        }

        var expectedBytes = File.ReadAllBytes(expectedManifestPath);
        while (expectedBytes.Length > 0 && (expectedBytes[^1] == (byte)'\n' || expectedBytes[^1] == (byte)'\r' || expectedBytes[^1] == (byte)' '))
        {
            expectedBytes = expectedBytes[..^1];
        }

        var expectedSha = File.ReadAllText(expectedShaPath).Trim();

        Assert.Equal(expectedBytes, canonicalBytes);
        Assert.Equal(expectedSha, hashHex);

    }

    [Fact]
    public void SignatureEnvelope_computes_deterministic_timestamp_imprint()
    {
        var envelope = new SignatureEnvelope(
            EnvelopeVersion: 1,
            ManifestSha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            KeyId: "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            SignatureSuite: SignatureSuite.EcdsaP256Sha256,
            PublicKeyBase64: "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...",
            KeyProtection: new IEM.Evidence.Crypto.KeyProtectionClaim(IEM.Evidence.Crypto.KeyProtectionLevel.SoftwareProtected, IEM.Evidence.Crypto.KeyProtectionEvidence.ProviderReported, "SoftwareKey"),
            SignatureBase64: "MEUCIQDx...",
            SignedUtc: new DateTimeOffset(2026, 8, 19, 0, 0, 2, TimeSpan.Zero));


        var imprint1 = envelope.ComputeTimestampMessageImprint();
        var imprint2 = envelope.ComputeTimestampMessageImprint();

        Assert.Equal(32, imprint1.Length);
        Assert.Equal(imprint1, imprint2);
    }

    [Fact]
    public void Baseline_v2_7_2_remains_readable_and_can_be_inventoried()
    {
        var sessionPath = BaselineSnapshot.Session;
        Assert.True(Directory.Exists(sessionPath));

        var inventory = ManifestBuilder.InventoryFiles(sessionPath);
        Assert.NotEmpty(inventory);

        var manifest = ManifestBuilder.CreateManifest(
            sessionPath,
            "SES-BASELINE-2.7.2",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(24),
            "3.0.0");

        Assert.NotNull(manifest);
        Assert.Equal("RFC8785-JCS", manifest.Canonicalization);
        Assert.NotEmpty(manifest.Files);
        Assert.NotNull(manifest.ComputeManifestSha256());
    }
}


