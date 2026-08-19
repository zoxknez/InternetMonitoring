using System.Formats.Asn1;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Evidence.Timestamping;

namespace IEM.Core.Tests;

/// <summary>
/// Verifies RFC 3161 trusted timestamping contracts, MessageImprint derivation,
/// nonce validation, network fallback (Pending), and offline verification.
/// </summary>
public sealed class TimestampTests : IDisposable
{
    private readonly string _tempDir;

    public TimestampTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "iem-timestamp-tests-" + Guid.NewGuid().ToString("N"));
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

    private static X509Certificate2 CreateTestTsaCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=IEM Test TSA Authority, O=IEM, C=RS", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") }, critical: true));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static byte[] IssueTestTimestampToken(
        byte[] imprintBytes,
        byte[]? nonceBytes,
        X509Certificate2 tsaCert,
        DateTimeOffset genTime)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(1); // version
            writer.WriteObjectIdentifier("1.3.6.1.4.1.12345.1"); // policyId
            using (writer.PushSequence()) // MessageImprint
            {
                using (writer.PushSequence()) // AlgorithmIdentifier
                {
                    writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1"); // SHA-256
                    writer.WriteNull(); // Parameter NULL
                }
                writer.WriteOctetString(imprintBytes);
            }
            writer.WriteInteger(123456789); // SerialNumber
            writer.WriteGeneralizedTime(genTime); // GenTime
            if (nonceBytes != null)
            {
                writer.WriteInteger(new BigInteger(nonceBytes, isUnsigned: true, isBigEndian: true));
            }
        }

        var tstInfoBytes = writer.Encode();
        var contentInfo = new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), tstInfoBytes);
        var signedCms = new SignedCms(contentInfo, detached: false);
        var signer = new CmsSigner(tsaCert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        // RFC 5816 / RFC 3161: id-aa-signingCertificateV2 attribute
        var certHash = SHA256.HashData(tsaCert.RawData);
        var essWriter = new AsnWriter(AsnEncodingRules.DER);
        using (essWriter.PushSequence()) // SigningCertificateV2
        {
            using (essWriter.PushSequence()) // SEQUENCE OF ESSCertIDv2
            {
                using (essWriter.PushSequence()) // ESSCertIDv2
                {
                    essWriter.WriteOctetString(certHash);
                }
            }
        }
        signer.SignedAttributes.Add(new Pkcs9AttributeObject(new Oid("1.2.840.113549.1.9.16.2.47"), essWriter.Encode()));

        signedCms.ComputeSignature(signer);
        return signedCms.Encode();
    }


    [Fact]
    public void Request_contains_SHA256_algorithm_and_random_nonce()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("sample manifest.sig bytes"));
        var nonce = RandomNumberGenerator.GetBytes(16);

        var request = Rfc3161TimestampRequest.CreateFromHash(
            hash,
            HashAlgorithmName.SHA256,
            requestSignerCertificates: true,
            nonce: nonce);

        Assert.Equal("sha256", request.HashAlgorithmId.FriendlyName, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(request.GetNonce());

        var actualNonce = request.GetNonce()!.Value.Span;
        var actualInt = new System.Numerics.BigInteger(actualNonce, isUnsigned: true, isBigEndian: true);
        var expectedInt = new System.Numerics.BigInteger(nonce, isUnsigned: true, isBigEndian: true);
        Assert.Equal(expectedInt, actualInt);




        var encoded = request.Encode();
        Assert.NotEmpty(encoded);

        Assert.True(Rfc3161TimestampRequest.TryDecode(encoded, out var decoded, out _));
        Assert.NotNull(decoded);
    }


    [Fact]
    public void Valid_timestamp_verifies_against_manifest_sig_bytes()
    {
        using var cert = CreateTestTsaCert();
        var manifestSigBytes = Encoding.UTF8.GetBytes("{\"signature\":\"valid_sig_bytes\"}");
        var imprint = SHA256.HashData(manifestSigBytes);
        var nonce = RandomNumberGenerator.GetBytes(16);

        var request = Rfc3161TimestampRequest.CreateFromHash(
            imprint,
            HashAlgorithmName.SHA256,
            requestSignerCertificates: true,
            nonce: nonce);
        var requestBytes = request.Encode();

        var tsrBytes = IssueTestTimestampToken(imprint, nonce, cert, DateTimeOffset.UtcNow);

        var result = Rfc3161TimestampVerifier.Verify(manifestSigBytes, tsrBytes, requestBytes);

        Assert.True(result.IsCryptographicallyValid);
        Assert.NotNull(result.Timestamp);
        Assert.Equal(Convert.ToHexStringLower(imprint), result.Timestamp.MessageImprintSha256);
        Assert.Equal(TrustedTimeState.ValidUntrusted, result.State); // Self-signed test root -> ValidUntrusted
    }

    [Fact]
    public void Changing_one_byte_of_manifest_sig_breaks_timestamp_binding()
    {
        using var cert = CreateTestTsaCert();
        var manifestSigBytes = Encoding.UTF8.GetBytes("original manifest.sig bytes");
        var imprint = SHA256.HashData(manifestSigBytes);

        var tsrBytes = IssueTestTimestampToken(imprint, null, cert, DateTimeOffset.UtcNow);

        var tamperedManifestSigBytes = Encoding.UTF8.GetBytes("tampered manifest.sig bytes");
        var result = Rfc3161TimestampVerifier.Verify(tamperedManifestSigBytes, tsrBytes);

        Assert.False(result.IsCryptographicallyValid);
        Assert.Equal(TrustedTimeState.Invalid, result.State);
        Assert.Contains("MessageImprint", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Wrong_nonce_returns_Invalid()
    {
        using var cert = CreateTestTsaCert();
        var manifestSigBytes = Encoding.UTF8.GetBytes("sample sig bytes");
        var imprint = SHA256.HashData(manifestSigBytes);

        var reqNonce = RandomNumberGenerator.GetBytes(16);
        var request = Rfc3161TimestampRequest.CreateFromHash(
            imprint,
            HashAlgorithmName.SHA256,
            requestSignerCertificates: true,
            nonce: reqNonce);
        var requestBytes = request.Encode();

        var respNonce = RandomNumberGenerator.GetBytes(16); // Different nonce!
        var tsrBytes = IssueTestTimestampToken(imprint, respNonce, cert, DateTimeOffset.UtcNow);

        var result = Rfc3161TimestampVerifier.Verify(manifestSigBytes, tsrBytes, requestBytes);

        Assert.False(result.IsCryptographicallyValid);
        Assert.Equal(TrustedTimeState.Invalid, result.State);
        Assert.Contains("Nonce", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Network_failure_becomes_Pending_not_Invalid_and_leaves_session_sealed()
    {
        var manifestPath = Path.Combine(_tempDir, "manifest.json");
        var sigPath = Path.Combine(_tempDir, "manifest.sig");

        await File.WriteAllTextAsync(manifestPath, "{\"manifest\":1}");
        await File.WriteAllTextAsync(sigPath, "{\"signature\":\"MEUCIQ...\"}");

        // Unreachable local port
        var unreachableUri = new Uri("http://127.0.0.1:54321/tsa");

        var result = await Rfc3161TimestampClient.RequestTimestampAsync(_tempDir, unreachableUri);

        Assert.Equal(TrustedTimeState.Pending, result.State);
        Assert.True(result.IsPending);
        Assert.False(result.IsSuccess);
        Assert.Contains("TSA server nije dostupan", result.Message, StringComparison.Ordinal);

        // Verify session files were not modified or deleted
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(sigPath));
        Assert.False(File.Exists(Path.Combine(_tempDir, "Evidence", "timestamp", "timestamp.tsr")));
    }

    [Fact]
    public void Timestamp_presentation_text_adheres_to_Invariant_17()
    {
        var validTimestamp = new TrustedTimestamp(
            GenTimeUtc: new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            MessageImprintSha256: "e3b0c442...",
            State: TrustedTimeState.ValidTrusted,
            TsaSubjectName: "CN=Sectigo TSA");

        var text = validTimestamp.PresentationText;

        Assert.Contains("najkasnije u trenutku", text, StringComparison.Ordinal);
        Assert.DoesNotContain("prekid se desio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retry_is_idempotent_and_does_not_modify_sealed_evidence()
    {
        using var cert = CreateTestTsaCert();
        var manifestSigBytes = Encoding.UTF8.GetBytes("{\"signature\":\"exact_bytes\"}");
        var imprint = SHA256.HashData(manifestSigBytes);
        var nonce = RandomNumberGenerator.GetBytes(16);

        var tsrBytes = IssueTestTimestampToken(imprint, nonce, cert, DateTimeOffset.UtcNow);

        var sigPath = Path.Combine(_tempDir, "manifest.sig");
        await File.WriteAllBytesAsync(sigPath, manifestSigBytes);

        var timestampDir = Path.Combine(_tempDir, "Evidence", "timestamp");
        Directory.CreateDirectory(timestampDir);
        var tsrPath = Path.Combine(timestampDir, "timestamp.tsr");
        await File.WriteAllBytesAsync(tsrPath, tsrBytes);

        // Attempt timestamping with existing valid TSR -> should return existing without re-requesting
        var dummyUri = new Uri("http://127.0.0.1:54321/tsa");
        var result = await Rfc3161TimestampClient.RequestTimestampAsync(_tempDir, dummyUri);

        Assert.True(result.IsSuccess);
        Assert.Equal(tsrPath, result.ArtifactPath);
        Assert.Equal(Convert.ToHexStringLower(imprint), result.Timestamp?.MessageImprintSha256);
    }

    [Fact]
    public void Golden_fixture_rfc3161_offline_verification()
    {
        using var cert = CreateTestTsaCert();
        var manifestSigBytes = Encoding.UTF8.GetBytes("{\"signature\":\"golden_manifest_sig\"}");
        var imprint = SHA256.HashData(manifestSigBytes);
        var nonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

        var request = Rfc3161TimestampRequest.CreateFromHash(
            imprint,
            HashAlgorithmName.SHA256,
            requestSignerCertificates: true,
            nonce: nonce);
        var tsqBytes = request.Encode();

        var genTime = new DateTimeOffset(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);
        var tsrBytes = IssueTestTimestampToken(imprint, nonce, cert, genTime);

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Rfc3161");
        if (!Directory.Exists(fixtureDir))
        {
            fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "Rfc3161"));
        }
        Directory.CreateDirectory(fixtureDir);

        var tsqPath = Path.Combine(fixtureDir, "request.tsq");
        var tsrPath = Path.Combine(fixtureDir, "response.tsr");
        var cerPath = Path.Combine(fixtureDir, "signer.cer");
        var sigPath = Path.Combine(fixtureDir, "manifest.sig");

        if (!File.Exists(tsqPath))
        {
            File.WriteAllBytes(tsqPath, tsqBytes);
            File.WriteAllBytes(tsrPath, tsrBytes);
            File.WriteAllBytes(cerPath, cert.RawData);
            File.WriteAllBytes(sigPath, manifestSigBytes);
        }

        var savedSigBytes = File.ReadAllBytes(sigPath);
        var savedTsrBytes = File.ReadAllBytes(tsrPath);
        var savedTsqBytes = File.ReadAllBytes(tsqPath);

        var result = Rfc3161TimestampVerifier.Verify(savedSigBytes, savedTsrBytes, savedTsqBytes);

        Assert.True(result.IsCryptographicallyValid);
        Assert.NotNull(result.Timestamp);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(savedSigBytes)), result.Timestamp.MessageImprintSha256);
    }

    [Fact]
    public async Task Live_TSA_test_gated_by_environment_variable()
    {
        if (Environment.GetEnvironmentVariable("IEM_TEST_LIVE_TSA") != "1")
        {
            // Gated for CI stability; only runs when explicitly requested
            return;
        }

        var tsaUri = new Uri("https://freetsa.org/tsr");
        var testBytes = Encoding.UTF8.GetBytes("live test manifest sig");
        await File.WriteAllBytesAsync(Path.Combine(_tempDir, "manifest.sig"), testBytes);

        var result = await Rfc3161TimestampClient.RequestTimestampAsync(_tempDir, tsaUri);
        Assert.True(result.IsSuccess || result.IsPending);
    }
}


