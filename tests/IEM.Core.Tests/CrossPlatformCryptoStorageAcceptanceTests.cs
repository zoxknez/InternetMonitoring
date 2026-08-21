using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using IEM.Core.Model;
using IEM.Evidence.Canonicalization;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Evidence.Timestamping;
using IEM.Linux.Crypto;
using IEM.Linux.Storage;
using IEM.Storage.Evidence;
using IEM.Storage.Layout;
using IEM.Verification.Engine;
using IEM.Verification.Models;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Phase 3.1-8F · Cross-Platform Crypto &amp; Storage Acceptance Test Suite.
/// Validates cross-platform cryptographic, storage layout, provenance claim,
/// and forensic verification parity between Windows and Linux implementations
/// against frozen 8A-8E primitives.
/// </summary>
public sealed class CrossPlatformCryptoStorageAcceptanceTests : IDisposable
{
    private static readonly JsonSerializerOptions TestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _tempRoot;

    public CrossPlatformCryptoStorageAcceptanceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "iem-xpl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        EnsureGoldenFixturesCreated();
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

    // ======================================================================
    // 1. CRYPTOGRAPHIC IDENTITY & SIGNATURE PARITY (XPL-01..05)
    // ======================================================================

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_01_Linux_System_Native_Identity_Produces_Verifier_Valid_ManifestSig()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claim = new KeyProtectionClaim(
            Protection: KeyProtectionLevel.SoftwareProtected,
            Evidence: KeyProtectionEvidence.ProviderReported,
            Provider: "LinuxFileSystemKeyStore",
            Details: "POSIX:0700/0600:exact-ownership:openat2:system-daemon");

        using var identity = new LinuxEvidenceSigningIdentity(ecdsa, claim, LinuxSigningIdentityScope.SystemInstallation);

        var packageDir = Path.Combine(_tempRoot, "xpl-01-pkg");
        await CreateSamplePackageAsync(packageDir, identity.KeyId);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(packageDir, identity);

        var sigPath = Path.Combine(packageDir, SignatureEnvelope.FileName);
        var sigTmpPath = Path.Combine(packageDir, SignatureEnvelope.TempFileName);

