using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Redaction;

public sealed record RedactionRule(
    RedactionFieldKind FieldKind,
    RedactionAction Action,
    string? MaskPattern,
    string Reason);

/// <summary>
/// Versioned and hashed policy governing redactions for shareable evidence packages.
/// Invariants:
/// 178. REDACTION_POLICY_IS_VERSIONED_AND_HASH_BOUND
/// 179. SAME_SOURCE_AND_POLICY_PRODUCE_THE_SAME_REDACTED_SEMANTICS
/// 185. USER_SHARE_POLICY_NEVER_CHANGES_CANONICAL_EVIDENCE_SEMANTICS
/// </summary>
public sealed record RedactionPolicy
{
    public string PolicyId { get; init; } = "StandardPrivacy-v1";
    public int PolicyVersion { get; init; } = 1;
    public IReadOnlyList<RedactionRule> Rules { get; init; } = Array.Empty<RedactionRule>();

    public string PolicyHash => ComputePolicyHash();

    private string ComputePolicyHash()
    {
        var sb = new StringBuilder();
        sb.Append($"id={PolicyId};v={PolicyVersion};rules=");
        foreach (var r in Rules)
        {
            sb.Append($"[{r.FieldKind}:{r.Action}:{r.MaskPattern ?? ""}];");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static readonly RedactionPolicy StandardPrivacy = new()
    {
        PolicyId = "StandardPrivacy-v1",
        PolicyVersion = 1,
        Rules = new List<RedactionRule>
        {
            new(RedactionFieldKind.NetworkSsid, RedactionAction.Masked, "[REDACTED-SSID]", "Zaštita naziva kućne/poslovne Wi-Fi mreže"),
            new(RedactionFieldKind.NetworkBssid, RedactionAction.Masked, "XX:XX:XX:XX:XX:XX", "Zaštita hardverske adrese rutera"),
            new(RedactionFieldKind.MacAddress, RedactionAction.Masked, "XX:XX:XX:XX:XX:XX", "Zaštita fizičke MAC adrese mrežnog interfejsa"),
            new(RedactionFieldKind.HostName, RedactionAction.Masked, "[REDACTED-HOST]", "Zaštita imena računara"),
            new(RedactionFieldKind.UserName, RedactionAction.Masked, "[REDACTED-USER]", "Zaštita korisničkog naloga"),
            new(RedactionFieldKind.LocalPath, RedactionAction.Masked, "[REDACTED-PATH]", "Zaštita lokalnih putanja datoteka"),
            new(RedactionFieldKind.UserContractMetadata, RedactionAction.Masked, "[REDACTED-CONTRACT]", "Zaštita broja ugovora i privatnih podataka"),
            new(RedactionFieldKind.UserCustomNotes, RedactionAction.Masked, "[REDACTED-NOTE]", "Zaštita slobodnog teksta"),
        },
    };

    public static readonly RedactionPolicy StrictAnonymization = new()
    {
        PolicyId = "StrictAnonymization-v1",
        PolicyVersion = 1,
        Rules = new List<RedactionRule>
        {
            new(RedactionFieldKind.NetworkSsid, RedactionAction.Removed, null, "Potpuno uklanjanje SSID-a"),
            new(RedactionFieldKind.NetworkBssid, RedactionAction.Removed, null, "Potpuno uklanjanje BSSID-a"),
            new(RedactionFieldKind.MacAddress, RedactionAction.Removed, null, "Potpuno uklanjanje MAC adrese"),
            new(RedactionFieldKind.HostName, RedactionAction.Removed, null, "Potpuno uklanjanje imena računara"),
            new(RedactionFieldKind.UserName, RedactionAction.Removed, null, "Potpuno uklanjanje korisničkog imena"),
            new(RedactionFieldKind.LocalPath, RedactionAction.Removed, null, "Potpuno uklanjanje lokalnih putanja"),
            new(RedactionFieldKind.PrivateIpAddress, RedactionAction.Masked, "192.168.X.X", "Maskiranje lokalnog IP opsega"),
            new(RedactionFieldKind.UserContractMetadata, RedactionAction.Removed, null, "Potpuno uklanjanje ugovora"),
            new(RedactionFieldKind.UserCustomNotes, RedactionAction.Removed, null, "Potpuno uklanjanje napomena"),
        },
    };
}
