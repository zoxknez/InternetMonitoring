using System.Security.Cryptography;
using System.Text;
using IEM.Evidence.Canonicalization;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;

namespace IEM.Core.Tests;

/// <summary>
/// Verifies the 3.0-3 cryptographic signing contracts, deterministic KeyId,
/// DER signature encoding, and verification primitives.
/// </summary>
public sealed class SigningTests : IDisposable
{
    private readonly string _tempDir;

    public SigningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "iem-signing-tests-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void KeyId_equals_SHA256_of_SPKI_public_key()
    {
        using var identity = new EphemeralSoftwareSigningIdentity();

        var expectedKeyId = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(identity.PublicKey));

        Assert.Equal(expectedKeyId, identity.KeyId);
        Assert.StartsWith("sha256:", identity.KeyId);
        Assert.Equal(71, identity.KeyId.Length); // "sha256:" + 64 hex chars
    }

    [Fact]
    public async Task Manifest_hash_is_signed_successfully_and_verifies()
    {
        using var identity = new EphemeralSoftwareSigningIdentity();

        var manifestJson = "{\"manifestSchemaVersion\":1,\"canonicalization\":\"RFC8785-JCS\"}";
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(_tempDir, identity);

        Assert.NotNull(envelope);
        Assert.Equal(identity.KeyId, envelope.KeyId);
        Assert.Equal(Convert.ToBase64String(identity.PublicKey), envelope.PublicKeyBase64);

        var result = SignatureVerifier.Verify(manifestBytes, envelope);
        Assert.True(result.IsValid);
        Assert.Equal(SignatureVerificationStatus.Valid, result.Status);
    }

    [Fact]
    public async Task One_byte_manifest_change_breaks_signature()
    {
        using var identity = new EphemeralSoftwareSigningIdentity();

        var manifestJson = "{\"manifestSchemaVersion\":1,\"canonicalization\":\"RFC8785-JCS\"}";
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        await File.WriteAllBytesAsync(manifestPath, manifestBytes);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(_tempDir, identity);

        // Tamper with manifest bytes (change 1 to 2)
        var tamperedJson = "{\"manifestSchemaVersion\":2,\"canonicalization\":\"RFC8785-JCS\"}";
        var tamperedBytes = Encoding.UTF8.GetBytes(tamperedJson);

        var result = SignatureVerifier.Verify(tamperedBytes, envelope);
        Assert.False(result.IsValid);
        Assert.Equal(SignatureVerificationStatus.HashMismatch, result.Status);
    }

    [Fact]
    public async Task Different_public_key_breaks_signature()
    {
        using var identity1 = new EphemeralSoftwareSigningIdentity();
        using var identity2 = new EphemeralSoftwareSigningIdentity();

        var manifestBytes = Encoding.UTF8.GetBytes("{\"data\":\"test\"}");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "manifest.json"), manifestBytes);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(_tempDir, identity1);

        // Replace public key in envelope with identity2's public key (and its keyId)
        var tamperedEnvelope = envelope with
        {
            PublicKeyBase64 = Convert.ToBase64String(identity2.PublicKey),
            KeyId = identity2.KeyId,
        };

        var result = SignatureVerifier.Verify(manifestBytes, tamperedEnvelope);
        Assert.False(result.IsValid);
        Assert.Equal(SignatureVerificationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public async Task Different_manifest_hash_breaks_signature()
    {
        using var identity = new EphemeralSoftwareSigningIdentity();

        var manifestBytes = Encoding.UTF8.GetBytes("{\"data\":\"test\"}");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "manifest.json"), manifestBytes);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(_tempDir, identity);

        var tamperedEnvelope = envelope with
        {
            ManifestSha256 = "0000000000000000000000000000000000000000000000000000000000000000",
        };

        var result = SignatureVerifier.Verify(manifestBytes, tamperedEnvelope);
        Assert.False(result.IsValid);
        Assert.Equal(SignatureVerificationStatus.HashMismatch, result.Status);
    }

    [Fact]
    public async Task Signature_is_RFC3279_DER_not_P1363()
    {
        using var identity = new EphemeralSoftwareSigningIdentity();

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("hello"));
        var signature = await identity.SignHashAsync(hash);

        // RFC 3279 DER Sequence starts with tag 0x30 (SEQUENCE) followed by length
        Assert.NotEmpty(signature);
        Assert.Equal(0x30, signature[0]);

        // IEEE P1363 is exactly 64 bytes (r || s); DER sequence is usually 70-72 bytes
        Assert.InRange(signature.Length, 68, 73);
    }

    [Fact]
    public async Task Private_key_never_appears_in_session_files()
    {
        using var identity = new EphemeralSoftwareSigningIdentity();

        var manifestBytes = Encoding.UTF8.GetBytes("{\"manifestSchemaVersion\":1}");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "manifest.json"), manifestBytes);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(_tempDir, identity);

        var sigPath = Path.Combine(_tempDir, "manifest.sig");
        Assert.True(File.Exists(sigPath));

        var sigFileContent = await File.ReadAllTextAsync(sigPath);
        Assert.DoesNotContain("PRIVATE KEY", sigFileContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"privateKey\"", sigFileContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"private_key\"", sigFileContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"d\":", sigFileContent, StringComparison.Ordinal); // exact JSON property "d":

    }

    [Fact]
    public async Task Partial_signature_is_never_published()
    {
        using var identity = new EphemeralSoftwareSigningIdentity();

        var manifestBytes = Encoding.UTF8.GetBytes("{\"manifestSchemaVersion\":1}");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "manifest.json"), manifestBytes);

        var envelope = await ManifestSigner.SignManifestAtomicallyAsync(_tempDir, identity);

        Assert.True(File.Exists(Path.Combine(_tempDir, "manifest.sig")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "manifest.sig.tmp")));
    }
}
