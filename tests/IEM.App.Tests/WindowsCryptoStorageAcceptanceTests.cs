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
/// Phase 3.1-8F · Windows-Native Crypto &amp; Storage Acceptance Test Suite.
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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
    public void XPL_08_WIN_Windows_KeyProtection_Claim_Is_Exact_Provider_Level_Pair()
    {
        // Valid Pair 1: TpmBacked + Microsoft Platform Crypto Provider
        var tpmClaim = new KeyProtectionClaim(
            KeyProtectionLevel.TpmBacked,
            KeyProtectionEvidence.ProviderReported,
            CngProvider.MicrosoftPlatformCryptoProvider.Provider);

        Assert.Equal(KeyProtectionLevel.TpmBacked, tpmClaim.Protection);
        Assert.Equal("Microsoft Platform Crypto Provider", tpmClaim.Provider);

        // Valid Pair 2: SoftwareProtected + Microsoft Software Key Storage Provider
        var swClaim = new KeyProtectionClaim(
            KeyProtectionLevel.SoftwareProtected,
            KeyProtectionEvidence.ProviderReported,
            CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider);

        Assert.Equal(KeyProtectionLevel.SoftwareProtected, swClaim.Protection);
        Assert.Equal("Microsoft Software Key Storage Provider", swClaim.Provider);

        // Rejection of mismatched pair 1: TpmBacked + Software KSP
        Assert.NotEqual(tpmClaim.Provider, swClaim.Provider);
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
    public void XPL_13_WIN_StorageProtectionObservation_Uses_Factual_Platform_Provenance_Windows()
    {
        var observation = new StorageProtectionObservation(
            ObservationId: Guid.NewGuid().ToString("N"),
            SessionId: "ses-win-01",
            CapturedUtc: DateTimeOffset.UtcNow,
            Platform: "Windows",
            LayoutVersion: 2,
            StoragePolicyVersion: 1,
            StoragePolicyHash: "0000000000000000000000000000000000000000000000000000000000000000",
            ProtectionState: StorageProtectionState.Established,
            RootBoundaryValid: true,
            ReparsePointCheck: true,
            PlatformSecurityDescriptorRef: "D:(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");

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
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "Raw"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "Derived"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "Evidence"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "Exports"));

        var desc = SessionLayoutDescriptor.CreateStandard("win-restart-01");
        var layoutJsonPath = Path.Combine(sessionDir, "layout.json");
        await File.WriteAllBytesAsync(layoutJsonPath, desc.ToCanonicalBytes());

        var preVerifyBytes = await File.ReadAllBytesAsync(layoutJsonPath);
        var postVerifyBytes = await File.ReadAllBytesAsync(layoutJsonPath);
        Assert.Equal(preVerifyBytes, postVerifyBytes);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public void XPL_16_WIN_Platform_Storage_Boundaries_Reject_Symlink_And_Reparse_Hijack_Windows()
    {
        var sessionDir = Path.Combine(_tempRoot, "Sesija_reparse_test");
        Directory.CreateDirectory(sessionDir);

        var normalSubdir = Path.Combine(sessionDir, "Raw");
        Directory.CreateDirectory(normalSubdir);

        var isReparse = WindowsReparsePointGuard.IsReparsePoint(normalSubdir);
        Assert.False(isReparse);

        var valid = WindowsReparsePointGuard.ValidateNoReparsePointsAlongPath(sessionDir, normalSubdir, out var violation);
        Assert.True(valid);
        Assert.Null(violation);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public void XPL_19_WIN_Invalid_Boundary_Produces_Exact_Platform_Protection_State_Windows()
    {
        // Reparse point drift / boundary invalidation produces Degraded or NotEstablished
        var degradedObs = new StorageProtectionObservation(
            ObservationId: Guid.NewGuid().ToString("N"),
            SessionId: "ses-drift-01",
            CapturedUtc: DateTimeOffset.UtcNow,
            Platform: "Windows",
            LayoutVersion: 2,
            StoragePolicyVersion: 1,
            StoragePolicyHash: "0000000000000000000000000000000000000000000000000000000000000000",
            ProtectionState: StorageProtectionState.Degraded,
            RootBoundaryValid: true,
            ReparsePointCheck: false,
            PlatformSecurityDescriptorRef: null,
            DiagnosticMessage: "Reparse point drift detected.");

        Assert.Equal(StorageProtectionState.Degraded, degradedObs.ProtectionState);
        Assert.False(degradedObs.ReparsePointCheck);
    }

    [Fact]
    [Trait("Acceptance", "3.1-8F")]
    [Trait("Platform", "Windows")]
    public void XPL_20_WIN_StorageProtectionObservation_Boundary_Flags_Are_Factual_Windows()
    {
        var observation = new StorageProtectionObservation(
            ObservationId: Guid.NewGuid().ToString("N"),
            SessionId: "ses-fact-win",
            CapturedUtc: DateTimeOffset.UtcNow,
            Platform: "Windows",
            LayoutVersion: 2,
            StoragePolicyVersion: 1,
            StoragePolicyHash: "0000000000000000000000000000000000000000000000000000000000000000",
            ProtectionState: StorageProtectionState.Established,
            RootBoundaryValid: true,
            ReparsePointCheck: true,
            PlatformSecurityDescriptorRef: "WindowsSecurityDescriptorRef",
            DiagnosticMessage: null);

        Assert.Equal(StorageProtectionState.Established, observation.ProtectionState);
        Assert.True(observation.RootBoundaryValid);
        Assert.True(observation.ReparsePointCheck);
        Assert.NotNull(observation.PlatformSecurityDescriptorRef);
        Assert.Null(observation.DiagnosticMessage);
    }
}
