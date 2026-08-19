using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using IEM.Evidence.Manifest;

namespace IEM.Evidence.Timestamping;

/// <summary>
/// Result of requesting or validating an RFC 3161 timestamp.
/// </summary>
public sealed record TimestampResult(
    TrustedTimeState State,
    TrustedTimestamp? Timestamp,
    string? ArtifactPath = null,
    string? Message = null)
{
    public bool IsSuccess => State == TrustedTimeState.ValidTrusted || State == TrustedTimeState.ValidUntrusted;
    public bool IsPending => State == TrustedTimeState.Pending;

    public static TimestampResult Pending(string message) =>
        new(TrustedTimeState.Pending, null, null, message);

    public static TimestampResult Invalid(string message) =>
        new(TrustedTimeState.Invalid, null, null, message);

    public static TimestampResult Success(TrustedTimestamp timestamp, string artifactPath, TrustedTimeState state) =>
        new(state, timestamp, artifactPath, null);
}

/// <summary>
/// Client for requesting, verifying, and persisting RFC 3161 trusted timestamps.
/// </summary>
public static class Rfc3161TimestampClient
{
    public const string TimestampSubdirectory = "Evidence/timestamp";
    public const string RequestFileName = "timestamp.tsq";
    public const string ResponseFileName = "timestamp.tsr";
    public const string TempResponseFileName = "timestamp.tsr.tmp";
    public const string CertificatesDirectory = "Evidence/timestamp/validation/certificates";

    /// <summary>
    /// Requests a trusted timestamp for the exact <c>manifest.sig</c> bytes in the session directory.
    /// </summary>
    public static async Task<TimestampResult> RequestTimestampAsync(
        string sessionDirectory,
        Uri tsaEndpoint,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        ArgumentNullException.ThrowIfNull(tsaEndpoint);

        var sigPath = Path.Combine(sessionDirectory, SignatureEnvelope.FileName);
        if (!File.Exists(sigPath))
        {
            return TimestampResult.Invalid($"Potpis manifesta ({SignatureEnvelope.FileName}) ne postoji u sesiji.");
        }

        var manifestSigBytes = await File.ReadAllBytesAsync(sigPath, ct).ConfigureAwait(false);
        var messageImprint = SHA256.HashData(manifestSigBytes);

        var timestampDir = Path.Combine(sessionDirectory, TimestampSubdirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(timestampDir);

        var tsrPath = Path.Combine(timestampDir, ResponseFileName);
        var tsqPath = Path.Combine(timestampDir, RequestFileName);

        // Idempotency: if valid timestamp already exists for this manifest.sig, return it
        if (File.Exists(tsrPath))
        {
            var existingTsrBytes = await File.ReadAllBytesAsync(tsrPath, ct).ConfigureAwait(false);
            byte[]? existingTsqBytes = File.Exists(tsqPath) ? await File.ReadAllBytesAsync(tsqPath, ct).ConfigureAwait(false) : null;
            var verification = Rfc3161TimestampVerifier.Verify(manifestSigBytes, existingTsrBytes, existingTsqBytes);
            if (verification.IsCryptographicallyValid)
            {
                return TimestampResult.Success(verification.Timestamp!, tsrPath, verification.State);
            }
        }

        // Generate cryptographically secure random 128-bit nonce
        var nonce = RandomNumberGenerator.GetBytes(16);

        var request = Rfc3161TimestampRequest.CreateFromHash(
            messageImprint,
            HashAlgorithmName.SHA256,
            requestSignerCertificates: true,
            nonce: nonce);

        var requestBytes = request.Encode();
        await File.WriteAllBytesAsync(tsqPath, requestBytes, ct).ConfigureAwait(false);

        // Send HTTP request to TSA
        byte[] responseBytes;
        try
        {
            var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, tsaEndpoint)
            {
                Content = new ByteArrayContent(requestBytes),
            };
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/timestamp-reply"));

            using var httpResponse = await client.SendAsync(httpRequest, ct).ConfigureAwait(false);
            if (!httpResponse.IsSuccessStatusCode)
            {
                return TimestampResult.Pending($"TSA server je vratio HTTP status {(int)httpResponse.StatusCode} ({httpResponse.ReasonPhrase}).");
            }

            responseBytes = await httpResponse.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException)
        {
            // Network failure / timeout leaves state as Pending per RFC 3161 Invariant 17
            return TimestampResult.Pending($"TSA server nije dostupan: {ex.Message}");
        }

        // Invariant 26: TIMESTAMP_RESPONSE_IS_NEVER_PUBLISHED_BEFORE_SELF_VERIFICATION
        var verificationResult = Rfc3161TimestampVerifier.Verify(manifestSigBytes, responseBytes, requestBytes);
        if (!verificationResult.IsCryptographicallyValid)
        {
            return TimestampResult.Invalid($"Primljeni TSA odgovor nije validan: {verificationResult.FailureReason}");
        }

        // Preserve offline validation certificates
        try
        {
            if (Rfc3161TimestampToken.TryDecode(responseBytes, out var token, out _))
            {
                var certsDir = Path.Combine(sessionDirectory, CertificatesDirectory.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(certsDir);

                var certificates = token.AsSignedCms().Certificates;
                for (var i = 0; i < certificates.Count; i++)
                {
                    var cert = certificates[i];
                    var certPath = Path.Combine(certsDir, i == 0 ? "tsa-signer.cer" : $"intermediate-{i}.cer");
                    await File.WriteAllBytesAsync(certPath, cert.RawData, ct).ConfigureAwait(false);
                }
            }
        }

        catch
        {
            // Preservation failure of optional cert files does not invalidate a valid cryptographic token
        }

        // Atomic write of timestamp.tsr
        var tempTsrPath = Path.Combine(timestampDir, TempResponseFileName);
        await File.WriteAllBytesAsync(tempTsrPath, responseBytes, ct).ConfigureAwait(false);

        if (File.Exists(tsrPath))
        {
            File.Delete(tsrPath);
        }

        File.Move(tempTsrPath, tsrPath);

        return TimestampResult.Success(verificationResult.Timestamp!, tsrPath, verificationResult.State);
    }
}
