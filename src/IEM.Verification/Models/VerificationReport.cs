using System.Text.Json;
using System.Text.Json.Serialization;
using IEM.Evidence.Crypto;
using IEM.Evidence.Timestamping;

namespace IEM.Verification.Models;

/// <summary>
/// Overall result of independent forensic verification of an IEM evidence package.
/// Exit codes are stable across all versions.
/// </summary>
public enum OverallStatus
{
    /// <summary>Evidence integrity verified, digital signature valid, and trust anchor established.</summary>
    Verified = 0,

    /// <summary>Evidence integrity and digital signature valid, but key identity or TSA root is not independently established.</summary>
    ValidTrustNotEstablished = 10,

    /// <summary>Package is incomplete (e.g. timestamp was pending due to network outage).</summary>
    Incomplete = 20,

    /// <summary>Package failed cryptographic or structural verification (tampered data, bad signature, wrong hash).</summary>
    Invalid = 30,

    /// <summary>Package uses an unsupported manifest schema version or algorithm.</summary>
    Unsupported = 40,

    /// <summary>Input error (directory does not exist, invalid arguments).</summary>
    InputError = 50,
}

public enum IntegrityStatus
{
    Verified,
    Incomplete,
    Invalid,
}

public enum TrustStatus
{
    Established,
    NotEstablished,
    NotApplicable,
}

public enum LayerStatus
{
    Verified,
    ValidUntrusted,
    Pending,
    Missing,
    Invalid,
    Unsupported,
}

public sealed record RawChainReport(
    LayerStatus Status,
    long RecordCount,
    string? FinalChainHash,
    string? StoredManifestHash,
    string? Details = null);

public sealed record ManifestReport(
    LayerStatus Status,
    int SchemaVersion,
    int TotalFiles,
    int ModifiedFiles,
    int MissingFiles,
    IReadOnlyList<string> Violations);

public sealed record SignatureReport(
    LayerStatus Status,
    string Algorithm,
    string? KeyId,
    string? ExpectedKeyId,
    bool IsKeyMatched,
    KeyProtectionClaim? Protection,
    string? Details = null);

public sealed record TimestampReport(
    LayerStatus Status,
    DateTimeOffset? GenTimeUtc,
    string? TsaSubject,
    string? MessageImprint,
    string? Details = null);

/// <summary>
/// Comprehensive two-dimensional verification report for an IEM evidence package.
/// </summary>
public sealed class VerificationReport
{
    [JsonPropertyName("verifierVersion")]
    public string VerifierVersion { get; init; } = "3.0.0";

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("overall")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverallStatus Overall { get; init; }

    [JsonPropertyName("exitCode")]
    public int ExitCode => (int)Overall;

    [JsonPropertyName("integrity")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IntegrityStatus Integrity { get; init; }

    [JsonPropertyName("trust")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TrustStatus Trust { get; init; }

    [JsonPropertyName("layers")]
    public LayerReports Layers { get; init; } = new();

    [JsonPropertyName("notes")]
    public List<string> Notes { get; init; } = new();

    public sealed class LayerReports
    {
        public RawChainReport? RawChain { get; set; }
        public ManifestReport? Manifest { get; set; }
        public SignatureReport? Signature { get; set; }
        public TimestampReport? TrustedTimestamp { get; set; }
    }

    public string ToConsoleReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Internet Evidence Monitor — nezavisna provera");
        sb.AppendLine();
        sb.AppendLine("Paket");
        sb.AppendLine($"  Sesija:             {SessionId ?? "Nepoznata"}");
        sb.AppendLine($"  Manifest schema:    {Layers.Manifest?.SchemaVersion ?? 0}");
        sb.AppendLine();

        // Raw chain
        sb.AppendLine($"Lanac evidencije      {FormatStatus(Layers.RawChain?.Status)}");
        sb.AppendLine($"  Zapisa:             {Layers.RawChain?.RecordCount ?? 0:N0}");
        sb.AppendLine($"  Završni otisak:     {Truncate(Layers.RawChain?.FinalChainHash, 16)}");
        sb.AppendLine();

        // Manifest
        sb.AppendLine($"Manifest              {FormatStatus(Layers.Manifest?.Status)}");
        sb.AppendLine($"  Fajlova:            {Layers.Manifest?.TotalFiles ?? 0}");
        sb.AppendLine($"  Izmenjenih:         {Layers.Manifest?.ModifiedFiles ?? 0}");
        sb.AppendLine($"  Nedostajućih:       {Layers.Manifest?.MissingFiles ?? 0}");
        sb.AppendLine();

        // Signature
        sb.AppendLine($"Digitalni potpis      {FormatStatus(Layers.Signature?.Status)}");
        sb.AppendLine($"  Algoritam:          {Layers.Signature?.Algorithm ?? "ECDSA P-256 / SHA-256"}");
        sb.AppendLine($"  Key ID:             {Truncate(Layers.Signature?.KeyId, 24)}");
        sb.AppendLine($"  Identitet ključa:   {(Layers.Signature?.IsKeyMatched == true ? "POTVRĐEN (odgovara očekivanom)" : "nije nezavisno potvrđen")}");
        sb.AppendLine();

        // Timestamp
        sb.AppendLine($"Vremenski žig         {FormatStatus(Layers.TrustedTimestamp?.Status)}");
        sb.AppendLine($"  TSA vreme:          {(Layers.TrustedTimestamp?.GenTimeUtc.HasValue == true ? Layers.TrustedTimestamp.GenTimeUtc.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "Nije dostupno")}");
        sb.AppendLine($"  Poverenje:          {(Layers.TrustedTimestamp?.Status == LayerStatus.Verified ? "Uspostavljeno" : "nije uspostavljeno")}");
        sb.AppendLine();

        sb.AppendLine("UKUPNO:");
        sb.AppendLine(FormatOverall(Overall));

        return sb.ToString();
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static string FormatStatus(LayerStatus? status) => status switch
    {
        LayerStatus.Verified => "ISPRAVAN",
        LayerStatus.ValidUntrusted => "KRIPTOGRAFSKI ISPRAVAN",
        LayerStatus.Pending => "NA ČEKANJU",
        LayerStatus.Missing => "NEDOSTAJE",
        LayerStatus.Unsupported => "NEPODRŽAN",
        _ => "NEISPRAVAN",
    };

    private static string FormatOverall(OverallStatus overall) => overall switch
    {
        OverallStatus.Verified => "VERIFIED",
        OverallStatus.ValidTrustNotEstablished => "VALID — TRUST NOT ESTABLISHED",
        OverallStatus.Incomplete => "INCOMPLETE",
        OverallStatus.Unsupported => "UNSUPPORTED",
        OverallStatus.InputError => "INPUT_ERROR",
        _ => "INVALID",
    };

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "N/A";
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
