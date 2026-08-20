using System.Security.Cryptography;
using System.Text;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Linux.Crypto;
using IEM.Linux.Storage;
using Xunit;

namespace IEM.Core.Tests;

public sealed class LinuxEvidenceKeyProviderTests
{
    // ==========================================
    // 1. FIRST CREATE & PARITY TESTS
    // ==========================================

    [Fact]
    public async Task New_System_Identity_Creates_P256_Key_With_Exact_0700_And_0600()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(
            scope: LinuxSigningIdentityScope.SystemInstallation,
            customStateRoot: "/var/lib/internet-evidence-monitor",
            posix: mock,
            ownershipPolicy: policy);

        using var identity = await provider.GetOrCreateIdentityAsync();

        Assert.NotNull(identity);
        Assert.StartsWith("sha256:", identity.KeyId);
        Assert.Equal(SignatureSuite.EcdsaP256Sha256, identity.Suite);
        Assert.NotEmpty(identity.PublicKey);
        Assert.Equal(KeyProtectionLevel.SoftwareProtected, identity.Protection.Protection);
        Assert.Equal(KeyProtectionEvidence.ProviderReported, identity.Protection.Evidence);

        // Verify keys directory mode is 0700
        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys", out var keysEntry));
        Assert.True(keysEntry.Stat.IsDirectory);
        Assert.Equal(0x1C0, (int)(keysEntry.Stat.Mode & 0xFFF)); // 0700

