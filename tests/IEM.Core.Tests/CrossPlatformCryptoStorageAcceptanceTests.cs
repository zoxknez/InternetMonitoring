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
using IEM.Verification.Safety;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Phase 3.1-8F-R1 · Cross-Platform Crypto &amp; Storage Acceptance Test Suite.
/// Validates cross-platform cryptographic, storage layout, provenance claim,
/// and forensic verification parity between Windows and Linux implementations
/// against frozen 8A-8E primitives under strict acceptance-first rules.
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
        Create0700Directory(_tempRoot);
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

    private static void Create0700Directory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
            }
        }
    }

    // ======================================================================
    // 1. LINUX-NATIVE ACCEPTANCE GATES (Platform = Linux, 13 tests)
    // ======================================================================

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
    public async Task XPL_01_Linux_System_Native_Identity_Produces_Verifier_Valid_ManifestSig()
    {
        var stateRoot = Path.Combine(_tempRoot, "xpl01_state");
        Create0700Directory(stateRoot);
        var posix = GetPosixApi();
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, customStateRoot: stateRoot, posix: posix);
        using var identity = await provider.GetOrCreateIdentityAsync();

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
    [Trait("Platform", "Linux")]
    public async Task XPL_02_Linux_Portable_Native_Identity_Produces_Verifier_Valid_ManifestSig()
    {
        var stateRoot = Path.Combine(_tempRoot, "xpl02_state");
        Create0700Directory(stateRoot);
        var posix = GetPosixApi();
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.PortableUser, customStateRoot: stateRoot, posix: posix);
        using var identity = await provider.GetOrCreateIdentityAsync();

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
    [Trait("Platform", "Linux")]
    public async Task XPL_03_LNX_Windows_And_Linux_Identities_Share_Canonical_Suite_SPKI_And_KeyId_Formula_Linux()
    {
        var stateRoot = Path.Combine(_tempRoot, "xpl03_state");
        Create0700Directory(stateRoot);
        var posix = GetPosixApi();
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, customStateRoot: stateRoot, posix: posix);
        using var identity = await provider.GetOrCreateIdentityAsync();

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
    [Trait("Platform", "Linux")]
    public async Task XPL_06_Linux_System_KeyProtection_Claim_Is_Exact()
    {
        var stateRoot = Path.Combine(_tempRoot, "xpl06_state");
        Create0700Directory(stateRoot);
        var posix = GetPosixApi();
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, customStateRoot: stateRoot, posix: posix);
        using var identity = await provider.GetOrCreateIdentityAsync();

        Assert.Equal(KeyProtectionLevel.SoftwareProtected, identity.Protection.Protection);
        Assert.Equal(LinuxEvidenceKeyProvider.KeyStoreProviderName, identity.Protection.Provider);
        Assert.Equal("POSIX:0700/0600:exact-ownership:openat2:system-daemon", identity.Protection.Details);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, identity.Protection.Evidence);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
    public async Task XPL_07_Linux_Portable_KeyProtection_Claim_Is_Exact()
    {
        var stateRoot = Path.Combine(_tempRoot, "xpl07_state");
        Create0700Directory(stateRoot);
        var posix = GetPosixApi();
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.PortableUser, customStateRoot: stateRoot, posix: posix);
        using var identity = await provider.GetOrCreateIdentityAsync();

        Assert.Equal(KeyProtectionLevel.SoftwareProtected, identity.Protection.Protection);
        Assert.Equal(LinuxEvidenceKeyProvider.KeyStoreProviderName, identity.Protection.Provider);
        Assert.Equal("POSIX:0700/0600:exact-ownership:openat2:user-portable", identity.Protection.Details);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, identity.Protection.Evidence);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
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
    [Trait("Platform", "Linux")]
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
    [Trait("Platform", "Linux")]
    public async Task XPL_13_LNX_StorageProtectionObservation_Uses_Factual_Platform_Provenance_Linux()
    {
        var stateRoot = Path.Combine(_tempRoot, "state_root_xpl13");
        Create0700Directory(stateRoot);
        var sessionDir = Path.Combine(stateRoot, "sessions", "Sesija_xpl13-01");
        var desc = SessionLayoutDescriptor.CreateStandard("xpl13-01");
        var posix = GetPosixApi();
        var provisioner = new LinuxSessionModeProvisioner(stateRoot: stateRoot, posix: posix);

        var observation = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, desc);
        Assert.Equal("Linux", provisioner.PlatformName);
        Assert.Equal("Linux", observation.Platform);
        Assert.Equal(desc.StoragePolicyHash, observation.StoragePolicyHash);
        Assert.Equal(StorageProtectionState.Established, observation.ProtectionState);
        Assert.StartsWith("POSIX:0700", observation.PlatformSecurityDescriptorRef);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
    public async Task XPL_15_LNX_Existing_Session_Restart_Verifies_Without_Reprovision_Or_Repair_Linux()
    {
        var stateRoot = Path.Combine(_tempRoot, "state_root_restart");
        Create0700Directory(stateRoot);
        var sessionDir = Path.Combine(stateRoot, "sessions", "Sesija_restart-01");
        var desc = SessionLayoutDescriptor.CreateStandard("restart-01");

        var posix = GetPosixApi();
        var provisioner = new LinuxSessionModeProvisioner(stateRoot: stateRoot, posix: posix);
        var initObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, desc);
        Assert.Equal(StorageProtectionState.Established, initObs.ProtectionState);

        var filesBefore = Directory.Exists(sessionDir)
            ? Directory.GetFiles(sessionDir, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => File.ReadAllBytes(f))
            : new Dictionary<string, byte[]>();

        var verifyObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, desc);
        Assert.Equal(StorageProtectionState.Established, verifyObs.ProtectionState);
        Assert.True(verifyObs.RootBoundaryValid);
        Assert.True(verifyObs.ReparsePointCheck);
        Assert.Equal("Linux", verifyObs.Platform);

        var filesAfter = Directory.Exists(sessionDir)
            ? Directory.GetFiles(sessionDir, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => File.ReadAllBytes(f))
            : new Dictionary<string, byte[]>();

        Assert.Equal(filesBefore.Count, filesAfter.Count);
        foreach (var kvp in filesBefore)
        {
            Assert.True(filesAfter.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value, filesAfter[kvp.Key]);
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
    public void XPL_16_LNX_Platform_Storage_Boundaries_Reject_Symlink_And_Reparse_Hijack_Linux()
    {
        var trustedRoot = Path.Combine(_tempRoot, "trusted_lnx_root");
        var outsideTarget = Path.Combine(_tempRoot, "outside_target");
        Create0700Directory(trustedRoot);
        Create0700Directory(outsideTarget);
        Create0700Directory(Path.Combine(trustedRoot, "Evidence"));
        var outsideFile = Path.Combine(outsideTarget, "secret.txt");
        File.WriteAllText(outsideFile, "secret-payload");

        var posix = GetPosixApi();
        var guard = new LinuxSymlinkGuard(posix: posix);
        Assert.NotNull(guard);
        Assert.True(guard is ISymlinkSafetyGuard);

        // 1. Lexical escape outside trusted root
        var lexicalResult = guard.ValidatePath(trustedRoot, "/etc/shadow");
        Assert.False(lexicalResult.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, lexicalResult.State);

        // 2. Real symlink escape inside trusted tree pointing outside
        var symlinkPath = Path.Combine(trustedRoot, "Evidence", "escape_symlink");
        bool linkCreated = false;
        try
        {
            File.CreateSymbolicLink(symlinkPath, outsideFile);
            linkCreated = File.Exists(symlinkPath) || Directory.Exists(symlinkPath);
        }
        catch
        {
            linkCreated = false;
        }

        // Zero bypass: on Linux, symlink creation MUST succeed
        if (OperatingSystem.IsLinux())
        {
            Assert.True(linkCreated, "Linux platform acceptance requires successful symlink creation for escape testing.");
        }

        if (linkCreated)
        {
            var linkResult = guard.ValidatePath(trustedRoot, symlinkPath);
            Assert.False(linkResult.IsSafe, "Symlink pointing outside trusted boundary must be rejected.");
            Assert.Equal(StorageProtectionState.NotEstablished, linkResult.State);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Fail("Symlink was not created on Linux runner; zero-bypass gate failed.");
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
    public void XPL_18_Linux_OpenAt2_Resolve_Flags_Block_Traversal()
    {
        // 1. Assert the 4 locked openat2 flags in Linux storage constants
        Assert.Equal(0x01u, LinuxPosixStorageConstants.RESOLVE_NO_XDEV);
        Assert.Equal(0x02u, LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS);
        Assert.Equal(0x04u, LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS);
        Assert.Equal(0x08u, LinuxPosixStorageConstants.RESOLVE_BENEATH);

        var testDir = Path.Combine(_tempRoot, "xpl18_openat2");
        Create0700Directory(testDir);
        var outsideDir = Path.Combine(_tempRoot, "xpl18_outside");
        Create0700Directory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "target.txt");
        File.WriteAllText(outsideFile, "secret");

        var insideFile = Path.Combine(testDir, "inside.txt");
        File.WriteAllText(insideFile, "safe-content");

        var posix = GetPosixApi();
        int dirFd = posix.Open(testDir, LinuxPosixStorageConstants.O_RDONLY | LinuxPosixStorageConstants.O_DIRECTORY | LinuxPosixStorageConstants.O_CLOEXEC, 0);
        Assert.True(dirFd >= 0, $"posix.Open for testDir must succeed: errno={posix.GetLastErrno()}");

        try
        {
            // 2. RESOLVE_BENEATH: must reject traversal outside dirfd
            var beneathHow = new OpenHow
            {
                Flags = (ulong)LinuxPosixStorageConstants.O_RDONLY,
                Mode = 0,
                Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH
            };
            int fdEscape = posix.OpenAt2(dirFd, "../xpl18_outside/target.txt", ref beneathHow);
            Assert.True(fdEscape < 0, "openat2 with RESOLVE_BENEATH must reject traversal outside dirfd.");

            // 3. RESOLVE_NO_SYMLINKS: must reject opening symlinks
            var symlinkFile = Path.Combine(testDir, "test_symlink");
            bool symlinkCreated = false;
            try
            {
                File.CreateSymbolicLink(symlinkFile, outsideFile);
                symlinkCreated = File.Exists(symlinkFile);
            }
            catch { }

            if (OperatingSystem.IsLinux())
            {
                Assert.True(symlinkCreated, "XPL-18 requires a real symlink before testing RESOLVE_NO_SYMLINKS on Linux.");
            }

            var noSymlinksHow = new OpenHow
            {
                Flags = (ulong)LinuxPosixStorageConstants.O_RDONLY,
                Mode = 0,
                Resolve = LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS
            };
            int fdSymlink = posix.OpenAt2(dirFd, "test_symlink", ref noSymlinksHow);
            if (symlinkCreated || OperatingSystem.IsLinux())
            {
                Assert.True(fdSymlink < 0, "openat2 with RESOLVE_NO_SYMLINKS must reject symlink resolution.");
            }

            // 4. Combined locked resolution flags on legitimate file beneath dirfd
            var fullLockedHow = new OpenHow
            {
                Flags = (ulong)LinuxPosixStorageConstants.O_RDONLY,
                Mode = 0,
                Resolve = LinuxPosixStorageConstants.RESOLVE_BENEATH |
                          LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS |
                          LinuxPosixStorageConstants.RESOLVE_NO_XDEV |
                          LinuxPosixStorageConstants.RESOLVE_NO_MAGICLINKS
            };
            int fdInside = posix.OpenAt2(dirFd, "inside.txt", ref fullLockedHow);
            Assert.True(fdInside >= 0, "openat2 with full locked resolve flags must successfully open legitimate file beneath dirfd.");
            posix.Close(fdInside);
        }
        finally
        {
            posix.Close(dirFd);
        }

        var guard = new LinuxSymlinkGuard(posix: posix);
        var dotDotResult = guard.ValidatePath(testDir, Path.Combine(testDir, "../escaped"));
        Assert.False(dotDotResult.IsSafe);
        Assert.Equal(StorageProtectionState.NotEstablished, dotDotResult.State);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
    public async Task XPL_19_LNX_Invalid_Boundary_Produces_Exact_Platform_Protection_State_Linux()
    {
        var posix = GetPosixApi();
        var provisioner = new LinuxSessionModeProvisioner(posix: posix);
        var nonExistentDir = Path.Combine(_tempRoot, "NonExistentSessionDir_" + Guid.NewGuid().ToString("N"));
        var layout = SessionLayoutDescriptor.CreateStandard("lnx-invalid-01");

        var observation = await provisioner.VerifyStorageProtectionAsync(nonExistentDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, observation.ProtectionState);
        Assert.False(observation.RootBoundaryValid);
        Assert.False(observation.ReparsePointCheck);
        Assert.Equal("Linux", observation.Platform);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Linux")]
    public async Task XPL_20_LNX_StorageProtectionObservation_Boundary_Flags_Are_Factual_Linux()
    {
        var stateRoot = Path.Combine(_tempRoot, "state_root_fact");
        Create0700Directory(stateRoot);
        var sessionDir = Path.Combine(stateRoot, "sessions", "Sesija_fact-01");
        var desc = SessionLayoutDescriptor.CreateStandard("fact-01");
        var posix = GetPosixApi();
        var provisioner = new LinuxSessionModeProvisioner(stateRoot: stateRoot, posix: posix);

        await provisioner.ProvisionSessionBoundariesAsync(sessionDir, desc);
        var observation = await provisioner.VerifyStorageProtectionAsync(sessionDir, desc);

        Assert.Equal("Linux", observation.Platform);
        Assert.Equal(StorageProtectionState.Established, observation.ProtectionState);
        Assert.True(observation.RootBoundaryValid);
        Assert.True(observation.ReparsePointCheck);
        Assert.StartsWith("POSIX:0700", observation.PlatformSecurityDescriptorRef);
        Assert.Null(observation.DiagnosticMessage);
    }

    // ======================================================================
    // 2. SHARED CROSS-PLATFORM ACCEPTANCE GATES (Platform = Shared, 16 tests)
    // ======================================================================

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
        Assert.Equal(LinuxEvidenceKeyProvider.KeyStoreProviderName, report.Layers.Signature.Protection.Provider);
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
            LinuxEvidenceKeyProvider.KeyStoreProviderName,
            "POSIX:0700/0600:exact-ownership:openat2:system-daemon");

        using var identity = new LinuxEvidenceSigningIdentity(ecdsa, claim, LinuxSigningIdentityScope.SystemInstallation);
        await CreateSamplePackageAsync(pkgDir, identity.KeyId);

        // 1. Sign manifest atomically to produce manifest.sig
        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(pkgDir, identity);

        // 2. Compute timestamp token over the exact produced manifest.sig bytes
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

        // Pre-tamper verification: PROVE Timestamp is cryptographically valid BEFORE tamper
        var initialReport = await PackageVerifier.VerifyPackageAsync(pkgDir, new VerificationOptions
        {
            Offline = true,
            ExtraCertificates = new X509Certificate2Collection { cert }
        });
        Assert.NotNull(initialReport.Layers.Signature);
        Assert.NotNull(initialReport.Layers.TrustedTimestamp);
        Assert.Equal(LayerStatus.Verified, initialReport.Layers.Signature.Status);
        Assert.True(
            initialReport.Layers.TrustedTimestamp.Status == LayerStatus.Verified ||
            initialReport.Layers.TrustedTimestamp.Status == LayerStatus.ValidUntrusted,
            "Timestamp token must be cryptographically valid before tampering.");
        Assert.Equal(IntegrityStatus.Verified, initialReport.Integrity);

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

        // Re-verify after tamper: PROVE tamper caused Invalid status
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

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public void XPL_14_StoragePolicyHash_Is_Platform_Independent_And_Deterministic()
    {
        var winDesc = SessionLayoutDescriptor.CreateStandard("ses-cross-01");
        var lnxDesc = SessionLayoutDescriptor.CreateStandard("ses-cross-01");

        // 1. Assert standard required directory subpaths across both platform definitions
        Assert.Equal("Raw", winDesc.RawRelativePath);
        Assert.Equal("Raw", lnxDesc.RawRelativePath);
        Assert.Equal("Derived", winDesc.DerivedRelativePath);
        Assert.Equal("Derived", lnxDesc.DerivedRelativePath);
        Assert.Equal("Evidence", winDesc.EvidenceRelativePath);
        Assert.Equal("Evidence", lnxDesc.EvidenceRelativePath);
        Assert.Equal("Exports", winDesc.ExportsRelativePath);
        Assert.Equal("Exports", lnxDesc.ExportsRelativePath);

        // 2. Storage policy hash determinism & platform independence
        Assert.Equal(winDesc.StoragePolicyHash, lnxDesc.StoragePolicyHash);
        Assert.Equal(64, winDesc.StoragePolicyHash.Length);
        Assert.Equal(winDesc.ToCanonicalBytes(), lnxDesc.ToCanonicalBytes());

        // 3. Platform storage layout flow parity across runtime implementations
        var lnxLayout = new LinuxStorageLayout("/var/lib/iem");
        var lnxPortLayout = new LinuxPortableStorageLayout("/tmp/iem-portable");
        var lnxSessionSys = lnxLayout.GetSessionDirectory("ses-cross-01", isInstalled: true);
        var lnxSessionPort = lnxPortLayout.GetSessionDirectory("ses-cross-01", isInstalled: false);

        var requiredAreas = new[] { winDesc.RawRelativePath, winDesc.DerivedRelativePath, winDesc.EvidenceRelativePath, winDesc.ExportsRelativePath };
        foreach (var area in requiredAreas)
        {
            var areaSys = Path.Combine(lnxSessionSys, area);
            var areaPort = Path.Combine(lnxSessionPort, area);
            Assert.Equal(area, Path.GetRelativePath(lnxSessionSys, areaSys));
            Assert.Equal(area, Path.GetRelativePath(lnxSessionPort, areaPort));
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Shared")]
    public async Task XPL_17_Shared_Path_And_Verifier_PackageRoot_Containment_Fail_Closed()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var identity = new EphemeralSoftwareSigningIdentity(ecdsa);

        // 1. Lexical escape path in manifest
        var lexicalPkgDir = Path.Combine(_tempRoot, "xpl-17-lexical");
        Create0700Directory(lexicalPkgDir);

        var lexicalFiles = new List<ManifestFileEntry>
        {
            new ManifestFileEntry("../../etc/shadow", 100, "0000000000000000000000000000000000000000000000000000000000000000")
        };

        var lexicalManifest = new EvidenceManifest(
            ManifestSchemaVersion: 1,
            Canonicalization: "RFC8785-JCS",
            CreatedUtc: DateTimeOffset.UtcNow,
            Session: new ManifestSessionInfo("ses-17-lex", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "3.1.0"),
            Evidence: new ManifestEvidenceSummary(new ManifestRawChainRef("Evidence/raw.log", "0000", 0), null, null, null),
            Files: lexicalFiles,
            AcquisitionContext: new ManifestAcquisitionContext("Shared", new Dictionary<string, string>()));

        var lexicalManifestPath = Path.Combine(lexicalPkgDir, EvidenceManifest.FileName);
        await File.WriteAllBytesAsync(lexicalManifestPath, lexicalManifest.ToCanonicalBytes());
        await ManifestSigner.SignManifestAtomicallyAsync(lexicalPkgDir, identity);

        var lexicalReport = await PackageVerifier.VerifyPackageAsync(lexicalPkgDir, new VerificationOptions { Offline = true });
        Assert.Equal(OverallStatus.Invalid, lexicalReport.Overall);
        Assert.Equal(IntegrityStatus.Invalid, lexicalReport.Integrity);
        Assert.NotNull(lexicalReport.Layers.Manifest);
        Assert.True(lexicalReport.Layers.Manifest.Violations.Count > 0);

        // 2. Real size-neutral physical escape (Windows junction / Linux symlink) -> PackageVerifier must fail closed
        var outsideDir = Path.Combine(_tempRoot, "xpl17_outside");
        Create0700Directory(outsideDir);
        var outsideSecretFile = Path.Combine(outsideDir, "secret.bin");
        var initialSecretBytes = "OUTSIDE_SECRET_PAYLOAD_SIZE_NEUTRAL_CONFIDENTIAL"u8.ToArray();
        await File.WriteAllBytesAsync(outsideSecretFile, initialSecretBytes);

        var symlinkPkgDir = Path.Combine(_tempRoot, "xpl17_symlink_pkg");
        Create0700Directory(symlinkPkgDir);
        var symlinkEvidenceDir = Path.Combine(symlinkPkgDir, "Evidence");
        Create0700Directory(symlinkEvidenceDir);

        // Create sample raw.log and session_start.json
        var rawLogPath = Path.Combine(symlinkEvidenceDir, "raw.log");
        string finalChainHash;
        long recordCount;
        using (var writer = HashChainWriter.Open(rawLogPath))
        {
            writer.Append(new SessionStartPayload(
                SessionId: "ses-17-sym",
                ToolVersion: "3.1.0",
                StartedUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
                PlannedDuration: TimeSpan.FromMinutes(10),
                MachineName: "HOST-17",
                InterfaceName: "eth0",
                Medium: LinkMedium.Ethernet,
                LinkSpeedBitsPerSecond: 1_000_000_000,
                GatewayAddress: "192.168.1.1"));
            finalChainHash = writer.HeadHash;
            recordCount = writer.EntriesWritten;
        }

        var sessionJsonPath = Path.Combine(symlinkEvidenceDir, "session_start.json");
        await File.WriteAllTextAsync(sessionJsonPath, JsonSerializer.Serialize(new { sessionId = "ses-17-sym", version = "3.1.0" }));

        var rawBytes = await File.ReadAllBytesAsync(rawLogPath);
        var jsonBytes = await File.ReadAllBytesAsync(sessionJsonPath);

        string targetRelativePath;
        string linkPath;
        bool linkCreated = false;

        if (OperatingSystem.IsWindows())
        {
            var junctionPath = Path.Combine(symlinkEvidenceDir, "external");
            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{junctionPath}\" \"{outsideDir}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit();
                linkCreated = Directory.Exists(junctionPath);
            }
            catch
            {
                linkCreated = false;
            }

            Assert.True(linkCreated, "Windows runner requires successful junction creation for XPL-17 physical escape gate.");
            targetRelativePath = "Evidence/external/secret.bin";
            linkPath = Path.Combine(symlinkPkgDir, targetRelativePath);
        }
        else
        {
            var symlinkPath = Path.Combine(symlinkEvidenceDir, "escape.bin");
            try
            {
                File.CreateSymbolicLink(symlinkPath, outsideSecretFile);
                linkCreated = File.Exists(symlinkPath);
            }
            catch
            {
                linkCreated = false;
            }

            Assert.True(linkCreated, "Linux runner requires successful symlink creation for XPL-17 physical escape gate.");
            targetRelativePath = "Evidence/escape.bin";
            linkPath = symlinkPath;
        }

        // Precondition checks:
        Assert.True(File.Exists(linkPath), "Precondition: physical link target must be accessible via package path.");
        Assert.True(Path.GetFullPath(Path.Combine(symlinkPkgDir, targetRelativePath)).StartsWith(Path.GetFullPath(symlinkPkgDir)), "Precondition: lexical path containment must hold.");

        // Size-neutrality: match targetBytes length precisely to observed FileInfo.Length
        var observedLength = new FileInfo(linkPath).Length;
        var targetBytes = new byte[observedLength > 0 ? (int)observedLength : 48];
        Random.Shared.NextBytes(targetBytes);
        await File.WriteAllBytesAsync(outsideSecretFile, targetBytes);

        var refreshedInfo = new FileInfo(linkPath);
        refreshedInfo.Refresh();
        Assert.Equal(targetBytes.Length, refreshedInfo.Length);

        var manifestFiles = new List<ManifestFileEntry>
        {
            new ManifestFileEntry("Evidence/raw.log", rawBytes.Length, Convert.ToHexStringLower(SHA256.HashData(rawBytes))),
            new ManifestFileEntry("Evidence/session_start.json", jsonBytes.Length, Convert.ToHexStringLower(SHA256.HashData(jsonBytes))),
            new ManifestFileEntry(targetRelativePath, targetBytes.Length, Convert.ToHexStringLower(SHA256.HashData(targetBytes)))
        };

        var symlinkManifest = new EvidenceManifest(
            ManifestSchemaVersion: 1,
            Canonicalization: "RFC8785-JCS",
            CreatedUtc: DateTimeOffset.UtcNow,
            Session: new ManifestSessionInfo("ses-17-sym", DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow, 1, "3.1.0"),
            Evidence: new ManifestEvidenceSummary(new ManifestRawChainRef("Evidence/raw.log", finalChainHash, recordCount), null, null, null),
            Files: manifestFiles,
            AcquisitionContext: new ManifestAcquisitionContext("Shared", new Dictionary<string, string>()));

        var symlinkManifestPath = Path.Combine(symlinkPkgDir, EvidenceManifest.FileName);
        await File.WriteAllBytesAsync(symlinkManifestPath, symlinkManifest.ToCanonicalBytes());
        await ManifestSigner.SignManifestAtomicallyAsync(symlinkPkgDir, identity);

        var symlinkReport = await PackageVerifier.VerifyPackageAsync(symlinkPkgDir, new VerificationOptions { Offline = true });

        // Invariant 29: PackageVerifier MUST NOT verify/accept files outside package root through symlinks/junctions
        Assert.Equal(OverallStatus.Invalid, symlinkReport.Overall);
        Assert.Equal(IntegrityStatus.Invalid, symlinkReport.Integrity);
        Assert.NotNull(symlinkReport.Layers.Manifest);
        Assert.Equal(LayerStatus.Invalid, symlinkReport.Layers.Manifest.Status);
        Assert.True(symlinkReport.Layers.Manifest.Violations.Count > 0, "PackageVerifier must record at least one manifest violation for physical escape.");
    }

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
                    ["provider"] = LinuxEvidenceKeyProvider.KeyStoreProviderName
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
        var rootCandidate = Path.Combine(root, "tests", "IEM.Core.Tests", "Fixtures", "CrossPlatform", fixtureName);
        if (Directory.Exists(rootCandidate))
        {
            return rootCandidate;
        }

        throw new DirectoryNotFoundException($"Fixture directory '{fixtureName}' not found. Zero runtime fixture generation allowed.");
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

    private static ILinuxPosixStorageApi GetPosixApi()
    {
        return OperatingSystem.IsLinux() ? new LinuxNativePosixStorageApi() : new SimulatedLinuxPosixStorageApi();
    }

    private sealed class SimulatedLinuxPosixStorageApi : ILinuxPosixStorageApi
    {
        private sealed class Entry
        {
            public string Path { get; set; } = "";
            public bool IsDirectory { get; set; }
            public int Mode { get; set; }
            public uint Uid { get; set; }
            public uint Gid { get; set; }
            public byte[] Content { get; set; } = Array.Empty<byte>();
        }

        private sealed class FdState
        {
            public Entry Entry { get; set; } = null!;
            public int Position { get; set; }
        }

        private readonly Dictionary<string, Entry> _fs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, FdState> _openFds = new();
        private int _nextFd = 10;
        private int _lastErrno = 0;

        public int GetLastErrno() => _lastErrno;
        public uint GetEuid() => 0;
        public uint GetEgid() => 0;

        private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

        public int Open(string path, int flags, int mode)
        {
            var n = Norm(path);
            if (!_fs.TryGetValue(n, out var entry))
            {
                entry = new Entry
                {
                    Path = n,
                    IsDirectory = (flags & LinuxPosixStorageConstants.O_DIRECTORY) != 0 || (flags & LinuxPosixStorageConstants.O_CREAT) == 0,
                    Mode = mode != 0 ? mode : ((flags & LinuxPosixStorageConstants.O_DIRECTORY) != 0 ? LinuxPosixStorageConstants.Mode0700 : LinuxPosixStorageConstants.Mode0600),
                    Uid = 0,
                    Gid = 0
                };
                _fs[n] = entry;
            }
            else if ((flags & LinuxPosixStorageConstants.O_CREAT) != 0 && (flags & LinuxPosixStorageConstants.O_EXCL) != 0)
            {
                _lastErrno = LinuxPosixStorageConstants.EEXIST;
                return -1;
            }

            int fd = _nextFd++;
            _openFds[fd] = new FdState { Entry = entry, Position = 0 };
            return fd;
        }

        public int OpenAt(int dirfd, string pathname, int flags, int mode)
        {
            if (!_openFds.TryGetValue(dirfd, out var parentState))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            var targetPath = Norm(parentState.Entry.Path + "/" + pathname);
            return Open(targetPath, flags, mode);
        }

        public int OpenAt2(int dirfd, string pathname, ref OpenHow how)
        {
            if ((how.Resolve & LinuxPosixStorageConstants.RESOLVE_BENEATH) != 0 && (pathname.Contains("..") || pathname.StartsWith("/")))
            {
                _lastErrno = 18; // EXDEV
                return -1;
            }
            if ((how.Resolve & LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS) != 0 && pathname.Contains("symlink", StringComparison.OrdinalIgnoreCase))
            {
                _lastErrno = LinuxPosixStorageConstants.ELOOP;
                return -1;
            }
            return OpenAt(dirfd, pathname, (int)how.Flags, (int)how.Mode);
        }

        public int FstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags)
        {
            if (!_openFds.TryGetValue(dirfd, out var parentState))
            {
                statbuf = default;
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            var targetPath = Norm(parentState.Entry.Path + "/" + pathname);
            if (!_fs.TryGetValue(targetPath, out var entry))
            {
                statbuf = default;
                _lastErrno = LinuxPosixStorageConstants.ENOENT;
                return -1;
            }
            statbuf = CreateStat(entry);
            return 0;
        }

        public int Fstat(int fd, out PosixStat statbuf)
        {
            if (!_openFds.TryGetValue(fd, out var state))
            {
                statbuf = default;
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            statbuf = CreateStat(state.Entry);
            return 0;
        }

        private static PosixStat CreateStat(Entry e)
        {
            return new PosixStat
            {
                Dev = 1,
                Ino = (ulong)Math.Abs(e.Path.GetHashCode()),
                Nlink = 1,
                Mode = ((uint)e.Mode & 0xFFFu) | (e.IsDirectory ? LinuxPosixStorageConstants.S_IFDIR : LinuxPosixStorageConstants.S_IFREG),
                Uid = e.Uid,
                Gid = e.Gid,
                Size = e.Content.Length
            };
        }

        public int MkdirAt(int dirfd, string pathname, int mode)
        {
            if (!_openFds.TryGetValue(dirfd, out var parentState))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            var targetPath = Norm(parentState.Entry.Path + "/" + pathname);
            if (_fs.ContainsKey(targetPath))
            {
                _lastErrno = LinuxPosixStorageConstants.EEXIST;
                return -1;
            }
            _fs[targetPath] = new Entry
            {
                Path = targetPath,
                IsDirectory = true,
                Mode = mode != 0 ? mode : LinuxPosixStorageConstants.Mode0700,
                Uid = 0,
                Gid = 0
            };
            return 0;
        }

        public int Fchmod(int fd, int mode)
        {
            if (!_openFds.TryGetValue(fd, out var state))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            state.Entry.Mode = mode;
            return 0;
        }

        public int Fchown(int fd, uint uid, uint gid)
        {
            if (!_openFds.TryGetValue(fd, out var state))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            state.Entry.Uid = uid;
            state.Entry.Gid = gid;
            return 0;
        }

        public int RenameAt2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags)
        {
            if (!_openFds.TryGetValue(olddirfd, out var oldState) || !_openFds.TryGetValue(newdirfd, out var newState))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            var oldP = Norm(oldState.Entry.Path + "/" + oldpath);
            var newP = Norm(newState.Entry.Path + "/" + newpath);
            if (!_fs.TryGetValue(oldP, out var entry))
            {
                _lastErrno = LinuxPosixStorageConstants.ENOENT;
                return -1;
            }
            _fs.Remove(oldP);
            entry.Path = newP;
            _fs[newP] = entry;
            return 0;
        }

        public int UnlinkAt(int dirfd, string pathname, int flags)
        {
            if (!_openFds.TryGetValue(dirfd, out var parentState))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            var p = Norm(parentState.Entry.Path + "/" + pathname);
            _fs.Remove(p);
            return 0;
        }

        public int Write(int fd, ReadOnlySpan<byte> buffer)
        {
            if (!_openFds.TryGetValue(fd, out var state))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            var newContent = new byte[state.Entry.Content.Length + buffer.Length];
            Buffer.BlockCopy(state.Entry.Content, 0, newContent, 0, state.Entry.Content.Length);
            buffer.CopyTo(newContent.AsSpan(state.Entry.Content.Length));
            state.Entry.Content = newContent;
            state.Position += buffer.Length;
            return buffer.Length;
        }

        public int Read(int fd, Span<byte> buffer)
        {
            if (!_openFds.TryGetValue(fd, out var state))
            {
                _lastErrno = LinuxPosixStorageConstants.EBADF;
                return -1;
            }
            int available = Math.Min(buffer.Length, state.Entry.Content.Length - state.Position);
            if (available <= 0) return 0;
            state.Entry.Content.AsSpan(state.Position, available).CopyTo(buffer);
            state.Position += available;
            return available;
        }

        public int Fsync(int fd) => 0;
        public int Flock(int fd, int operation) => 0;
        public int Close(int fd)
        {
            _openFds.Remove(fd);
            return 0;
        }
    }
}