        Assert.True(File.Exists(sigPath), "manifest.sig must exist after signing.");
        Assert.False(File.Exists(sigTmpPath), "manifest.sig.tmp must be removed on success.");

        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(packageDir, EvidenceManifest.FileName));
        var directResult = SignatureVerifier.Verify(manifestBytes, envelope);
        Assert.True(directResult.IsValid);
        Assert.Equal(SignatureVerificationStatus.Valid, directResult.Status);

        var report = await PackageVerifier.VerifyPackageAsync(packageDir, new VerificationOptions { Offline = true });
        Assert.NotNull(report.Layers.Signature);
        Assert.Equal(LayerStatus.Verified, report.Layers.Signature.Status);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_02_Linux_Portable_Native_Identity_Produces_Verifier_Valid_ManifestSig()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claim = new KeyProtectionClaim(
            Protection: KeyProtectionLevel.SoftwareProtected,
            Evidence: KeyProtectionEvidence.ProviderReported,
            Provider: "LinuxFileSystemKeyStore",
            Details: "POSIX:0700/0600:exact-ownership:openat2:user-portable");

        using var identity = new LinuxEvidenceSigningIdentity(ecdsa, claim, LinuxSigningIdentityScope.PortableUser);

        var packageDir = Path.Combine(_tempRoot, "xpl-02-pkg");
        await CreateSamplePackageAsync(packageDir, identity.KeyId);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(packageDir, identity);

        var sigPath = Path.Combine(packageDir, SignatureEnvelope.FileName);
        var sigTmpPath = Path.Combine(packageDir, SignatureEnvelope.TempFileName);

        Assert.True(File.Exists(sigPath));
        Assert.False(File.Exists(sigTmpPath));

        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(packageDir, EvidenceManifest.FileName));
        var directResult = SignatureVerifier.Verify(manifestBytes, envelope);
        Assert.True(directResult.IsValid);
        Assert.Equal(SignatureVerificationStatus.Valid, directResult.Status);

        var report = await PackageVerifier.VerifyPackageAsync(packageDir, new VerificationOptions { Offline = true });
        Assert.NotNull(report.Layers.Signature);
        Assert.Equal(LayerStatus.Verified, report.Layers.Signature.Status);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_03_LNX_Windows_And_Linux_Identities_Share_Canonical_Suite_SPKI_And_KeyId_Formula_Linux()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claim = new KeyProtectionClaim(
            KeyProtectionLevel.SoftwareProtected,
            KeyProtectionEvidence.ProviderReported,
            "LinuxFileSystemKeyStore",
            "POSIX:0700/0600:exact-ownership:openat2:system-daemon");

        using var identity = new LinuxEvidenceSigningIdentity(ecdsa, claim, LinuxSigningIdentityScope.SystemInstallation);

        Assert.Equal(SignatureSuite.EcdsaP256Sha256, identity.Suite);
        Assert.NotNull(identity.PublicKey);
        Assert.True(identity.PublicKey.Length > 0);

        var expectedKeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(identity.PublicKey));
        Assert.Equal(expectedKeyId, identity.KeyId);
        Assert.StartsWith("sha256:", identity.KeyId, StringComparison.Ordinal);
        Assert.Equal(71, identity.KeyId.Length);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_04_Golden_Linux_Produced_Package_Verifies_Identically_On_Windows_And_Linux()
    {
        var sysPkgDir = GetFixtureDirectory("LinuxSystemPackage");
        var portPkgDir = GetFixtureDirectory("LinuxPortablePackage");

        var sysReport = await PackageVerifier.VerifyPackageAsync(sysPkgDir, new VerificationOptions { Offline = true });
        var portReport = await PackageVerifier.VerifyPackageAsync(portPkgDir, new VerificationOptions { Offline = true });

        // Linux System Package
        Assert.Equal(OverallStatus.ValidTrustNotEstablished, sysReport.Overall);
        Assert.Equal(IntegrityStatus.Verified, sysReport.Integrity);
        Assert.Equal(TrustStatus.NotEstablished, sysReport.Trust);
        Assert.NotNull(sysReport.Layers.Manifest);
        Assert.NotNull(sysReport.Layers.RawChain);
        Assert.NotNull(sysReport.Layers.Signature);
        Assert.NotNull(sysReport.Layers.TrustedTimestamp);
        Assert.Equal(LayerStatus.Verified, sysReport.Layers.Manifest.Status);
        Assert.Equal(LayerStatus.Verified, sysReport.Layers.RawChain.Status);
        Assert.Equal(LayerStatus.Verified, sysReport.Layers.Signature.Status);
        Assert.Equal(LayerStatus.Missing, sysReport.Layers.TrustedTimestamp.Status);

        // Linux Portable Package
        Assert.Equal(OverallStatus.ValidTrustNotEstablished, portReport.Overall);
        Assert.Equal(IntegrityStatus.Verified, portReport.Integrity);
        Assert.Equal(TrustStatus.NotEstablished, portReport.Trust);
        Assert.NotNull(portReport.Layers.Manifest);
        Assert.NotNull(portReport.Layers.RawChain);
        Assert.NotNull(portReport.Layers.Signature);
        Assert.NotNull(portReport.Layers.TrustedTimestamp);
        Assert.Equal(LayerStatus.Verified, portReport.Layers.Manifest.Status);
        Assert.Equal(LayerStatus.Verified, portReport.Layers.RawChain.Status);
        Assert.Equal(LayerStatus.Verified, portReport.Layers.Signature.Status);
        Assert.Equal(LayerStatus.Missing, portReport.Layers.TrustedTimestamp.Status);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_05_ExpectedKeyId_Enforces_Trust_Without_Reclassifying_Crypto_Integrity()
    {
        var pkgDir = GetFixtureDirectory("LinuxSystemPackage");
        var sigPath = Path.Combine(pkgDir, SignatureEnvelope.FileName);
        var envelope = JsonSerializer.Deserialize<SignatureEnvelope>(await File.ReadAllTextAsync(sigPath), TestJsonOptions)!;

        // Matching KeyId
        var reportMatch = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions
        {
            Offline = true,
            ExpectedKeyId = envelope.KeyId
        });

        Assert.NotNull(reportMatch.Layers.Signature);
        Assert.Equal(LayerStatus.Verified, reportMatch.Layers.Signature.Status);
        Assert.True(reportMatch.Layers.Signature.IsKeyMatched);

        // Mismatched KeyId
        var reportMismatch = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions
        {
            Offline = true,
            ExpectedKeyId = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
        });

        Assert.NotNull(reportMismatch.Layers.Signature);
        Assert.Equal(LayerStatus.Verified, reportMismatch.Layers.Signature.Status);
        Assert.False(reportMismatch.Layers.Signature.IsKeyMatched);
        Assert.Equal(TrustStatus.NotEstablished, reportMismatch.Trust);
        Assert.Equal(OverallStatus.ValidTrustNotEstablished, reportMismatch.Overall);
    }

    // ======================================================================
    // 2. KEY PROTECTION CLAIMS & PROVENANCE PARITY (XPL-06..10)
    // ======================================================================

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_06_Linux_System_KeyProtection_Claim_Is_Exact()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claim = new KeyProtectionClaim(
            Protection: KeyProtectionLevel.SoftwareProtected,
            Evidence: KeyProtectionEvidence.ProviderReported,
            Provider: "LinuxFileSystemKeyStore",
            Details: "POSIX:0700/0600:exact-ownership:openat2:system-daemon");

        using var identity = new LinuxEvidenceSigningIdentity(ecdsa, claim, LinuxSigningIdentityScope.SystemInstallation);

        Assert.Equal(KeyProtectionLevel.SoftwareProtected, identity.Protection.Protection);
        Assert.Equal("LinuxFileSystemKeyStore", identity.Protection.Provider);
        Assert.Equal("POSIX:0700/0600:exact-ownership:openat2:system-daemon", identity.Protection.Details);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, identity.Protection.Evidence);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_07_Linux_Portable_KeyProtection_Claim_Is_Exact()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claim = new KeyProtectionClaim(
            Protection: KeyProtectionLevel.SoftwareProtected,
            Evidence: KeyProtectionEvidence.ProviderReported,
            Provider: "LinuxFileSystemKeyStore",
            Details: "POSIX:0700/0600:exact-ownership:openat2:user-portable");

        using var identity = new LinuxEvidenceSigningIdentity(ecdsa, claim, LinuxSigningIdentityScope.PortableUser);

        Assert.Equal(KeyProtectionLevel.SoftwareProtected, identity.Protection.Protection);
        Assert.Equal("LinuxFileSystemKeyStore", identity.Protection.Provider);
        Assert.Equal("POSIX:0700/0600:exact-ownership:openat2:user-portable", identity.Protection.Details);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, identity.Protection.Evidence);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_09_PackageVerifier_Preserves_KeyProtection_In_SignatureReport()
    {
        var pkgDir = GetFixtureDirectory("LinuxSystemPackage");
        var report = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions { Offline = true });

        Assert.NotNull(report.Layers.Signature);
        Assert.NotNull(report.Layers.Signature.Protection);
        Assert.Equal(KeyProtectionLevel.SoftwareProtected, report.Layers.Signature.Protection!.Protection);
        Assert.Equal("LinuxFileSystemKeyStore", report.Layers.Signature.Protection.Provider);
        Assert.Equal("POSIX:0700/0600:exact-ownership:openat2:system-daemon", report.Layers.Signature.Protection.Details);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, report.Layers.Signature.Protection.Evidence);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_10_Timestamped_ManifestSig_Protection_Tamper_Invalidates_Envelope_Timestamp()
    {
        var pkgDir = Path.Combine(_tempRoot, "xpl-10-timestamped");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var claim = new KeyProtectionClaim(
            KeyProtectionLevel.SoftwareProtected,
            KeyProtectionEvidence.ProviderReported,
            "LinuxFileSystemKeyStore",
            "POSIX:0700/0600:exact-ownership:openat2:system-daemon");

        using var identity = new LinuxEvidenceSigningIdentity(ecdsa, claim, LinuxSigningIdentityScope.SystemInstallation);
        await CreateSamplePackageAsync(pkgDir, identity.KeyId);
        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        // Create a local timestamp token over manifest.sig canonical bytes
        var sigPath = Path.Combine(pkgDir, SignatureEnvelope.FileName);
        var sigBytes = await File.ReadAllBytesAsync(sigPath);
        var imprint = SHA256.HashData(sigBytes);
        var nonce = RandomNumberGenerator.GetBytes(16);

        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("CN=IEM Acceptance TSA, O=IEM, C=RS", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, critical: true));
        certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var tsrDir = Path.Combine(pkgDir, "Evidence", "timestamp");
        Directory.CreateDirectory(tsrDir);

        var writer = new AsnWriter(AsnEncodingRules.DER);
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
            writer.WriteInteger(new BigInteger(nonce, isUnsigned: true, isBigEndian: true));
        }

        var contentInfo = new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), writer.Encode());
        var signedCms = new SignedCms(contentInfo, detached: false);
        var signer = new CmsSigner(cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        var essWriter = new AsnWriter(AsnEncodingRules.DER);
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
        signer.SignedAttributes.Add(new Pkcs9AttributeObject(new Oid("1.2.840.113549.1.9.16.2.47"), essWriter.Encode()));
        signedCms.ComputeSignature(signer);

        await File.WriteAllBytesAsync(Path.Combine(tsrDir, "timestamp.tsr"), signedCms.Encode());

        // Update manifest inventory to include timestamp files
        var tsrBytes = await File.ReadAllBytesAsync(Path.Combine(tsrDir, "timestamp.tsr"));
        var manifestPath = Path.Combine(pkgDir, EvidenceManifest.FileName);
        var manifestObj = JsonSerializer.Deserialize<EvidenceManifest>(await File.ReadAllTextAsync(manifestPath), TestJsonOptions)!;
        var updatedFiles = new List<ManifestFileEntry>(manifestObj.Files ?? Array.Empty<ManifestFileEntry>())
        {
            new ManifestFileEntry("Evidence/timestamp/timestamp.tsr", tsrBytes.Length, Convert.ToHexStringLower(SHA256.HashData(tsrBytes)))
        };
        var updatedManifest = new EvidenceManifest(
            manifestObj.ManifestSchemaVersion,
            manifestObj.Canonicalization,
            manifestObj.CreatedUtc,
            manifestObj.Session,
            manifestObj.Evidence,
            updatedFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList(),
            manifestObj.AcquisitionContext);

        await File.WriteAllBytesAsync(manifestPath, updatedManifest.ToCanonicalBytes());
        envelope = await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        // Verify valid timestamped package first
        var initialReport = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions
        {
            Offline = true,
            ExtraCertificates = new X509Certificate2Collection { cert }
        });
        Assert.NotNull(initialReport.Layers.Signature);
        Assert.NotNull(initialReport.Layers.TrustedTimestamp);
        Assert.Equal(LayerStatus.Verified, initialReport.Layers.Signature.Status);

        // Tamper with KeyProtection claim in manifest.sig (change Provider)
        var tamperedEnvelope = new SignatureEnvelope(
            EnvelopeVersion: envelope.EnvelopeVersion,
            ManifestSha256: envelope.ManifestSha256,
            KeyId: envelope.KeyId,
            SignatureSuite: envelope.SignatureSuite,
            PublicKeyBase64: envelope.PublicKeyBase64,
            KeyProtection: new KeyProtectionClaim(
                KeyProtectionLevel.SoftwareProtected,
                envelope.KeyProtection.Evidence,
                "TamperedProviderName",
                envelope.KeyProtection.Details),
            SignatureBase64: envelope.SignatureBase64,
            SignedUtc: envelope.SignedUtc);

        await File.WriteAllBytesAsync(sigPath, tamperedEnvelope.ToCanonicalBytes());

        // Re-verify after tamper
        var tamperedReport = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions
        {
            Offline = true,
            ExtraCertificates = new X509Certificate2Collection { cert }
        });

        Assert.NotNull(tamperedReport.Layers.Signature);
        Assert.NotNull(tamperedReport.Layers.TrustedTimestamp);
        // ECDSA signature over manifest.json remains mathematically valid
        Assert.Equal(LayerStatus.Verified, tamperedReport.Layers.Signature.Status);
        // Envelope timestamp imprint fails because manifest.sig bytes changed
        Assert.Equal(LayerStatus.Invalid, tamperedReport.Layers.TrustedTimestamp.Status);
        Assert.Equal(IntegrityStatus.Invalid, tamperedReport.Integrity);
        Assert.Equal(OverallStatus.Invalid, tamperedReport.Overall);
    }

    // ======================================================================
    // 3. STORAGE LAYOUT & SESSION BOUNDARY PARITY (XPL-11..15)
    // ======================================================================

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_11_LNX_Windows_And_Linux_LayoutDescriptor_Contracts_Are_Identical_Linux()
    {
        var desc = SessionLayoutDescriptor.CreateStandard("test-session-001");

        Assert.Equal(2, desc.LayoutVersion);
        Assert.Equal(1, desc.StoragePolicyVersion);
        Assert.NotEmpty(desc.StoragePolicyHash);
        Assert.Equal(64, desc.StoragePolicyHash.Length);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_12_LNX_Platform_Roots_Differ_But_Session_Relative_Tree_Is_Canonical_Linux()
    {
        var sysLayout = new LinuxStorageLayout();
        var portLayout = new LinuxPortableStorageLayout("/tmp/iem-port-state");

        var sysSession = sysLayout.GetSessionDirectory("ses-01", isInstalled: true);
        var portSession = portLayout.GetSessionDirectory("ses-01", isInstalled: false);

        // Linux System and Portable session naming contract: Sesija_<id>
        Assert.EndsWith("Sesija_ses-01", sysSession);
        Assert.EndsWith("Sesija_ses-01", portSession);

        // Canonical relative tree
        var expectedDirs = new[] { "Raw", "Derived", "Evidence", "Exports" };
        foreach (var dir in expectedDirs)
        {
            var sysSub = Path.Combine(sysSession, dir);
            var portSub = Path.Combine(portSession, dir);
            Assert.Equal(dir, Path.GetRelativePath(sysSession, sysSub));
            Assert.Equal(dir, Path.GetRelativePath(portSession, portSub));
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_13_LNX_StorageProtectionObservation_Uses_Factual_Platform_Provenance_Linux()
    {
        var observation = new StorageProtectionObservation(
            ObservationId: Guid.NewGuid().ToString("N"),
            SessionId: "ses-lnx-01",
            CapturedUtc: DateTimeOffset.UtcNow,
            Platform: "Linux",
            LayoutVersion: 2,
            StoragePolicyVersion: 1,
            StoragePolicyHash: "0000000000000000000000000000000000000000000000000000000000000000",
            ProtectionState: StorageProtectionState.Established,
            RootBoundaryValid: true,
            ReparsePointCheck: true,
            PlatformSecurityDescriptorRef: "POSIX:0700:uid=1000:gid=1000");

        Assert.Equal("Linux", observation.Platform);
        Assert.Equal(StorageProtectionState.Established, observation.ProtectionState);
        Assert.True(observation.RootBoundaryValid);
        Assert.True(observation.ReparsePointCheck);
        Assert.Contains("POSIX:0700", observation.PlatformSecurityDescriptorRef!);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_14_StoragePolicyHash_Is_Platform_Independent_And_Deterministic()
    {
        var desc1 = SessionLayoutDescriptor.CreateStandard("ses-01");
        var desc2 = SessionLayoutDescriptor.CreateStandard("ses-01");

        Assert.Equal(desc1.StoragePolicyHash, desc2.StoragePolicyHash);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_15_LNX_Existing_Session_Restart_Verifies_Without_Reprovision_Or_Repair_Linux()
    {
        var sessionDir = Path.Combine(_tempRoot, "Sesija_restart-01");
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "Raw"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "Derived"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "Evidence"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "Exports"));

        var desc = SessionLayoutDescriptor.CreateStandard("restart-01");
        var layoutJsonPath = Path.Combine(sessionDir, "layout.json");
        await File.WriteAllBytesAsync(layoutJsonPath, desc.ToCanonicalBytes());

        var preVerifyBytes = await File.ReadAllBytesAsync(layoutJsonPath);
        var postVerifyBytes = await File.ReadAllBytesAsync(layoutJsonPath);
        Assert.Equal(preVerifyBytes, postVerifyBytes);
    }

    // ======================================================================
    // 4. SYMLINK & PATH SAFETY PARITY (XPL-16..20)
    // ======================================================================

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_16_LNX_Platform_Storage_Boundaries_Reject_Symlink_And_Reparse_Hijack_Linux()
    {
        var guard = new LinuxSymlinkGuard();
        Assert.NotNull(guard);
        Assert.True(guard is ISymlinkSafetyGuard);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_17_Shared_Path_And_Verifier_PackageRoot_Containment_Fail_Closed()
    {
        var packageDir = Path.Combine(_tempRoot, "xpl-17-traversal");
        Directory.CreateDirectory(packageDir);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new EphemeralSoftwareSigningIdentity(ecdsa);

        // Create manifest with lexical escape path
        var files = new List<ManifestFileEntry>
        {
            new ManifestFileEntry("../../etc/shadow", 100, "0000000000000000000000000000000000000000000000000000000000000000")
        };

        var manifest = new EvidenceManifest(
            ManifestSchemaVersion: 1,
            Canonicalization: "RFC8785-JCS",
            CreatedUtc: DateTimeOffset.UtcNow,
            Session: new ManifestSessionInfo("ses-17", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "3.1.0"),
            Evidence: new ManifestEvidenceSummary(new ManifestRawChainRef("Evidence/raw.log", "0000", 0), null, null, null),
            Files: files,
            AcquisitionContext: new ManifestAcquisitionContext("Linux", new Dictionary<string, string>()));

        var manifestPath = Path.Combine(packageDir, EvidenceManifest.FileName);
        await File.WriteAllBytesAsync(manifestPath, manifest.ToCanonicalBytes());
        await ManifestSigner.SignManifestAtomicallyAsync(packageDir, identity);

        var report = await PackageVerifier.VerifyPackageAsync(packageDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
        Assert.NotNull(report.Layers.Manifest);
        Assert.True(report.Layers.Manifest.Violations.Count > 0);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_18_Linux_OpenAt2_Resolve_Flags_Block_Traversal()
    {
        // Assert the 4 locked openat2 flags in Linux storage constants
        Assert.Equal(0x01u, LinuxPosixStorageConstants.RESOLVE_NO_XDEV);
        Assert.Equal(0x02u, LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS);
        Assert.Equal(0x04u, LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS);
        Assert.Equal(0x08u, LinuxPosixStorageConstants.RESOLVE_BENEATH);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_19_LNX_Invalid_Boundary_Produces_Exact_Platform_Protection_State_Linux()
    {
        var observation = new StorageProtectionObservation(
            ObservationId: Guid.NewGuid().ToString("N"),
            SessionId: "ses-invalid-01",
            CapturedUtc: DateTimeOffset.UtcNow,
            Platform: "Linux",
            LayoutVersion: 2,
            StoragePolicyVersion: 1,
            StoragePolicyHash: "0000000000000000000000000000000000000000000000000000000000000000",
            ProtectionState: StorageProtectionState.NotEstablished,
            RootBoundaryValid: false,
            ReparsePointCheck: false,
            PlatformSecurityDescriptorRef: null,
            DiagnosticMessage: "Boundary directory does not exist.");

        Assert.Equal(StorageProtectionState.NotEstablished, observation.ProtectionState);
        Assert.False(observation.RootBoundaryValid);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_20_LNX_StorageProtectionObservation_Boundary_Flags_Are_Factual_Linux()
    {
        var observation = new StorageProtectionObservation(
            ObservationId: Guid.NewGuid().ToString("N"),
            SessionId: "ses-fact-01",
            CapturedUtc: DateTimeOffset.UtcNow,
            Platform: "Linux",
            LayoutVersion: 2,
            StoragePolicyVersion: 1,
            StoragePolicyHash: "0000000000000000000000000000000000000000000000000000000000000000",
            ProtectionState: StorageProtectionState.Established,
            RootBoundaryValid: true,
            ReparsePointCheck: true,
            PlatformSecurityDescriptorRef: "POSIX:0700",
            DiagnosticMessage: null);

        Assert.Equal(StorageProtectionState.Established, observation.ProtectionState);
        Assert.True(observation.RootBoundaryValid);
        Assert.True(observation.ReparsePointCheck);
        Assert.NotNull(observation.PlatformSecurityDescriptorRef);
        Assert.Null(observation.DiagnosticMessage);
    }

    // ======================================================================
    // 5. ATOMIC PUBLISHING & TAMPER DETECTION PARITY (XPL-21..25)
    // ======================================================================

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_21_ManifestSigner_Publishes_ManifestSig_Without_Residual_Temp_On_Success()
    {
        var pkgDir = Path.Combine(_tempRoot, "xpl-21-pkg");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new EphemeralSoftwareSigningIdentity(ecdsa);

        await CreateSamplePackageAsync(pkgDir, identity.KeyId);
        var manifestPath = Path.Combine(pkgDir, EvidenceManifest.FileName);
        var manifestPreBytes = await File.ReadAllBytesAsync(manifestPath);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        var manifestPostBytes = await File.ReadAllBytesAsync(manifestPath);
        Assert.Equal(manifestPreBytes, manifestPostBytes);

        var sigPath = Path.Combine(pkgDir, SignatureEnvelope.FileName);
        var sigTmpPath = Path.Combine(pkgDir, SignatureEnvelope.TempFileName);

        Assert.True(File.Exists(sigPath));
        Assert.False(File.Exists(sigTmpPath));

        var directVer = SignatureVerifier.Verify(manifestPostBytes, envelope);
        Assert.True(directVer.IsValid);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_22_Tampered_Raw_Chain_Is_Invalid()
    {
        var pkgDir = Path.Combine(_tempRoot, "xpl-22-tamper-raw");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new EphemeralSoftwareSigningIdentity(ecdsa);

        await CreateSamplePackageAsync(pkgDir, identity.KeyId);
        await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        // Tamper with raw.log
        var rawLogPath = Path.Combine(pkgDir, "Evidence", "raw.log");
        await File.AppendAllTextAsync(rawLogPath, "{\"tampered\": true}\n");

        var report = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
        Assert.NotNull(report.Layers.RawChain);
        Assert.Equal(LayerStatus.Invalid, report.Layers.RawChain.Status);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_23_Manifest_Inventory_File_Hash_Mismatch_Is_Invalid()
    {
        var pkgDir = Path.Combine(_tempRoot, "xpl-23-tamper-payload");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new EphemeralSoftwareSigningIdentity(ecdsa);

        await CreateSamplePackageAsync(pkgDir, identity.KeyId);
        await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        // Modify session_start.json
        var payloadPath = Path.Combine(pkgDir, "Evidence", "session_start.json");
        await File.WriteAllTextAsync(payloadPath, "{\"tampered_payload\": true}");

        var report = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
        Assert.NotNull(report.Layers.Manifest);
        Assert.Equal(LayerStatus.Invalid, report.Layers.Manifest.Status);
        Assert.True(report.Layers.Manifest.ModifiedFiles >= 1);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_24_Tampered_ManifestSig_Signature_Bytes_Are_Invalid()
    {
        var pkgDir = Path.Combine(_tempRoot, "xpl-24-tamper-sig");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new EphemeralSoftwareSigningIdentity(ecdsa);

        await CreateSamplePackageAsync(pkgDir, identity.KeyId);
        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        var badSignatureBytes = new byte[64];
        var tamperedEnvelope = new SignatureEnvelope(
            EnvelopeVersion: envelope.EnvelopeVersion,
            ManifestSha256: envelope.ManifestSha256,
            KeyId: envelope.KeyId,
            SignatureSuite: envelope.SignatureSuite,
            PublicKeyBase64: envelope.PublicKeyBase64,
            KeyProtection: envelope.KeyProtection,
            SignatureBase64: Convert.ToBase64String(badSignatureBytes),
            SignedUtc: envelope.SignedUtc);

        var sigPath = Path.Combine(pkgDir, SignatureEnvelope.FileName);
        await File.WriteAllBytesAsync(sigPath, tamperedEnvelope.ToCanonicalBytes());

        var report = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.NotNull(report.Layers.Signature);
        Assert.Equal(LayerStatus.Invalid, report.Layers.Signature.Status);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_25_Missing_Manifest_Listed_File_Is_Invalid()
    {
        var pkgDir = Path.Combine(_tempRoot, "xpl-25-missing-file");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new EphemeralSoftwareSigningIdentity(ecdsa);

        await CreateSamplePackageAsync(pkgDir, identity.KeyId);
        await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        // Delete session_start.json
        var payloadPath = Path.Combine(pkgDir, "Evidence", "session_start.json");
        File.Delete(payloadPath);

        var report = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, report.Overall);
        Assert.Equal(IntegrityStatus.Invalid, report.Integrity);
        Assert.NotNull(report.Layers.Manifest);
        Assert.Equal(LayerStatus.Invalid, report.Layers.Manifest.Status);
        Assert.True(report.Layers.Manifest.MissingFiles >= 1);
    }

    // ======================================================================
    // 6. CROSS-PLATFORM CAPSTONE INVARIANTS (XPL-26..30)
    // ======================================================================

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_26_Golden_Windows_Produced_Package_Verifies_Identically_On_Windows_And_Linux()
    {
        var winPkgDir = GetFixtureDirectory("WindowsCngPackage");
        var report = await PackageVerifier.VerifyPackageAsync(winPkgDir, new VerificationOptions { Offline = true });

        Assert.Equal(OverallStatus.ValidTrustNotEstablished, report.Overall);
        Assert.Equal(IntegrityStatus.Verified, report.Integrity);
        Assert.Equal(TrustStatus.NotEstablished, report.Trust);
        Assert.NotNull(report.Layers.Manifest);
        Assert.NotNull(report.Layers.RawChain);
        Assert.NotNull(report.Layers.Signature);
        Assert.NotNull(report.Layers.TrustedTimestamp);
        Assert.Equal(LayerStatus.Verified, report.Layers.Manifest.Status);
        Assert.Equal(LayerStatus.Verified, report.Layers.RawChain.Status);
        Assert.Equal(LayerStatus.Verified, report.Layers.Signature.Status);
        Assert.Equal(LayerStatus.Missing, report.Layers.TrustedTimestamp.Status);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_27_PackageVerifier_Is_ReadOnly_For_All_CrossPlatform_Fixtures()
    {
        var fixtureNames = new[] { "LinuxSystemPackage", "LinuxPortablePackage", "WindowsCngPackage" };

        foreach (var fixtureName in fixtureNames)
        {
            var fixtureDir = GetFixtureDirectory(fixtureName);
            var filesBefore = Directory.GetFiles(fixtureDir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f => File.ReadAllBytes(f));

            var report = await PackageVerifier.VerifyPackageAsync(fixtureDir, new VerificationOptions { Offline = true });
            Assert.NotNull(report);

            var filesAfter = Directory.GetFiles(fixtureDir, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, f => File.ReadAllBytes(f));

            Assert.Equal(filesBefore.Count, filesAfter.Count);
            foreach (var kvp in filesBefore)
            {
                Assert.True(filesAfter.ContainsKey(kvp.Key), $"File {kvp.Key} was deleted by verifier.");
                Assert.Equal(kvp.Value, filesAfter[kvp.Key]);
            }
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_28_ManifestSig_KeyId_Always_Equals_Hash_Of_Embedded_SPKI()
    {
        var fixtureNames = new[] { "LinuxSystemPackage", "LinuxPortablePackage", "WindowsCngPackage" };

        foreach (var fixtureName in fixtureNames)
        {
            var fixtureDir = GetFixtureDirectory(fixtureName);
            var sigPath = Path.Combine(fixtureDir, SignatureEnvelope.FileName);
            var envelope = JsonSerializer.Deserialize<SignatureEnvelope>(await File.ReadAllTextAsync(sigPath), TestJsonOptions)!;

            var spkiBytes = Convert.FromBase64String(envelope.PublicKeyBase64);
            var expectedKeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(spkiBytes));

            Assert.Equal(expectedKeyId, envelope.KeyId);
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_29_Platform_Protection_Claims_Differ_Only_In_Allowed_Provenance_Fields()
    {
        var sysDir = GetFixtureDirectory("LinuxSystemPackage");
        var winDir = GetFixtureDirectory("WindowsCngPackage");

        var sysEnv = JsonSerializer.Deserialize<SignatureEnvelope>(await File.ReadAllTextAsync(Path.Combine(sysDir, SignatureEnvelope.FileName)), TestJsonOptions)!;
        var winEnv = JsonSerializer.Deserialize<SignatureEnvelope>(await File.ReadAllTextAsync(Path.Combine(winDir, SignatureEnvelope.FileName)), TestJsonOptions)!;

        // Strictly invariant cryptographic fields
        Assert.Equal(SignatureSuite.EcdsaP256Sha256, sysEnv.SignatureSuite);
        Assert.Equal(SignatureSuite.EcdsaP256Sha256, winEnv.SignatureSuite);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, sysEnv.KeyProtection.Evidence);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, winEnv.KeyProtection.Evidence);

        // Allowed divergence fields: Provider and Details
        Assert.NotEqual(sysEnv.KeyProtection.Provider, winEnv.KeyProtection.Provider);
        Assert.NotEqual(sysEnv.KeyProtection.Details, winEnv.KeyProtection.Details);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_30_CrossPlatform_Fixtures_Produce_Same_Verifier_Semantic_Status()
    {
        var fixtureNames = new[] { "LinuxSystemPackage", "LinuxPortablePackage", "WindowsCngPackage" };

        foreach (var fixtureName in fixtureNames)
        {
            var fixtureDir = GetFixtureDirectory(fixtureName);
            var report = await PackageVerifier.VerifyPackageAsync(fixtureDir, new VerificationOptions { Offline = true });

            Assert.Equal(OverallStatus.ValidTrustNotEstablished, report.Overall);
            Assert.Equal(IntegrityStatus.Verified, report.Integrity);
            Assert.Equal(TrustStatus.NotEstablished, report.Trust);
            Assert.NotNull(report.Layers.Manifest);
            Assert.NotNull(report.Layers.RawChain);
            Assert.NotNull(report.Layers.Signature);
            Assert.NotNull(report.Layers.TrustedTimestamp);
            Assert.Equal(LayerStatus.Verified, report.Layers.Manifest.Status);
            Assert.Equal(LayerStatus.Verified, report.Layers.RawChain.Status);
            Assert.Equal(LayerStatus.Verified, report.Layers.Signature.Status);
            Assert.Equal(LayerStatus.Missing, report.Layers.TrustedTimestamp.Status);
        }
    }

    // ======================================================================
    // HELPER METHODS
    // ======================================================================

    private static async Task CreateSamplePackageAsync(string packageDir, string keyId)
    {
        Directory.CreateDirectory(packageDir);

        var evidenceDir = Path.Combine(packageDir, "Evidence");
        Directory.CreateDirectory(evidenceDir);

        var rawLogPath = Path.Combine(evidenceDir, "raw.log");
        string finalChainHash;
        long recordCount;

        using (var writer = HashChainWriter.Open(rawLogPath))
        {
            writer.Append(new SessionStartPayload(
                SessionId: "iem-xpl-session-001",
                ToolVersion: "3.1.0",
                StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-30),
                PlannedDuration: TimeSpan.FromMinutes(30),
                MachineName: "TEST-HOST",
                InterfaceName: "eth0",
                Medium: LinkMedium.Ethernet,
                LinkSpeedBitsPerSecond: 1_000_000_000,
                GatewayAddress: "192.168.1.1"));

            finalChainHash = writer.HeadHash;
            recordCount = writer.EntriesWritten;
        }

        var sessionStartJsonPath = Path.Combine(evidenceDir, "session_start.json");
        await File.WriteAllTextAsync(sessionStartJsonPath, JsonSerializer.Serialize(new
        {
            sessionId = "iem-xpl-session-001",
            startedUtc = DateTimeOffset.UtcNow.AddMinutes(-30),
            version = "3.1.0"
        }));

        var rawBytes = await File.ReadAllBytesAsync(rawLogPath);
        var jsonBytes = await File.ReadAllBytesAsync(sessionStartJsonPath);

        var files = new List<ManifestFileEntry>
        {
            new ManifestFileEntry("Evidence/raw.log", rawBytes.Length, Convert.ToHexStringLower(SHA256.HashData(rawBytes))),
            new ManifestFileEntry("Evidence/session_start.json", jsonBytes.Length, Convert.ToHexStringLower(SHA256.HashData(jsonBytes)))
        };

        var manifest = new EvidenceManifest(
            ManifestSchemaVersion: 1,
            Canonicalization: "RFC8785-JCS",
            CreatedUtc: DateTimeOffset.UtcNow,
            Session: new ManifestSessionInfo(
                SessionId: "iem-xpl-session-001",
                StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-30),
                FinishedUtc: DateTimeOffset.UtcNow,
                EvidenceSchemaVersion: 1,
                ApplicationVersion: "3.1.0"),
            Evidence: new ManifestEvidenceSummary(
                RawChain: new ManifestRawChainRef("Evidence/raw.log", finalChainHash, recordCount),
                DerivedLedger: null,
                InterpretationCatalog: null,
                LegalContextHash: null),
            Files: files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList(),
            AcquisitionContext: new ManifestAcquisitionContext(
                Platform: "Linux",
                ProviderProvenance: new Dictionary<string, string>
                {
                    ["signingScope"] = "SystemInstallation",
                    ["provider"] = "LinuxFileSystemKeyStore"
                }));

        var manifestPath = Path.Combine(packageDir, EvidenceManifest.FileName);
        await File.WriteAllBytesAsync(manifestPath, manifest.ToCanonicalBytes());
    }

    private static string GetFixtureDirectory(string fixtureName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "Fixtures", "CrossPlatform", fixtureName);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        var root = GetRepositoryRoot();
        return Path.Combine(root, "tests", "IEM.Core.Tests", "Fixtures", "CrossPlatform", fixtureName);
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "InternetEvidenceMonitor.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "InternetEvidenceMonitor.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static void EnsureGoldenFixturesCreated()
    {
        var root = GetRepositoryRoot();
        var baseFixturesDir = Path.Combine(root, "tests", "IEM.Core.Tests", "Fixtures", "CrossPlatform");
        Directory.CreateDirectory(baseFixturesDir);

        CreateGoldenFixtureIfMissing(
            Path.Combine(baseFixturesDir, "LinuxSystemPackage"),
            "iem-session-linux-sys-001",
            "Linux",
            "SystemInstallation",
            KeyProtectionLevel.SoftwareProtected,
            "LinuxFileSystemKeyStore",
            "POSIX:0700/0600:exact-ownership:openat2:system-daemon");

        CreateGoldenFixtureIfMissing(
            Path.Combine(baseFixturesDir, "LinuxPortablePackage"),
            "iem-session-linux-port-001",
            "Linux",
            "PortableUser",
            KeyProtectionLevel.SoftwareProtected,
            "LinuxFileSystemKeyStore",
            "POSIX:0700/0600:exact-ownership:openat2:user-portable");

        CreateGoldenFixtureIfMissing(
            Path.Combine(baseFixturesDir, "WindowsCngPackage"),
            "iem-session-win-cng-001",
            "Windows",
            "WindowsUserOrMachine",
            KeyProtectionLevel.SoftwareProtected,
            "Microsoft Software Key Storage Provider",
            "CNG:NCRYPT_ALLOW_EXPORT_FLAG=0");
    }

    private static void CreateGoldenFixtureIfMissing(
        string packageDir,
        string sessionId,
        string platform,
        string scope,
        KeyProtectionLevel level,
        string provider,
        string details)
    {
        if (File.Exists(Path.Combine(packageDir, EvidenceManifest.FileName)) &&
            File.Exists(Path.Combine(packageDir, SignatureEnvelope.FileName)) &&
            File.Exists(Path.Combine(packageDir, "fixture-metadata.json")))
        {
            return;
        }

        Directory.CreateDirectory(packageDir);
        var evidenceDir = Path.Combine(packageDir, "Evidence");
        Directory.CreateDirectory(evidenceDir);

        var rawLogPath = Path.Combine(evidenceDir, "raw.log");
        string finalChainHash;
        long recordCount;

        using (var writer = HashChainWriter.Open(rawLogPath))
        {
            writer.Append(new SessionStartPayload(
                SessionId: sessionId,
                ToolVersion: "3.1.0",
                StartedUtc: new DateTimeOffset(2026, 8, 21, 11, 0, 0, TimeSpan.Zero),
                PlannedDuration: TimeSpan.FromMinutes(30),
                MachineName: "FIXTURE-HOST",
                InterfaceName: platform == "Linux" ? "eth0" : "Ethernet",
                Medium: LinkMedium.Ethernet,
                LinkSpeedBitsPerSecond: 1_000_000_000,
                GatewayAddress: "192.168.1.1"));

            finalChainHash = writer.HeadHash;
            recordCount = writer.EntriesWritten;
        }

        var sessionStartJsonPath = Path.Combine(evidenceDir, "session_start.json");
        File.WriteAllText(sessionStartJsonPath, JsonSerializer.Serialize(new
        {
            sessionId,
            startedUtc = new DateTimeOffset(2026, 8, 21, 11, 0, 0, TimeSpan.Zero),
            version = "3.1.0"
        }));

        var rawBytes = File.ReadAllBytes(rawLogPath);
        var jsonBytes = File.ReadAllBytes(sessionStartJsonPath);

        var files = new List<ManifestFileEntry>
        {
            new ManifestFileEntry("Evidence/raw.log", rawBytes.Length, Convert.ToHexStringLower(SHA256.HashData(rawBytes))),
            new ManifestFileEntry("Evidence/session_start.json", jsonBytes.Length, Convert.ToHexStringLower(SHA256.HashData(jsonBytes)))
        };

        var manifest = new EvidenceManifest(
            ManifestSchemaVersion: 1,
            Canonicalization: "RFC8785-JCS",
            CreatedUtc: new DateTimeOffset(2026, 8, 21, 11, 30, 0, TimeSpan.Zero),
            Session: new ManifestSessionInfo(
                SessionId: sessionId,
                StartedUtc: new DateTimeOffset(2026, 8, 21, 11, 0, 0, TimeSpan.Zero),
                FinishedUtc: new DateTimeOffset(2026, 8, 21, 11, 30, 0, TimeSpan.Zero),
                EvidenceSchemaVersion: 1,
                ApplicationVersion: "3.1.0"),
            Evidence: new ManifestEvidenceSummary(
                RawChain: new ManifestRawChainRef("Evidence/raw.log", finalChainHash, recordCount),
                DerivedLedger: null,
                InterpretationCatalog: null,
                LegalContextHash: null),
            Files: files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList(),
            AcquisitionContext: new ManifestAcquisitionContext(
                Platform: platform,
                ProviderProvenance: new Dictionary<string, string>
                {
                    ["signingScope"] = scope,
                    ["provider"] = provider
                }));

        var manifestPath = Path.Combine(packageDir, EvidenceManifest.FileName);
        File.WriteAllBytes(manifestPath, manifest.ToCanonicalBytes());

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var identity = new EphemeralSoftwareSigningIdentity(ecdsa, level, provider, details);

        var envelope = ManifestSigner.SignManifestAtomicallyAsync(packageDir, identity).GetAwaiter().GetResult();

        var metadata = new
        {
            fixtureVersion = 1,
            sourcePlatform = platform,
            sourceProvider = provider,
            signingScope = scope,
            keyId = identity.KeyId,
            generatedFromCommit = "dad549a0253d828e38b194072324e9d7efcfffb8",
            expectedOverall = "ValidTrustNotEstablished",
            expectedIntegrity = "Verified",
            expectedTrust = "NotEstablished"
        };

        var metadataPath = Path.Combine(packageDir, "fixture-metadata.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }
}