        // Verify key file mode is 0600
        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out var keyEntry));
        Assert.True(keyEntry.Stat.IsRegularFile);
        Assert.Equal(0x180, (int)(keyEntry.Stat.Mode & 0xFFF)); // 0600
        Assert.NotEmpty(keyEntry.Content);
    }

    [Fact]
    public async Task Linux_Signature_Verifies_In_Shared_SignatureVerifier()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(
            scope: LinuxSigningIdentityScope.SystemInstallation,
            customStateRoot: "/var/lib/internet-evidence-monitor",
            posix: mock,
            ownershipPolicy: policy);

        using var identity = await provider.GetOrCreateIdentityAsync();

        var payload = "Test canonical manifest payload to sign"u8.ToArray();
        var manifestHash = SHA256.HashData(payload);

        var signature = await identity.SignHashAsync(manifestHash);
        Assert.NotEmpty(signature);

        var envelope = new SignatureEnvelope(
            SignatureEnvelope.CurrentEnvelopeVersion,
            Convert.ToHexStringLower(manifestHash),
            identity.KeyId,
            identity.Suite,
            Convert.ToBase64String(identity.PublicKey),
            identity.Protection,
            Convert.ToBase64String(signature),
            DateTimeOffset.UtcNow);

        var result = SignatureVerifier.Verify(payload, envelope);
        Assert.True(result.IsValid, result.Message);
    }

    // ==========================================
    // 2. PERSISTENCE & NO SILENT ROTATION
    // ==========================================

    [Fact]
    public async Task Persistence_GetOrCreate_Twice_Returns_Identical_KeyId_Without_Rewriting()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(
            scope: LinuxSigningIdentityScope.SystemInstallation,
            customStateRoot: "/var/lib/internet-evidence-monitor",
            posix: mock,
            ownershipPolicy: policy);

        using var id1 = await provider.GetOrCreateIdentityAsync();
        int writesAfterFirst = mock.WriteCallCount;

        // Second call should reuse existing key
        using var id2 = await provider.GetOrCreateIdentityAsync();
        int writesAfterSecond = mock.WriteCallCount;

        Assert.Equal(id1.KeyId, id2.KeyId);
        Assert.Equal(id1.PublicKey, id2.PublicKey);
        Assert.Equal(writesAfterFirst, writesAfterSecond); // ZERO new writes!
    }

    [Fact]
    public async Task Restart_Simulation_Loads_Same_Key_Without_Fchmod_Or_Fchown()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);

        // Instance 1 creates key
        var prov1 = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);
        using var id1 = await prov1.GetOrCreateIdentityAsync();

        mock.ResetCounters();

        // Instance 2 (after service restart) opens existing key
        var prov2 = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);
        using var id2 = await prov2.GetOrCreateIdentityAsync();

        Assert.Equal(id1.KeyId, id2.KeyId);
        Assert.Equal(0, mock.FchmodCallCount); // ZERO repair!
        Assert.Equal(0, mock.FchownCallCount); // ZERO repair!
        Assert.Equal(0, mock.WriteCallCount);  // ZERO writes!
    }

    // ==========================================
    // 3. FAIL-CLOSED SECURITY TESTS
    // ==========================================

    [Theory]
    [InlineData(0x1A4)] // 0644
    [InlineData(0x1B0)] // 0660
    [InlineData(0x1ED)] // 0755
    [InlineData(0x980)] // 04600 (setuid + 0600)
    public async Task Existing_Key_With_Invalid_Permissions_Throws_And_Never_Rotates(int mode)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: mode, uid: 1000, gid: 1000, content: pkcs8);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("permissions", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify key was NOT deleted or overwritten
        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out var entry));
        Assert.Equal(pkcs8, entry.Content);
        Assert.Equal(0, mock.WriteCallCount);
    }

    [Fact]
    public async Task Existing_Key_With_Wrong_Owner_Throws_SigningIdentityUnavailableException()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 0, gid: 0, content: pkcs8); // Owned by root!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("UID mismatch", ex.Message);
        Assert.Equal(0, mock.WriteCallCount);
    }

    [Fact]
    public async Task Existing_Key_With_Wrong_Gid_Throws_SigningIdentityUnavailableException()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 9999, content: pkcs8); // Wrong GID!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("GID mismatch", ex.Message);
    }

    [Fact]
    public async Task Existing_Key_As_Symlink_Throws_SigningIdentityUnavailableException()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: true, mode: 0x180, uid: 1000, gid: 1000); // Symlink!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
    }

    [Fact]
    public async Task Existing_Keys_Directory_As_Symlink_Throws_SigningIdentityUnavailableException()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: true, mode: 0x1C0, uid: 1000, gid: 1000); // Keys dir is symlink!

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
    }

    [Fact]
    public async Task Truncated_Or_Corrupted_PKCS8_Throws_SigningIdentityUnavailableException()
    {
        var corrupted = new byte[] { 0x30, 0x81, 0x87, 0x02, 0x01, 0x00, 0xFF, 0xEE }; // Malformed PKCS#8

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: corrupted);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("PKCS#8", ex.Message);
    }

    [Fact]
    public async Task Valid_PKCS8_With_Trailing_Garbage_Throws_SigningIdentityUnavailableException()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        var withGarbage = new byte[pkcs8.Length + 4];
        Buffer.BlockCopy(pkcs8, 0, withGarbage, 0, pkcs8.Length);
        withGarbage[^1] = 0xAA; // Trailing byte!

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: withGarbage);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("Trailing", ex.Message);
    }

    [Fact]
    public async Task Non_P256_EC_Key_Throws_SigningIdentityUnavailableException()
    {
        using var ecdsaP384 = ECDsa.Create(ECCurve.NamedCurves.nistP384); // P-384 instead of P-256
        var pkcs8 = ecdsaP384.ExportPkcs8PrivateKey();

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: pkcs8);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("P-256", ex.Message);
    }

    // ==========================================
    // 4. ATOMICITY, DURABILITY & RACE CONDITIONS
    // ==========================================

    [Fact]
    public async Task R1_A_Directory_Fsync_Failure_After_Rename_Throws_SigningIdentityUnavailableException()
    {
        var mock = new MockPosixStorageApi
        {
            FailKeysDirFsync = true // Directory fsync fails after publish
        };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("durability", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R1_B_Temp_Fchmod_Failure_Aborts_Key_Provisioning_Without_Publishing()
    {
        var mock = new MockPosixStorageApi
        {
            FailFchmodOnTemp = true
        };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateIdentityAsync());
        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public async Task R1_B_Temp_Wrong_Uid_Aborts_Key_Provisioning()
    {
        var mock = new MockPosixStorageApi
        {
            ForceTempUid = 9999 // Different from expected 1000
        };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateIdentityAsync());
        Assert.False(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    [Fact]
    public async Task R1_C_D_Actual_Rename_Race_EEXIST_Uses_Existing_Winner_Key()
    {
        using var winnerEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var winnerPkcs8 = winnerEcdsa.ExportPkcs8PrivateKey();
        var winnerKeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(winnerEcdsa.ExportSubjectPublicKeyInfo()));

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        // Hook injects winner key right before renameat2 executes, simulating a true multi-process race
        mock.BeforeRenameAt2Hook = () =>
        {
            mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: winnerPkcs8);
            mock.LastErrno = LinuxPosixStorageConstants.EEXIST;
            return false; // rename fails with EEXIST because winner was published first
        };

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        using var identity = await provider.GetOrCreateIdentityAsync();

        Assert.Equal(winnerKeyId, identity.KeyId);
    }

    [Fact]
    public async Task R1_C_Rename_EIO_Must_Not_Reinterpret_As_Race_Even_If_Final_Appears()
    {
        using var winnerEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var winnerPkcs8 = winnerEcdsa.ExportPkcs8PrivateKey();

        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        mock.BeforeRenameAt2Hook = () =>
        {
            mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: winnerPkcs8);
            mock.LastErrno = LinuxPosixStorageConstants.EIO; // I/O error, not EEXIST!
            return false;
        };

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("errno 5", ex.Message); // EIO = 5
    }

    [Fact]
    public async Task R1_C_Rename_EINVAL_Fails_Closed()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        mock.BeforeRenameAt2Hook = () =>
        {
            mock.LastErrno = LinuxPosixStorageConstants.EINVAL;
            return false;
        };

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("errno 22", ex.Message); // EINVAL = 22
    }

    [Fact]
    public async Task R1_G_Partial_Write_And_Partial_Read_Complete_Successfully()
    {
        var mock = new MockPosixStorageApi
        {
            WriteChunkSize = 16, // Forces WriteAll loop
            ReadChunkSize = 16   // Forces ReadExactly loop
        };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        using var id1 = await provider.GetOrCreateIdentityAsync();
        Assert.NotNull(id1);

        using var id2 = await provider.GetOrCreateIdentityAsync();
        Assert.Equal(id1.KeyId, id2.KeyId);
    }

    [Fact]
    public async Task R1_G_Write_Error_Fails_Closed()
    {
        var mock = new MockPosixStorageApi
        {
            FailWrite = true
        };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateIdentityAsync());
    }

    [Fact]
    public async Task R1_G_Read_Error_Throws_SigningIdentityUnavailableException()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();

        var mock = new MockPosixStorageApi
        {
            FailRead = true
        };
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", isDir: false, isSymlink: false, mode: 0x180, uid: 1000, gid: 1000, content: pkcs8);

        var policy = LinuxStorageOwnershipPolicy.CreateSystem(uid: 1000, gid: 1000);
        var provider = new LinuxEvidenceKeyProvider(LinuxSigningIdentityScope.SystemInstallation, "/var/lib/internet-evidence-monitor", mock, policy);

        await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
    }

    [Fact]
    public async Task R1_F_Missing_Portable_StateRoot_Throws_SigningIdentityUnavailableException()
    {
        var mock = new MockPosixStorageApi(); // Empty filesystem, portable StateRoot does not exist!

        var env = new Dictionary<string, string> { ["HOME"] = "/home/user" };
        var provider = new LinuxEvidenceKeyProvider(
            scope: LinuxSigningIdentityScope.PortableUser,
            posix: mock,
            getEnvironmentVariable: k => env.GetValueOrDefault(k));

        var ex = await Assert.ThrowsAsync<SigningIdentityUnavailableException>(() => provider.GetOrCreateIdentityAsync());
        Assert.Contains("does not exist", ex.Message);
    }

    // ==========================================
    // 5. NAMESPACE ISOLATION TESTS
    // ==========================================

    [Fact]
    public async Task System_And_Portable_Identities_Have_Strict_Namespace_Isolation()
    {
        var mock = new MockPosixStorageApi();
        mock.AddEntry("/var/lib/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);
        mock.AddEntry("/home/user/.local/state/internet-evidence-monitor", isDir: true, isSymlink: false, mode: 0x1C0, uid: 1000, gid: 1000);

        var env = new Dictionary<string, string>
        {
            ["HOME"] = "/home/user"
        };

        var sysProvider = new LinuxEvidenceKeyProvider(
            scope: LinuxSigningIdentityScope.SystemInstallation,
            customStateRoot: "/var/lib/internet-evidence-monitor",
            posix: mock);

        var portableProvider = new LinuxEvidenceKeyProvider(
            scope: LinuxSigningIdentityScope.PortableUser,
            posix: mock,
            getEnvironmentVariable: k => env.GetValueOrDefault(k));

        using var sysId = await sysProvider.GetOrCreateIdentityAsync();
        using var portId = await portableProvider.GetOrCreateIdentityAsync();

        // System and portable keys must be distinct files with distinct KeyIds
        Assert.NotEqual(sysId.KeyId, portId.KeyId);
        Assert.NotEqual(sysId.PublicKey, portId.PublicKey);

        Assert.True(mock.TryGetEntry("/var/lib/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
        Assert.True(mock.TryGetEntry("/home/user/.local/state/internet-evidence-monitor/keys/evidence-signing-v1.p8", out _));
    }

    // ==========================================
    // MOCK POSIX API HELPER
    // ==========================================

    private sealed class MockPosixStorageApi : ILinuxPosixStorageApi
    {
        public sealed class FileEntry
        {
            public PosixStat Stat;
            public byte[] Content = Array.Empty<byte>();
            public int ReadOffset;
        }

        private readonly Dictionary<string, FileEntry> _entries = new(StringComparer.Ordinal);
        private int _fdCounter = 100;
        private readonly Dictionary<int, string> _openFds = new();

        public bool FailOpenAt2 { get; set; }
        public bool FailFsync { get; set; }
        public bool FailKeysDirFsync { get; set; }
        public bool FailFchmodOnTemp { get; set; }
        public bool FailWrite { get; set; }
        public bool FailRead { get; set; }
        public int WriteChunkSize { get; set; } = int.MaxValue;
        public int ReadChunkSize { get; set; } = int.MaxValue;
        public uint? ForceTempUid { get; set; }

        public Func<bool>? BeforeRenameAt2Hook { get; set; }
        public int LastErrno { get; set; }

        public int WriteCallCount { get; private set; }
        public int FchmodCallCount { get; private set; }
        public int FchownCallCount { get; private set; }

        public void ResetCounters()
        {
            WriteCallCount = 0;
            FchmodCallCount = 0;
            FchownCallCount = 0;
        }

        public bool TryGetEntry(string path, out FileEntry entry)
        {
            return _entries.TryGetValue(path.TrimEnd('/'), out entry!);
        }

        public void AddEntry(string path, bool isDir, bool isSymlink, int mode, uint uid, uint gid, byte[]? content = null)
        {
            uint fullMode = (uint)mode;
            if (isSymlink) fullMode |= LinuxPosixStorageConstants.S_IFLNK;
            else if (isDir) fullMode |= LinuxPosixStorageConstants.S_IFDIR;
            else fullMode |= LinuxPosixStorageConstants.S_IFREG;

            var bytes = content ?? Array.Empty<byte>();
            _entries[path.TrimEnd('/')] = new FileEntry
            {
                Stat = new PosixStat
                {
                    Mode = fullMode,
                    Uid = uid,
                    Gid = gid,
                    Size = bytes.Length
                },
                Content = bytes
            };
        }

        public int Open(string path, int flags, int mode)
        {
            var norm = path.TrimEnd('/');
            if (_entries.ContainsKey(norm))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = norm;
                return fd;
            }
            return -1;
        }

        public int OpenAt(int dirfd, string pathname, int flags, int mode)
        {
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            if (_entries.ContainsKey(fullPath))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = fullPath;
                return fd;
            }
            return -1;
        }

        public int OpenAt2(int dirfd, string pathname, ref OpenHow how)
        {
            if (FailOpenAt2) return -1;
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var fullPath = $"{baseDir}/{pathname}".TrimEnd('/');

            if (_entries.TryGetValue(fullPath, out var existing))
            {
                if (existing.Stat.IsSymlink && (how.Resolve & LinuxPosixStorageConstants.RESOLVE_NO_SYMLINKS) != 0)
                {
                    return -1; // ELOOP / symlink rejected
                }
            }

            bool exists = _entries.ContainsKey(fullPath);
            bool isOcreat = (how.Flags & (ulong)LinuxPosixStorageConstants.O_CREAT) != 0;
            bool isOexcl = (how.Flags & (ulong)LinuxPosixStorageConstants.O_EXCL) != 0;

            if (exists && isOcreat && isOexcl)
            {
                LastErrno = LinuxPosixStorageConstants.EEXIST;
                return -1;
            }

            if (!exists && isOcreat)
            {
                uint uid = ForceTempUid ?? 1000;
                AddEntry(fullPath, isDir: false, isSymlink: false, mode: (int)how.Mode, uid: uid, gid: 1000);
            }

            if (_entries.ContainsKey(fullPath))
            {
                var fd = ++_fdCounter;
                _openFds[fd] = fullPath;
                _entries[fullPath].ReadOffset = 0;
                return fd;
            }
            return -1;
        }

        public int FstatAt(int dirfd, string pathname, out PosixStat statbuf, int flags)
        {
            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD) fullPath = pathname.TrimEnd('/');
            else if (_openFds.TryGetValue(dirfd, out var baseDir)) fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            else
            {
                statbuf = default;
                return -1;
            }

            if (_entries.TryGetValue(fullPath, out var entry))
            {
                statbuf = entry.Stat;
                return 0;
            }
            statbuf = default;
            return -1;
        }

        public int Fstat(int fd, out PosixStat statbuf)
        {
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                statbuf = entry.Stat;
                return 0;
            }
            statbuf = default;
            return -1;
        }

        public int MkdirAt(int dirfd, string pathname, int mode)
        {
            string fullPath;
            if (dirfd == LinuxPosixStorageConstants.AT_FDCWD) fullPath = pathname.TrimEnd('/');
            else if (_openFds.TryGetValue(dirfd, out var baseDir)) fullPath = $"{baseDir}/{pathname}".TrimEnd('/');
            else return -1;

            AddEntry(fullPath, isDir: true, isSymlink: false, mode: mode, uid: 1000, gid: 1000);
            return 0;
        }

        public int Fchmod(int fd, int mode)
        {
            if (_openFds.TryGetValue(fd, out var path) && path.Contains(".tmp.") && FailFchmodOnTemp)
            {
                return -1;
            }

            FchmodCallCount++;
            if (_openFds.TryGetValue(fd, out var p) && _entries.TryGetValue(p, out var entry))
            {
                uint cleanMode = (entry.Stat.Mode & ~0xFFFu) | (uint)(mode & 0xFFF);
                entry.Stat.Mode = cleanMode;
                return 0;
            }
            return -1;
        }

        public int Fchown(int fd, uint uid, uint gid)
        {
            FchownCallCount++;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                entry.Stat.Uid = uid;
                entry.Stat.Gid = gid;
                return 0;
            }
            return -1;
        }

        public int RenameAt2(int olddirfd, string oldpath, int newdirfd, string newpath, uint flags)
        {
            if (BeforeRenameAt2Hook != null)
            {
                bool proceed = BeforeRenameAt2Hook();
                if (!proceed) return -1;
            }

            if (!_openFds.TryGetValue(olddirfd, out var oldBase) || !_openFds.TryGetValue(newdirfd, out var newBase))
                return -1;

            var oldFull = $"{oldBase}/{oldpath}".TrimEnd('/');
            var newFull = $"{newBase}/{newpath}".TrimEnd('/');

            if (!_entries.TryGetValue(oldFull, out var oldEntry)) return -1;

            if (_entries.ContainsKey(newFull))
            {
                if ((flags & LinuxPosixStorageConstants.RENAME_NOREPLACE) != 0)
                {
                    LastErrno = LinuxPosixStorageConstants.EEXIST;
                    return -1; // EEXIST
                }
            }

            _entries.Remove(oldFull);
            _entries[newFull] = oldEntry;
            return 0;
        }

        public int UnlinkAt(int dirfd, string pathname, int flags)
        {
            if (!_openFds.TryGetValue(dirfd, out var baseDir)) return -1;
            var full = $"{baseDir}/{pathname}".TrimEnd('/');
            return _entries.Remove(full) ? 0 : -1;
        }

        public int Write(int fd, ReadOnlySpan<byte> buffer)
        {
            if (FailWrite) return -1;
            WriteCallCount++;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                int toWrite = Math.Min(buffer.Length, WriteChunkSize);
                var newContent = new byte[entry.Content.Length + toWrite];
                Buffer.BlockCopy(entry.Content, 0, newContent, 0, entry.Content.Length);
                buffer.Slice(0, toWrite).CopyTo(newContent.AsSpan(entry.Content.Length));
                entry.Content = newContent;
                entry.Stat.Size = newContent.Length;
                return toWrite;
            }
            return -1;
        }

        public int Read(int fd, Span<byte> buffer)
        {
            if (FailRead) return -1;
            if (_openFds.TryGetValue(fd, out var path) && _entries.TryGetValue(path, out var entry))
            {
                int available = entry.Content.Length - entry.ReadOffset;
                if (available <= 0) return 0;
                int toCopy = Math.Min(Math.Min(buffer.Length, available), ReadChunkSize);
                entry.Content.AsSpan(entry.ReadOffset, toCopy).CopyTo(buffer);
                entry.ReadOffset += toCopy;
                return toCopy;
            }
            return -1;
        }

        public int Fsync(int fd)
        {
            if (FailFsync) return -1;
            if (_openFds.TryGetValue(fd, out var path) && path.EndsWith("/keys") && FailKeysDirFsync)
            {
                return -1;
            }
            return 0;
        }

        public int Close(int fd)
        {
            _openFds.Remove(fd);
            return 0;
        }

        public int GetLastErrno() => LastErrno;
        public uint GetEuid() => 1000;
        public uint GetEgid() => 1000;
    }
}
