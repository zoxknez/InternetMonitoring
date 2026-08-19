using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using IEM.Evidence.Canonicalization;
using IEM.Evidence.Crypto;
using IEM.Evidence.Manifest;
using IEM.Evidence.Timestamping;
using IEM.Storage.Evidence;
using IEM.Verification.Models;
using IEM.Verification.Safety;

namespace IEM.Verification.Engine;

/// <summary>
/// Independent, 100% platform-neutral, read-only forensic verifier for IEM evidence packages.
/// Invariants:
/// 28. VERIFIER_HAS_NO_PLATFORM_IMPLEMENTATION_DEPENDENCY
/// 29. VERIFIER_NEVER_READS_OUTSIDE_PACKAGE_ROOT
/// 30. EMBEDDED_PUBLIC_KEY_PROVES_SIGNATURE_MATCH_NOT_EXTERNAL_IDENTITY
/// 31. OFFLINE_VERIFICATION_NEVER_SILENTLY_USES_NETWORK
/// 32. VERIFICATION_IS_STRICTLY_READ_ONLY
/// </summary>
public static class PackageVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<VerificationReport> VerifyPackageAsync(

        string packageDirectory,
        VerificationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new VerificationOptions();

        if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
        {
            return new VerificationReport
            {
                Overall = OverallStatus.InputError,
                Integrity = IntegrityStatus.Invalid,
                Trust = TrustStatus.NotApplicable,
                Notes = { $"Navedeni direktorijum paketa '{packageDirectory}' ne postoji." },
            };
        }

        var notes = new List<string>();
        string? sessionId = null;

        // -------------------------------------------------------------
        // Layer 2: Manifest Verification
        // -------------------------------------------------------------
        var manifestPath = Path.Combine(packageDirectory, EvidenceManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return new VerificationReport
            {
                Overall = OverallStatus.Invalid,
                Integrity = IntegrityStatus.Invalid,
                Trust = TrustStatus.NotApplicable,
                Layers = new VerificationReport.LayerReports
                {
                    Manifest = new ManifestReport(LayerStatus.Missing, 0, 0, 0, 0, new[] { "manifest.json nedostaje u paketu." }),
                },
                Notes = { "Paket dokaza je nevažeći jer manifest.json ne postoji." },
            };
        }

        byte[] manifestRawBytes;
        using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            manifestRawBytes = new byte[stream.Length];
            var read = await stream.ReadAsync(manifestRawBytes, ct).ConfigureAwait(false);
            if (read != manifestRawBytes.Length)
            {
                return new VerificationReport
                {
                    Overall = OverallStatus.Invalid,
                    Integrity = IntegrityStatus.Invalid,
                    Trust = TrustStatus.NotApplicable,
                    Notes = { "Neuspešno čitanje datoteke manifest.json." },
                };
            }
        }

        // Canonical verification: canonicalize the parsed JSON and verify exact byte equality
        try
        {
            var manifestText = System.Text.Encoding.UTF8.GetString(manifestRawBytes);
            var canonicalBytes = JsonCanonicalizer.Canonicalize(manifestText);
            if (!canonicalBytes.SequenceEqual(manifestRawBytes))
            {
                notes.Add("UPOZORENJE: manifest.json nije u striktnom RFC 8785 kanonskom formatu.");
            }
        }
        catch (Exception ex)
        {
            return new VerificationReport
            {
                Overall = OverallStatus.Invalid,
                Integrity = IntegrityStatus.Invalid,
                Trust = TrustStatus.NotApplicable,
                Layers = new VerificationReport.LayerReports
                {
                    Manifest = new ManifestReport(LayerStatus.Invalid, 0, 0, 0, 0, new[] { $"Neispravna JSON struktura: {ex.Message}" }),
                },
            };
        }

        EvidenceManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<EvidenceManifest>(manifestRawBytes, JsonOptions);
            if (manifest is null)
            {
                throw new InvalidOperationException("Manifest se deserijalizovao u null.");
            }
        }

        catch (Exception ex)
        {
            return new VerificationReport
            {
                Overall = OverallStatus.Invalid,
                Integrity = IntegrityStatus.Invalid,
                Trust = TrustStatus.NotApplicable,
                Layers = new VerificationReport.LayerReports
                {
                    Manifest = new ManifestReport(LayerStatus.Invalid, 0, 0, 0, 0, new[] { $"Greška pri parsiranju manifesta: {ex.Message}" }),
                },
            };
        }

        sessionId = manifest.Session?.SessionId;

        if (manifest is null || manifest.Files is null)
        {
            return new VerificationReport
            {
                SessionId = sessionId,
                Overall = OverallStatus.Invalid,
                Integrity = IntegrityStatus.Invalid,
                Trust = TrustStatus.NotApplicable,
                Layers = new VerificationReport.LayerReports
                {
                    Manifest = new ManifestReport(LayerStatus.Invalid, manifest?.ManifestSchemaVersion ?? 0, 0, 0, 0,
                        new[] { "Manifest je oštećen ili ne sadrži inventar datoteka ('files')." }),
                },
            };
        }

        // Check unsupported schema version
        if (manifest.ManifestSchemaVersion > EvidenceManifest.CurrentSchemaVersion)
        {
            return new VerificationReport
            {
                SessionId = sessionId,
                Overall = OverallStatus.Unsupported,
                Integrity = IntegrityStatus.Invalid,
                Trust = TrustStatus.NotApplicable,
                Layers = new VerificationReport.LayerReports
                {
                    Manifest = new ManifestReport(LayerStatus.Unsupported, manifest.ManifestSchemaVersion, 0, 0, 0,
                        new[] { $"Verzija manifest šeme ({manifest.ManifestSchemaVersion}) je novija od podržane ({EvidenceManifest.CurrentSchemaVersion})." }),
                },
            };
        }

        // Check inventory files (size, hash, path traversal safety)
        var totalFiles = manifest.Files.Count;

        var modifiedFiles = 0;
        var missingFiles = 0;
        var violations = new List<string>();

        foreach (var fileEntry in manifest.Files)
        {
            if (!PathSafety.TryResolveSafeRelativePath(packageDirectory, fileEntry.RelativePath, out var safeFullPath, out var pathViolation))
            {
                violations.Add(pathViolation ?? $"Nevažeća putanja: {fileEntry.RelativePath}");
                modifiedFiles++;
                continue;
            }

            if (!File.Exists(safeFullPath))
            {
                violations.Add($"Nedostaje datoteka: {fileEntry.RelativePath}");
                missingFiles++;
                continue;
            }

            var fileInfo = new FileInfo(safeFullPath);
            if (fileInfo.Length != fileEntry.Size)
            {
                violations.Add($"Izmenjena veličina datoteke '{fileEntry.RelativePath}': očekivano {fileEntry.Size} B, pronađeno {fileInfo.Length} B");
                modifiedFiles++;
                continue;
            }

            using var fileStream = new FileStream(safeFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var computedSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(fileStream, ct).ConfigureAwait(false));
            if (!string.Equals(computedSha, fileEntry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Izmenjen SHA-256 heš za '{fileEntry.RelativePath}': očekivano {fileEntry.Sha256}, izračunato {computedSha}");
                modifiedFiles++;
            }
        }

        var manifestStatus = (modifiedFiles == 0 && missingFiles == 0 && violations.Count == 0)
            ? LayerStatus.Verified
            : LayerStatus.Invalid;

        var manifestReport = new ManifestReport(
            manifestStatus,
            manifest.ManifestSchemaVersion,
            totalFiles,
            modifiedFiles,
            missingFiles,
            violations);

        // -------------------------------------------------------------
        // Layer 1: Raw Chain Verification
        // -------------------------------------------------------------
        RawChainReport rawChainReport;
        if (manifest.Evidence.RawChain is not null)
        {
            if (PathSafety.TryResolveSafeRelativePath(packageDirectory, manifest.Evidence.RawChain.RelativePath, out var rawChainFullPath, out var rawPathViolation) &&
                File.Exists(rawChainFullPath))
            {
                using var rawStream = new FileStream(rawChainFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var chainVerification = ChainVerifier.Verify(rawStream);

                if (!chainVerification.Valid)
                {
                    rawChainReport = new RawChainReport(
                        LayerStatus.Invalid,
                        chainVerification.EntriesChecked,
                        chainVerification.HeadHash,
                        manifest.Evidence.RawChain.FinalChainHash,
                        $"Prekid heš lanca na liniji {chainVerification.FirstBrokenLine}: {chainVerification.Reason}");
                }
                else if (chainVerification.EntriesChecked != manifest.Evidence.RawChain.RecordCount ||
                         !string.Equals(chainVerification.HeadHash, manifest.Evidence.RawChain.FinalChainHash, StringComparison.OrdinalIgnoreCase))
                {
                    rawChainReport = new RawChainReport(
                        LayerStatus.Verified,
                        chainVerification.EntriesChecked,
                        chainVerification.HeadHash,
                        manifest.Evidence.RawChain.FinalChainHash,
                        "Heš lanac je sam po sebi ispravan, ali se ne poklapa sa deklaracijom u manifestu.");
                    violations.Add("Heš lanac ne odgovara vrednostima u manifest.json.");
                }
                else
                {
                    rawChainReport = new RawChainReport(
                        LayerStatus.Verified,
                        chainVerification.EntriesChecked,
                        chainVerification.HeadHash,
                        manifest.Evidence.RawChain.FinalChainHash);
                }
            }
            else
            {
                rawChainReport = new RawChainReport(
                    LayerStatus.Missing,
                    0,
                    null,
                    manifest.Evidence.RawChain.FinalChainHash,
                    rawPathViolation ?? "Datoteka sirove evidencije ne postoji na navedenoj putanji.");
            }
        }
        else
        {
            rawChainReport = new RawChainReport(LayerStatus.Missing, 0, null, null, "Sirova evidencija nije deklarisana u manifestu.");
        }

        // -------------------------------------------------------------
        // Layer 3: Digital Signature Verification
        // -------------------------------------------------------------
        var sigPath = Path.Combine(packageDirectory, SignatureEnvelope.FileName);
        SignatureReport signatureReport;
        byte[]? manifestSigRawBytes = null;

        if (File.Exists(sigPath))
        {
            try
            {
                using var sigStream = new FileStream(sigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                manifestSigRawBytes = new byte[sigStream.Length];
                await sigStream.ReadExactlyAsync(manifestSigRawBytes, ct).ConfigureAwait(false);

                var envelope = JsonSerializer.Deserialize<SignatureEnvelope>(manifestSigRawBytes, JsonOptions);
                if (envelope is null)
                {
                    signatureReport = new SignatureReport(LayerStatus.Invalid, "N/A", null, options.ExpectedKeyId, false, null, "Neispravna struktura manifest.sig.");
                }
                else
                {
                    var sigVerifyResult = SignatureVerifier.Verify(manifestRawBytes, envelope);
                    var isKeyMatched = false;

                    if (options.ExpectedKeyId is not null)
                    {
                        isKeyMatched = string.Equals(envelope.KeyId, options.ExpectedKeyId, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (options.TrustedKeyPath is not null && File.Exists(options.TrustedKeyPath))
                    {
                        var trustedKeyBytes = await File.ReadAllBytesAsync(options.TrustedKeyPath, ct).ConfigureAwait(false);
                        var pubKeyBytes = Convert.FromBase64String(envelope.PublicKeyBase64);
                        isKeyMatched = pubKeyBytes.SequenceEqual(trustedKeyBytes);
                    }

                    if (sigVerifyResult.IsValid)
                    {
                        signatureReport = new SignatureReport(
                            LayerStatus.Verified,
                            envelope.SignatureSuite.Algorithm + " / " + envelope.SignatureSuite.Hash,
                            envelope.KeyId,
                            options.ExpectedKeyId,
                            isKeyMatched,
                            envelope.KeyProtection);
                    }
                    else
                    {
                        signatureReport = new SignatureReport(
                            LayerStatus.Invalid,
                            envelope.SignatureSuite.Algorithm,
                            envelope.KeyId,
                            options.ExpectedKeyId,
                            isKeyMatched,
                            envelope.KeyProtection,
                            sigVerifyResult.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                signatureReport = new SignatureReport(LayerStatus.Invalid, "N/A", null, options.ExpectedKeyId, false, null, $"Greška pri čitanju potpisa: {ex.Message}");
            }
        }
        else
        {
            signatureReport = new SignatureReport(LayerStatus.Missing, "N/A", null, options.ExpectedKeyId, false, null, "manifest.sig ne postoji.");
        }


        // -------------------------------------------------------------
        // Layer 4: Trusted Timestamp Verification (RFC 3161)
        // -------------------------------------------------------------
        TimestampReport timestampReport;
        var tsrCandidates = new[]
        {
            Path.Combine(packageDirectory, "Evidence", "timestamp", "timestamp.tsr"),
            Path.Combine(packageDirectory, "timestamp.tsr"),
        };
        var tsrPath = tsrCandidates.FirstOrDefault(File.Exists);

        if (tsrPath is not null && manifestSigRawBytes is not null)
        {
            try
            {
                var tsrBytes = await File.ReadAllBytesAsync(tsrPath, ct).ConfigureAwait(false);
                var tsqPath = Path.ChangeExtension(tsrPath, ".tsq");
                byte[]? tsqBytes = File.Exists(tsqPath) ? await File.ReadAllBytesAsync(tsqPath, ct).ConfigureAwait(false) : null;

                var tsVerify = Rfc3161TimestampVerifier.Verify(manifestSigRawBytes, tsrBytes, tsqBytes, options.ExtraCertificates);
                if (tsVerify.State == TrustedTimeState.ValidTrusted)
                {
                    timestampReport = new TimestampReport(
                        LayerStatus.Verified,
                        tsVerify.Timestamp?.GenTimeUtc,
                        tsVerify.Timestamp?.TsaSubjectName,
                        tsVerify.Timestamp?.MessageImprintSha256);
                }
                else if (tsVerify.State == TrustedTimeState.ValidUntrusted)
                {
                    timestampReport = new TimestampReport(
                        LayerStatus.ValidUntrusted,
                        tsVerify.Timestamp?.GenTimeUtc,
                        tsVerify.Timestamp?.TsaSubjectName,
                        tsVerify.Timestamp?.MessageImprintSha256,
                        "Kriptografski validan, ali izdavalac nije u lokalnoj listi poverenja.");
                }
                else
                {
                    timestampReport = new TimestampReport(
                        LayerStatus.Invalid,
                        tsVerify.Timestamp?.GenTimeUtc,
                        tsVerify.Timestamp?.TsaSubjectName,
                        tsVerify.Timestamp?.MessageImprintSha256,
                        tsVerify.FailureReason ?? "Neispravan vremenski žig.");
                }
            }
            catch (Exception ex)
            {
                timestampReport = new TimestampReport(LayerStatus.Invalid, null, null, null, $"Greška pri čitanju timestamp.tsr: {ex.Message}");
            }
        }
        else
        {
            var tsqCandidates = new[]
            {
                Path.Combine(packageDirectory, "Evidence", "timestamp", "timestamp.tsq"),
                Path.Combine(packageDirectory, "timestamp.tsq"),
            };
            var isPending = tsqCandidates.Any(File.Exists);

            timestampReport = new TimestampReport(
                isPending ? LayerStatus.Pending : LayerStatus.Missing,
                null,
                null,
                null,
                isPending ? "Vremenski žig je na čekanju (Pending)." : "Vremenski žig nije priložen.");
        }

        // -------------------------------------------------------------
        // Synthesis: 2D Integrity & Trust Model
        // -------------------------------------------------------------
        IntegrityStatus integrity;
        if (manifestReport.Status == LayerStatus.Invalid ||
            rawChainReport.Status == LayerStatus.Invalid ||
            signatureReport.Status == LayerStatus.Invalid ||
            timestampReport.Status == LayerStatus.Invalid)
        {
            integrity = IntegrityStatus.Invalid;
        }
        else if (timestampReport.Status == LayerStatus.Pending ||
                 signatureReport.Status == LayerStatus.Missing ||
                 rawChainReport.Status == LayerStatus.Missing)
        {
            integrity = IntegrityStatus.Incomplete;
        }
        else
        {
            integrity = IntegrityStatus.Verified;
        }

        TrustStatus trust;
        if (integrity == IntegrityStatus.Invalid)
        {
            trust = TrustStatus.NotApplicable;
        }
        else if (signatureReport.Status == LayerStatus.Verified &&
                 (signatureReport.IsKeyMatched || options.ExpectedKeyId is null) &&
                 timestampReport.Status == LayerStatus.Verified)
        {
            trust = TrustStatus.Established;
        }
        else
        {
            trust = TrustStatus.NotEstablished;
        }

        OverallStatus overall;
        if (manifestReport.Status == LayerStatus.Unsupported)
        {
            overall = OverallStatus.Unsupported;
        }
        else if (integrity == IntegrityStatus.Invalid)
        {
            overall = OverallStatus.Invalid;
        }
        else if (integrity == IntegrityStatus.Incomplete)
        {
            overall = OverallStatus.Incomplete;
        }
        else if (trust == TrustStatus.Established)
        {
            overall = OverallStatus.Verified;
        }
        else
        {
            overall = OverallStatus.ValidTrustNotEstablished;
        }

        return new VerificationReport
        {
            SessionId = sessionId,
            Overall = overall,
            Integrity = integrity,
            Trust = trust,
            Layers = new VerificationReport.LayerReports
            {
                RawChain = rawChainReport,
                Manifest = manifestReport,
                Signature = signatureReport,
                TrustedTimestamp = timestampReport,
            },
            Notes = notes,
        };
    }
}
