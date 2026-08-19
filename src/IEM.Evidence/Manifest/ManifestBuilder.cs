using System.Security.Cryptography;
using IEM.Evidence.Canonicalization;
using IEM.Storage;

namespace IEM.Evidence.Manifest;

/// <summary>
/// Builds, validates, and atomically writes the canonical <see cref="EvidenceManifest"/> for a session.
/// </summary>
public static class ManifestBuilder
{
    private static readonly HashSet<string> ExcludedRelativePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        EvidenceManifest.FileName,
        EvidenceManifest.TempFileName,
        SignatureEnvelope.FileName,
        SignatureEnvelope.TempFileName,
        "timestamp.tsr",
        "timestamp.tsr.tmp",
    };

    /// <summary>
    /// Inventories the session directory and builds the canonical <see cref="EvidenceManifest"/>.
    /// </summary>
    public static EvidenceManifest CreateManifest(
        string sessionDirectory,
        string sessionId,
        DateTimeOffset startedUtc,
        DateTimeOffset? finishedUtc,
        string applicationVersion,
        string? platform = null,
        IReadOnlyDictionary<string, string>? providerProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        ArgumentNullException.ThrowIfNull(sessionId);

        if (!Directory.Exists(sessionDirectory))
        {
            throw new DirectoryNotFoundException($"Direktorijum sesije ne postoji: {sessionDirectory}");
        }

        var files = InventoryFiles(sessionDirectory);

        // Find raw chain info if present
        ManifestRawChainRef rawChainRef;
        var rawLogEntry = files.FirstOrDefault(f => f.RelativePath.EndsWith("sesija.log", StringComparison.OrdinalIgnoreCase));
        if (rawLogEntry is not null)
        {
            var rawLogFullPath = Path.Combine(sessionDirectory, rawLogEntry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var verification = IEM.Storage.Evidence.ChainVerifier.Verify(rawLogFullPath);
            rawChainRef = new ManifestRawChainRef(
                rawLogEntry.RelativePath,
                verification.HeadHash ?? rawLogEntry.Sha256,
                verification.EntriesChecked);

        }
        else
        {
            rawChainRef = new ManifestRawChainRef("Raw/sesija.log", string.Empty, 0);
        }

        // Find derived ledger / interpretation / legal context
        var derivedLedgerEntry = files.FirstOrDefault(f => f.RelativePath.Contains("Derived", StringComparison.OrdinalIgnoreCase) && f.RelativePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        var derivedLedgerRef = derivedLedgerEntry is not null ? new ManifestFileRef(derivedLedgerEntry.RelativePath, derivedLedgerEntry.Sha256) : null;

        var interpretationEntry = files.FirstOrDefault(f => f.RelativePath.Contains("interpretations", StringComparison.OrdinalIgnoreCase));
        var interpretationRef = interpretationEntry is not null ? new ManifestFileRef(interpretationEntry.RelativePath, interpretationEntry.Sha256) : null;

        var evidenceSummary = new ManifestEvidenceSummary(
            rawChainRef,
            derivedLedgerRef,
            interpretationRef,
            LegalContextHash: null);

        var sessionInfo = new ManifestSessionInfo(
            sessionId,
            startedUtc,
            finishedUtc,
            EvidenceSchemaVersion: 4,
            applicationVersion);

        var acquisition = new ManifestAcquisitionContext(
            platform ?? (OperatingSystem.IsWindows() ? "Windows" : (OperatingSystem.IsLinux() ? "Linux" : Environment.OSVersion.Platform.ToString())),
            providerProvenance ?? new Dictionary<string, string>());

        return new EvidenceManifest(
            EvidenceManifest.CurrentSchemaVersion,
            EvidenceManifest.CanonicalizationStandard,
            DateTimeOffset.UtcNow,
            sessionInfo,
            evidenceSummary,
            files,
            acquisition);
    }

    /// <summary>
    /// Atomically writes the canonical <see cref="EvidenceManifest"/> into the session directory per Invariants 19 &amp; 20.
    /// </summary>
    public static string WriteManifestAtomically(string sessionDirectory, EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(sessionDirectory);
        ArgumentNullException.ThrowIfNull(manifest);

        var targetPath = Path.Combine(sessionDirectory, EvidenceManifest.FileName);
        var tempPath = Path.Combine(sessionDirectory, EvidenceManifest.TempFileName);

        var canonicalBytes = manifest.ToCanonicalBytes();

        // Invariant 19 (MANIFEST_NEVER_DESCRIBES_MUTABLE_EVIDENCE): Re-verify files before commit
        foreach (var entry in manifest.Files)
        {
            var fullPath = Path.Combine(sessionDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException($"Fajl evidencije je nestao tokom izrade manifesta: {entry.RelativePath}");
            }

            var currentLength = new FileInfo(fullPath).Length;
            if (currentLength != entry.Size)
            {
                throw new InvalidOperationException($"Veličina fajla {entry.RelativePath} se promenila tokom izrade manifesta (očekivano: {entry.Size}, nađeno: {currentLength}).");
            }
        }

        // Invariant 20 (MANIFEST_IS_COMPLETE_OR_DOES_NOT_EXIST): Write to temp file then atomic replace
        File.WriteAllBytes(tempPath, canonicalBytes);

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(tempPath, targetPath);

        return targetPath;
    }

    /// <summary>
    /// Inventories all evidence files in the directory, excluding mutable exports and manifest artifacts,
    /// sorted deterministically by UTF-8 relative path.
    /// </summary>
    public static IReadOnlyList<ManifestFileEntry> InventoryFiles(string sessionDirectory)
    {
        var rootDir = new DirectoryInfo(sessionDirectory);
        var entries = new List<ManifestFileEntry>();

        foreach (var file in rootDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(sessionDirectory, file.FullName)
                .Replace('\\', '/');

            // Exclude Exports, tmp files, and manifest/sig artifacts
            if (relPath.StartsWith("Exports/", StringComparison.OrdinalIgnoreCase) ||
                relPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                ExcludedRelativePaths.Contains(relPath))
            {
                continue;
            }

            using var stream = File.OpenRead(file.FullName);
            var sha256Bytes = SHA256.HashData(stream);
            var sha256Hex = Convert.ToHexStringLower(sha256Bytes);

            entries.Add(new ManifestFileEntry(relPath, file.Length, sha256Hex));
        }

        // Deterministic sorting by relative path ordinal
        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));
        return entries;
    }
}
