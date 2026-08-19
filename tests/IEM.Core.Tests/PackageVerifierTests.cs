using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IEM.Evidence.Canonicalization;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Evidence.Timestamping;
using IEM.Storage.Evidence;
using IEM.Verification.Engine;
using IEM.Verification.Models;
using IEM.Verification.Safety;


namespace IEM.Core.Tests;

/// <summary>
/// Unit and forensic integration tests for IEM.Verification engine (Phase 3.0-5).
/// </summary>
public sealed class PackageVerifierTests : IDisposable
{
    private readonly string _tempRoot;

    public PackageVerifierTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "iem-verifier-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task<string> CreateTestEvidencePackageAsync(
        string scenarioName,
        bool includeSignature = true,
        bool includeTimestamp = true,
        bool tamperRaw = false,
        bool tamperManifest = false,
        bool tamperSignature = false,
        bool tamperTimestamp = false,
        bool missingFile = false,
        int schemaVersion = 1,
        string? maliciousPath = null)
    {
        var packageDir = Path.Combine(_tempRoot, scenarioName);
        Directory.CreateDirectory(packageDir);

        // 1. Create raw evidence log
        var rawLogRelPath = "Evidence/raw.log";
        var rawLogDir = Path.Combine(packageDir, "Evidence");
        Directory.CreateDirectory(rawLogDir);
        var rawLogFullPath = Path.Combine(packageDir, rawLogRelPath.Replace('/', Path.DirectorySeparatorChar));

        string finalChainHash;
        long recordCount;

        using (var chainWriter = HashChainWriter.Open(rawLogFullPath))

        {
            chainWriter.Append(new SessionStartPayload(
                SessionId: "iem-test-session-001",
                ToolVersion: "3.0.0",
                StartedUtc: DateTimeOffset.UtcNow.AddHours(-1),
                PlannedDuration: TimeSpan.FromHours(1),
                MachineName: "TEST-HOST",
                InterfaceName: "Ethernet",
                Medium: IEM.Core.Model.LinkMedium.Ethernet,
                LinkSpeedBitsPerSecond: 1000_000_000,
                GatewayAddress: "192.168.1.1"));

            chainWriter.Append(new SessionStartPayload(
                SessionId: "iem-test-session-001",
                ToolVersion: "3.0.0",
                StartedUtc: DateTimeOffset.UtcNow,
                PlannedDuration: TimeSpan.FromHours(1),
                MachineName: "TEST-HOST",
                InterfaceName: "Ethernet",
                Medium: IEM.Core.Model.LinkMedium.Ethernet,
                LinkSpeedBitsPerSecond: 1000_000_000,
                GatewayAddress: "192.168.1.1"));


            finalChainHash = chainWriter.HeadHash;
            recordCount = chainWriter.EntriesWritten;
        }

        if (tamperRaw)
        {
            await File.AppendAllTextAsync(rawLogFullPath, "{\"tampered\": true}\n");
        }

        // 2. Extra payload file
        var dataRelPath = maliciousPath ?? "Evidence/data.csv";
        if (!missingFile && maliciousPath is null)

        {
            var dataFullPath = Path.Combine(packageDir, dataRelPath.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllTextAsync(dataFullPath, "metric,value\nrtt,15.2\n");
        }

        // 3. Build Manifest
        var filesInventory = new List<ManifestFileEntry>();
        var allFiles = Directory.GetFiles(packageDir, "*", SearchOption.AllDirectories);
        foreach (var file in allFiles)
        {
            var rel = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
            var bytes = await File.ReadAllBytesAsync(file);
            filesInventory.Add(new ManifestFileEntry(rel, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        if (maliciousPath is not null)
        {
            filesInventory.Add(new ManifestFileEntry(maliciousPath, 100, "0000000000000000000000000000000000000000000000000000000000000000"));
        }

        var manifest = new EvidenceManifest(
            ManifestSchemaVersion: schemaVersion,
            Canonicalization: "RFC8785-JCS",
            CreatedUtc: DateTimeOffset.UtcNow,
            Session: new ManifestSessionInfo(
                SessionId: "iem-test-session-001",
                StartedUtc: DateTimeOffset.UtcNow.AddHours(-1),
                FinishedUtc: DateTimeOffset.UtcNow,
                EvidenceSchemaVersion: 1,
                ApplicationVersion: "3.0.0"),
            Evidence: new ManifestEvidenceSummary(
                RawChain: new ManifestRawChainRef(rawLogRelPath, finalChainHash, recordCount),
                DerivedLedger: null,
                InterpretationCatalog: null,
                LegalContextHash: null),
            Files: filesInventory.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList(),
            AcquisitionContext: new ManifestAcquisitionContext(
                Platform: "Windows",
                ProviderProvenance: new Dictionary<string, string>()));

        var manifestPath = Path.Combine(packageDir, EvidenceManifest.FileName);
        var canonicalBytes = manifest.ToCanonicalBytes();
        await File.WriteAllBytesAsync(manifestPath, canonicalBytes);


        if (tamperManifest)
        {
            await File.WriteAllTextAsync(manifestPath, "{\"tampered\": true}");
        }

        // 4. Signature
        var identity = new EphemeralSoftwareSigningIdentity();
        if (includeSignature)
        {
            var sigEnvelope = await ManifestSigner.SignManifestAtomicallyAsync(packageDir, identity);

            if (tamperSignature)
            {
                var sigPath = Path.Combine(packageDir, SignatureEnvelope.FileName);
                await File.WriteAllTextAsync(sigPath, "{\"signature\": \"invalid_sig\"}");
            }
        }


        // 5. Timestamp
        if (includeTimestamp && includeSignature)
        {
            var sigPath = Path.Combine(packageDir, SignatureEnvelope.FileName);
            var sigBytes = await File.ReadAllBytesAsync(sigPath);
            var imprint = SHA256.HashData(sigBytes);
            var nonce = RandomNumberGenerator.GetBytes(16);

            using var rsa = RSA.Create(2048);
            var certReq = new CertificateRequest("CN=IEM Test TSA, O=IEM, C=RS", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, critical: true));
            certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            var cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            var tsrDir = Path.Combine(packageDir, "Evidence", "timestamp");
            Directory.CreateDirectory(tsrDir);

            var req = System.Security.Cryptography.Pkcs.Rfc3161TimestampRequest.CreateFromHash(
                imprint,
                HashAlgorithmName.SHA256,
                requestSignerCertificates: true,
                nonce: nonce);
            await File.WriteAllBytesAsync(Path.Combine(tsrDir, "timestamp.tsq"), req.Encode());

            if (tamperTimestamp)
            {
                await File.WriteAllBytesAsync(Path.Combine(tsrDir, "timestamp.tsr"), new byte[] { 1, 2, 3, 4 });
            }
            else
            {
                // Issue minimal mock token or real DER token
                var writer = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
                using (writer.PushSequence())
                {
                    writer.WriteInteger(1);
                    writer.WriteObjectIdentifier("1.3.6.1.4.1.12345.1");
                    using (writer.PushSequence())
                    {
                        using (writer.PushSequence())
                        {
                            writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                            writer.WriteNull();
                        }
                        writer.WriteOctetString(imprint);
                    }
                    writer.WriteInteger(123456789);
                    writer.WriteGeneralizedTime(DateTimeOffset.UtcNow);
                    writer.WriteInteger(new System.Numerics.BigInteger(nonce, isUnsigned: true, isBigEndian: true));
                }

                var contentInfo = new System.Security.Cryptography.Pkcs.ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), writer.Encode());
                var signedCms = new System.Security.Cryptography.Pkcs.SignedCms(contentInfo, detached: false);
                var signer = new System.Security.Cryptography.Pkcs.CmsSigner(cert)
                {
                    DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
                    IncludeOption = System.Security.Cryptography.X509Certificates.X509IncludeOption.EndCertOnly,
                };
                var essWriter = new System.Formats.Asn1.AsnWriter(System.Formats.Asn1.AsnEncodingRules.DER);
                using (essWriter.PushSequence())
                {
                    using (essWriter.PushSequence())
                    {
                        using (essWriter.PushSequence())
                        {
                            essWriter.WriteOctetString(SHA256.HashData(cert.RawData));
                        }
                    }
                }
                signer.SignedAttributes.Add(new System.Security.Cryptography.Pkcs.Pkcs9AttributeObject(new Oid("1.2.840.113549.1.9.16.2.47"), essWriter.Encode()));
                signedCms.ComputeSignature(signer);

                await File.WriteAllBytesAsync(Path.Combine(tsrDir, "timestamp.tsr"), signedCms.Encode());
            }
        }

        return packageDir;
    }

    [Fact]
    public async Task Valid_package_returns_ValidTrustNotEstablished_and_exit_code_10()
    {
        var package = await CreateTestEvidencePackageAsync("valid-pkg");
        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.ValidTrustNotEstablished, report.Overall);
        Assert.Equal(10, report.ExitCode);
        Assert.Equal(IntegrityStatus.Verified, report.Integrity);
        Assert.Equal(TrustStatus.NotEstablished, report.Trust);
        Assert.Equal(LayerStatus.Verified, report.Layers.RawChain?.Status);
        Assert.Equal(LayerStatus.Verified, report.Layers.Manifest?.Status);
        Assert.Equal(LayerStatus.Verified, report.Layers.Signature?.Status);
        Assert.Equal(LayerStatus.ValidUntrusted, report.Layers.TrustedTimestamp?.Status);
    }

    [Fact]
    public async Task Pending_timestamp_returns_Incomplete_and_exit_code_20()
    {
        var package = await CreateTestEvidencePackageAsync("pending-ts-pkg", includeTimestamp: false);

        // Put timestamp.tsq only to signify pending
        var tsDir = Path.Combine(package, "Evidence", "timestamp");
        Directory.CreateDirectory(tsDir);
        await File.WriteAllTextAsync(Path.Combine(tsDir, "timestamp.tsq"), "pending request");

        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.Incomplete, report.Overall);
        Assert.Equal(20, report.ExitCode);
        Assert.Equal(IntegrityStatus.Incomplete, report.Integrity);
        Assert.Equal(LayerStatus.Pending, report.Layers.TrustedTimestamp?.Status);
    }

    [Fact]
    public async Task Tampered_raw_chain_returns_Invalid_and_exit_code_30()
    {
        var package = await CreateTestEvidencePackageAsync("tampered-raw-pkg", tamperRaw: true);
        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(30, report.ExitCode);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
        Assert.Equal(LayerStatus.Invalid, report.Layers.RawChain?.Status);
    }

    [Fact]
    public async Task Tampered_manifest_returns_Invalid_and_exit_code_30()
    {
        var package = await CreateTestEvidencePackageAsync("tampered-manifest-pkg", tamperManifest: true);
        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(30, report.ExitCode);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
    }

    [Fact]
    public async Task Tampered_signature_returns_Invalid_and_exit_code_30()
    {
        var package = await CreateTestEvidencePackageAsync("tampered-sig-pkg", tamperSignature: true);
        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(30, report.ExitCode);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
        Assert.Equal(LayerStatus.Invalid, report.Layers.Signature?.Status);
    }

    [Fact]
    public async Task Tampered_timestamp_returns_Invalid_and_exit_code_30()
    {
        var package = await CreateTestEvidencePackageAsync("tampered-ts-pkg", tamperTimestamp: true);
        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(30, report.ExitCode);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
        Assert.Equal(LayerStatus.Invalid, report.Layers.TrustedTimestamp?.Status);
    }

    [Fact]
    public async Task Path_traversal_in_manifest_is_blocked_by_PathSafety_Invariant_29()
    {
        var package = await CreateTestEvidencePackageAsync("path-traversal-pkg", maliciousPath: "../../Windows/System32/cmd.exe");
        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(30, report.ExitCode);
        Assert.Equal(LayerStatus.Invalid, report.Layers.Manifest?.Status);
        Assert.Contains(report.Layers.Manifest?.Violations ?? Array.Empty<string>(), v => v.Contains("zabranjene segmente") || v.Contains("izlazi van"));
    }

    [Fact]
    public async Task Unsupported_schema_version_returns_Unsupported_and_exit_code_40()
    {
        var package = await CreateTestEvidencePackageAsync("unsupported-schema-pkg", schemaVersion: 999);
        var report = await PackageVerifier.VerifyPackageAsync(package);

        Assert.Equal(OverallStatus.Unsupported, report.Overall);
        Assert.Equal(40, report.ExitCode);
        Assert.Equal(LayerStatus.Unsupported, report.Layers.Manifest?.Status);
    }

    [Fact]
    public async Task Nonexistent_directory_returns_InputError_and_exit_code_50()
    {
        var nonExistentPath = Path.Combine(_tempRoot, "nonexistent-folder-12345");
        var report = await PackageVerifier.VerifyPackageAsync(nonExistentPath);

        Assert.Equal(OverallStatus.InputError, report.Overall);
        Assert.Equal(50, report.ExitCode);
    }

    [Fact]
    public async Task Verification_is_strictly_read_only_and_does_not_modify_package_Invariant_32()
    {
        var package = await CreateTestEvidencePackageAsync("read-only-pkg");

        // Record exact state before verification
        var filesBefore = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .Select(f => (Path: f, Hash: Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), Length: new FileInfo(f).Length))
            .ToList();

        // Run verification
        var report = await PackageVerifier.VerifyPackageAsync(package, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.ValidTrustNotEstablished, report.Overall);

        // Verify exact state after verification
        var filesAfter = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
            .OrderBy(f => f)
            .Select(f => (Path: f, Hash: Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))), Length: new FileInfo(f).Length))
            .ToList();

        Assert.Equal(filesBefore.Count, filesAfter.Count);
        for (var i = 0; i < filesBefore.Count; i++)
        {
            Assert.Equal(filesBefore[i].Path, filesAfter[i].Path);
            Assert.Equal(filesBefore[i].Hash, filesAfter[i].Hash);
            Assert.Equal(filesBefore[i].Length, filesAfter[i].Length);
        }
    }

    [Fact]
    public async Task Expected_key_id_matching_behavior()
    {
        var package = await CreateTestEvidencePackageAsync("key-id-pkg");

        // 1. Read envelope to find actual key ID
        var sigPath = Path.Combine(package, SignatureEnvelope.FileName);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var envelope = JsonSerializer.Deserialize<SignatureEnvelope>(await File.ReadAllBytesAsync(sigPath), jsonOptions)!;


        // 2. Verify with matching expected KeyId
        var reportMatching = await PackageVerifier.VerifyPackageAsync(package, new VerificationOptions { ExpectedKeyId = envelope.KeyId });
        Assert.True(reportMatching.Layers.Signature?.IsKeyMatched);

        // 3. Verify with mismatched expected KeyId
        var reportMismatch = await PackageVerifier.VerifyPackageAsync(package, new VerificationOptions { ExpectedKeyId = "sha256:0000000000000000000000000000000000000000000000000000000000000000" });
        Assert.False(reportMismatch.Layers.Signature?.IsKeyMatched);
    }

    [Fact]
    public async Task Golden_forensic_packages_verification()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Verifier");
        if (!Directory.Exists(fixtureDir))
        {
            fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Verifier"));
        }
        Directory.CreateDirectory(fixtureDir);

        // 1. valid-untrusted
        var validUntrustedDir = Path.Combine(fixtureDir, "valid-untrusted");
        if (!Directory.Exists(validUntrustedDir))
        {
            var created = await CreateTestEvidencePackageAsync("fixture-valid-untrusted");
            CopyDirectory(created, validUntrustedDir);
        }
        var reportValidUntrusted = await PackageVerifier.VerifyPackageAsync(validUntrustedDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.ValidTrustNotEstablished, reportValidUntrusted.Overall);

        // 2. pending-timestamp
        var pendingTsDir = Path.Combine(fixtureDir, "pending-timestamp");
        if (!Directory.Exists(pendingTsDir))
        {
            var created = await CreateTestEvidencePackageAsync("fixture-pending-ts", includeTimestamp: false);
            Directory.CreateDirectory(Path.Combine(created, "Evidence", "timestamp"));
            await File.WriteAllTextAsync(Path.Combine(created, "Evidence", "timestamp", "timestamp.tsq"), "pending request");
            CopyDirectory(created, pendingTsDir);
        }
        var reportPending = await PackageVerifier.VerifyPackageAsync(pendingTsDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Incomplete, reportPending.Overall);

        // 3. tampered-raw
        var tamperedRawDir = Path.Combine(fixtureDir, "tampered-raw");
        if (!Directory.Exists(tamperedRawDir))
        {
            var created = await CreateTestEvidencePackageAsync("fixture-tampered-raw", tamperRaw: true);
            CopyDirectory(created, tamperedRawDir);
        }
        var reportTamperedRaw = await PackageVerifier.VerifyPackageAsync(tamperedRawDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, reportTamperedRaw.Overall);

        // 4. tampered-manifest
        var tamperedManifestDir = Path.Combine(fixtureDir, "tampered-manifest");
        if (!Directory.Exists(tamperedManifestDir))
        {
            var created = await CreateTestEvidencePackageAsync("fixture-tampered-manifest", tamperManifest: true);
            CopyDirectory(created, tamperedManifestDir);
        }
        var reportTamperedManifest = await PackageVerifier.VerifyPackageAsync(tamperedManifestDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, reportTamperedManifest.Overall);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destinationDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}

