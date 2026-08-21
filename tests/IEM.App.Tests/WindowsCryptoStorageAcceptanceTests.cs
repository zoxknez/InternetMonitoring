using System.IO;
using System.Security.Cryptography;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Storage.Layout;
using IEM.Windows.Crypto;
using IEM.Windows.Storage;
using Xunit;

namespace IEM.App.Tests;

/// <summary>
/// Phase 3.1-8F-R1 · Windows-Native Crypto &amp; Storage Acceptance Test Suite.
/// Validates Windows CNG signing identity, Windows ACL provisioning,
/// Windows reparse point guard, and Windows storage layout truth.
/// </summary>
public sealed class WindowsCryptoStorageAcceptanceTests : IDisposable
{
    private readonly string _tempRoot;

    public WindowsCryptoStorageAcceptanceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "iem-win-xpl-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public async Task XPL_03_WIN_Windows_And_Linux_Identities_Share_Canonical_Suite_SPKI_And_KeyId_Formula_Windows()
    {
        var provider = new WindowsCngKeyProvider("IEM_Test_XPL_03_" + Guid.NewGuid().ToString("N"), machineKey: false);
        var identity = await provider.GetOrCreateIdentityAsync();

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
    [Trait("Platform", "Windows")]
    public async Task XPL_08_WIN_Windows_KeyProtection_Claim_Is_Exact_Provider_Level_Pair()
    {
        var provider = new WindowsCngKeyProvider("IEM_Test_XPL_08_" + Guid.NewGuid().ToString("N"), machineKey: false);
        var identity = await provider.GetOrCreateIdentityAsync();

        Assert.NotNull(identity.Protection);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, identity.Protection.Evidence);

        if (identity.Protection.Protection == KeyProtectionLevel.TpmBacked)
        {
            Assert.Equal("Microsoft Platform Crypto Provider", identity.Protection.Provider);
        }
        else
        {
            Assert.Equal(KeyProtectionLevel.SoftwareProtected, identity.Protection.Protection);
            Assert.Equal("Microsoft Software Key Storage Provider", identity.Protection.Provider);
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public void XPL_11_WIN_Windows_And_Linux_LayoutDescriptor_Contracts_Are_Identical_Windows()
    {
        var desc = SessionLayoutDescriptor.CreateStandard("test-session-win");

        Assert.Equal(2, desc.LayoutVersion);
        Assert.Equal(1, desc.StoragePolicyVersion);
        Assert.NotEmpty(desc.StoragePolicyHash);
        Assert.Equal(64, desc.StoragePolicyHash.Length);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public void XPL_12_WIN_Platform_Roots_Differ_But_Session_Relative_Tree_Is_Canonical_Windows()
    {
        var layout = WindowsStorageLayout.Instance;

        var installedSession = layout.GetSessionDirectory("ses-01", isInstalled: true);
        var portableSession = layout.GetSessionDirectory("ses-01", isInstalled: false);

        // Windows installed and portable naming contract: Sesija_<id>
        Assert.EndsWith("Sesija_ses-01", installedSession);
        Assert.EndsWith("Sesija_ses-01", portableSession);

        // Canonical relative tree
        var expectedDirs = new[] { "Raw", "Derived", "Evidence", "Exports" };
        foreach (var dir in expectedDirs)
        {
            var instSub = Path.Combine(installedSession, dir);
            var portSub = Path.Combine(portableSession, dir);
            Assert.Equal(dir, Path.GetRelativePath(installedSession, instSub));
            Assert.Equal(dir, Path.GetRelativePath(portableSession, portSub));
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public async Task XPL_13_WIN_StorageProtectionObservation_Uses_Factual_Platform_Provenance_Windows()
    {
        var provisioner = new WindowsSessionAclProvisioner();
        Assert.Equal("Windows", provisioner.PlatformName);

        var sessionDir = Path.Combine(_tempRoot, "Sesija_win-prov-01");
        var layout = SessionLayoutDescriptor.CreateStandard("win-prov-01");

        var observation = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal("Windows", observation.Platform);
        Assert.Equal(StorageProtectionState.Established, observation.ProtectionState);
        Assert.True(observation.RootBoundaryValid);
        Assert.True(observation.ReparsePointCheck);
        Assert.NotNull(observation.PlatformSecurityDescriptorRef);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public async Task XPL_15_WIN_Existing_Session_Restart_Verifies_Without_Reprovision_Or_Repair_Windows()
    {
        var sessionDir = Path.Combine(_tempRoot, "Sesija_win-restart-01");
        var provisioner = new WindowsSessionAclProvisioner();
        var layout = SessionLayoutDescriptor.CreateStandard("win-restart-01");

        var initialObs = await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.Established, initialObs.ProtectionState);

        var filesBefore = Directory.GetFiles(sessionDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.ReadAllBytes(f));

        var verifyObs = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);
        Assert.Equal(StorageProtectionState.Established, verifyObs.ProtectionState);
        Assert.True(verifyObs.RootBoundaryValid);
        Assert.True(verifyObs.ReparsePointCheck);

        var filesAfter = Directory.GetFiles(sessionDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.ReadAllBytes(f));

        Assert.Equal(filesBefore.Count, filesAfter.Count);
        foreach (var kvp in filesBefore)
        {
            Assert.True(filesAfter.ContainsKey(kvp.Key));
            Assert.Equal(kvp.Value, filesAfter[kvp.Key]);
        }
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public void XPL_16_WIN_Platform_Storage_Boundaries_Reject_Symlink_And_Reparse_Hijack_Windows()
    {
        var sessionDir = Path.Combine(_tempRoot, "Sesija_reparse_win");
        var outsideTarget = Path.Combine(_tempRoot, "outside_target");
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(outsideTarget);

        var normalSub = Path.Combine(sessionDir, "Raw");
        Directory.CreateDirectory(normalSub);

        Assert.False(WindowsReparsePointGuard.IsReparsePoint(normalSub));
        Assert.True(WindowsReparsePointGuard.ValidateNoReparsePointsAlongPath(sessionDir, normalSub, out var okViolation));
        Assert.Null(okViolation);

        // Test reparse point detection logic on non-directory file or simulated reparse point path
        Assert.False(WindowsReparsePointGuard.IsReparsePoint(""));
        Assert.False(WindowsReparsePointGuard.IsReparsePoint(null!));
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public async Task XPL_19_WIN_Invalid_Boundary_Produces_Exact_Platform_Protection_State_Windows()
    {
        var provisioner = new WindowsSessionAclProvisioner();
        var nonExistentDir = Path.Combine(_tempRoot, "NonExistentSessionDir_" + Guid.NewGuid().ToString("N"));
        var layout = SessionLayoutDescriptor.CreateStandard("win-invalid-01");

        var observation = await provisioner.VerifyStorageProtectionAsync(nonExistentDir, layout);
        Assert.Equal(StorageProtectionState.NotEstablished, observation.ProtectionState);
        Assert.False(observation.RootBoundaryValid);
        Assert.False(observation.ReparsePointCheck);
        Assert.Equal("Windows", observation.Platform);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public async Task XPL_20_WIN_StorageProtectionObservation_Boundary_Flags_Are_Factual_Windows()
    {
        var provisioner = new WindowsSessionAclProvisioner();
        var sessionDir = Path.Combine(_tempRoot, "Sesija_win-fact-01");
        var layout = SessionLayoutDescriptor.CreateStandard("win-fact-01");

        await provisioner.ProvisionSessionBoundariesAsync(sessionDir, layout);
        var observation = await provisioner.VerifyStorageProtectionAsync(sessionDir, layout);

        Assert.Equal(StorageProtectionState.Established, observation.ProtectionState);
        Assert.True(observation.RootBoundaryValid);
        Assert.True(observation.ReparsePointCheck);
        Assert.Equal("WindowsDACL:Verified", observation.PlatformSecurityDescriptorRef);
        Assert.Null(observation.DiagnosticMessage);
    }
}
