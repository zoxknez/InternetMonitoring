using System.IO;
using System.Security.Cryptography;
using System.Text;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Windows.Crypto;


namespace IEM.App.Tests;

/// <summary>
/// Verifies Windows CNG key management, persistent installation identity,
/// TPM/Software provisioning, and live signing behavior.
/// </summary>
public sealed class WindowsCngKeyTests : IDisposable
{
    private readonly string _testKeyName;
    private readonly string _tempDir;
    private readonly WindowsCngKeyProvider _keyProvider;

    public WindowsCngKeyTests()
    {
        _testKeyName = "IEM_Test_Signing_Key_" + Guid.NewGuid().ToString("N")[..12];
        _keyProvider = new WindowsCngKeyProvider(_testKeyName, machineKey: false);
        _tempDir = Path.Combine(Path.GetTempPath(), "iem-cng-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _keyProvider.DeleteKeyForTesting();
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

    [Fact]
    public async Task First_run_creates_identity_and_subsequent_call_reuses_same_KeyId()
    {
        using (var identity1 = await _keyProvider.GetOrCreateIdentityAsync())
        {
            Assert.NotNull(identity1);
            Assert.StartsWith("sha256:", identity1.KeyId);
            Assert.NotNull(identity1.PublicKey);

            // Re-open the same key to verify persistent installation identity
            using (var identity2 = await _keyProvider.GetOrCreateIdentityAsync())
            {
                Assert.Equal(identity1.KeyId, identity2.KeyId);
                Assert.Equal(identity1.PublicKey, identity2.PublicKey);
                Assert.Equal(identity1.Protection.Protection, identity2.Protection.Protection);
            }
        }
    }

    [Fact]
    public async Task Two_sessions_signed_with_same_identity_both_verify_successfully()
    {
        using var identity = await _keyProvider.GetOrCreateIdentityAsync();

        var session1Dir = Path.Combine(_tempDir, "Session1");
        var session2Dir = Path.Combine(_tempDir, "Session2");
        Directory.CreateDirectory(session1Dir);
        Directory.CreateDirectory(session2Dir);

        var manifest1Bytes = Encoding.UTF8.GetBytes("{\"session\":\"S1\"}");
        var manifest2Bytes = Encoding.UTF8.GetBytes("{\"session\":\"S2\"}");

        await File.WriteAllBytesAsync(Path.Combine(session1Dir, "manifest.json"), manifest1Bytes);
        await File.WriteAllBytesAsync(Path.Combine(session2Dir, "manifest.json"), manifest2Bytes);

        var envelope1 = await ManifestSigner.SignManifestAtomicallyAsync(session1Dir, identity);
        var envelope2 = await ManifestSigner.SignManifestAtomicallyAsync(session2Dir, identity);

        // Same KeyId across distinct sessions
        Assert.Equal(envelope1.KeyId, envelope2.KeyId);
        Assert.Equal(identity.KeyId, envelope1.KeyId);

        // Distinct signatures for distinct manifests
        Assert.NotEqual(envelope1.SignatureBase64, envelope2.SignatureBase64);

        // Both verify cleanly
        var ver1 = SignatureVerifier.Verify(manifest1Bytes, envelope1);
        var ver2 = SignatureVerifier.Verify(manifest2Bytes, envelope2);

        Assert.True(ver1.IsValid);
        Assert.True(ver2.IsValid);
    }

    [Fact]
    public async Task WindowsCng_signature_is_RFC3279_DER_format()
    {
        using var identity = await _keyProvider.GetOrCreateIdentityAsync();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("windows cng test"));
        var signature = await identity.SignHashAsync(hash);

        Assert.NotEmpty(signature);
        Assert.Equal(0x30, signature[0]); // ASN.1 SEQUENCE tag
        Assert.InRange(signature.Length, 68, 73);
    }
}
