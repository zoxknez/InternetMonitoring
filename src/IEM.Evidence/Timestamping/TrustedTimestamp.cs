namespace IEM.Evidence.Timestamping;

/// <summary>
/// Status of RFC 3161 trusted third-party timestamp verification.
/// </summary>
public enum TrustedTimeState
{
    /// <summary>No timestamp was requested for this session.</summary>
    NotRequested,

    /// <summary>
    /// Timestamp was requested or is pending retry (e.g. offline during finalization, timeout, DNS failure).
    /// This is NOT a defect or evidence invalidity; the evidence package remains completely sealed and valid.
    /// </summary>
    Pending,

    /// <summary>Timestamp artifact exists on disk but has not been verified yet.</summary>
    PresentUnverified,

    /// <summary>
    /// Cryptographically verified and chains to a recognized trusted certificate authority.
    /// Proves the signed evidence package existed before GenTimeUtc per Invariant 17.
    /// </summary>
    ValidTrusted,

    /// <summary>
    /// Cryptographically valid CMS signature and matching message imprint, but the TSA certificate
    /// is not present in the local trust policy/root store (candidate trust anchor).
    /// </summary>
    ValidUntrusted,

    /// <summary>
    /// The timestamp artifact is corrupt, has a mismatched message imprint, invalid nonce,
    /// or failed cryptographic signature verification.
    /// </summary>
    Invalid,
}

/// <summary>
/// Structured summary of an RFC 3161 trusted timestamp token.
/// <para>
/// Invariant 17: Proves that the exact signed manifest existed no later than GenTimeUtc.
/// Does NOT prove when a network incident happened, nor does it testify to network facts.
/// </para>
/// </summary>
public sealed record TrustedTimestamp(
    DateTimeOffset GenTimeUtc,
    string MessageImprintSha256,
    TrustedTimeState State,
    string? TsaPolicyId = null,
    string? SerialNumber = null,
    TimeSpan? Accuracy = null,
    bool Ordering = false,
    string? TsaSubjectName = null,
    string? FailureReason = null)
{
    /// <summary>
    /// Serbian presentation label for evidence reports per Invariant 17.
    /// </summary>
    public string PresentationText => State switch
    {
        TrustedTimeState.ValidTrusted =>
            $"Potpisani dokazni paket postojao je najkasnije u trenutku potvrđenom vremenskim žigom ({GenTimeUtc:yyyy-MM-dd HH:mm:ss} UTC, TSA: {TsaSubjectName ?? "Povereni izdavalac"}).",
        TrustedTimeState.ValidUntrusted =>
            $"Vremenski žig je kriptografski ispravan ({GenTimeUtc:yyyy-MM-dd HH:mm:ss} UTC), ali izdavalac nije u lokalnoj listi poverenja.",
        TrustedTimeState.Pending =>
            "Vremenski žig treće strane je na čekanju (nije bilo internet veze u trenutku finalizacije).",
        TrustedTimeState.Invalid =>
            $"Vremenski žig je neispravan ({FailureReason ?? "kriptografska greška"}).",
        _ => "Vremenski žig treće strane nije priložen.",
    };
}
