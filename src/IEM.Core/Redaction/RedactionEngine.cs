using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IEM.Core.Redaction;

/// <summary>
/// Deterministic engine that derives redacted evidence packages without mutating original sources.
/// Invariants:
/// 174. REDACTION_NEVER_MUTATES_SOURCE_EVIDENCE
/// 179. SAME_SOURCE_AND_POLICY_PRODUCE_THE_SAME_REDACTED_SEMANTICS
/// 180. REDACTION_NEVER_FABRICATES_REPLACEMENT_EVIDENCE
/// 181. REMOVED_INFORMATION_NEVER_BECOMES_UNKNOWN_MEASUREMENT_DATA
/// 182. REDACTION_METADATA_NEVER_REVEALS_THE_REDACTED_VALUE
/// </summary>
public static class RedactionEngine
{
    private static readonly Regex MacRegex = new(@"\b([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})\b", RegexOptions.Compiled);
    private static readonly Regex PrivateIpRegex = new(@"\b(192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2[0-9]|3[0-1])\.\d{1,3}\.\d{1,3})\b", RegexOptions.Compiled);

    public static string RedactText(
        string content,
        RedactionPolicy policy,
        string targetPath,
        ICollection<RedactionEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(targetPath);
        ArgumentNullException.ThrowIfNull(entries);

        var result = content;

        foreach (var rule in policy.Rules)
        {
            switch (rule.FieldKind)
            {
                case RedactionFieldKind.MacAddress or RedactionFieldKind.NetworkBssid:
                    result = MacRegex.Replace(result, m =>
                    {
                        var hashBefore = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(m.Value)));
                        entries.Add(new RedactionEntry(
                            TargetPath: targetPath,
                            FieldPath: "MAC/BSSID",
                            FieldKind: rule.FieldKind,
                            Action: rule.Action,
                            Reason: rule.Reason,
                            FieldHashBefore: hashBefore));
                        return rule.MaskPattern ?? "[REDACTED-MAC]";
                    });
                    break;

                case RedactionFieldKind.PrivateIpAddress:
                    result = PrivateIpRegex.Replace(result, m =>
                    {
                        var hashBefore = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(m.Value)));
                        entries.Add(new RedactionEntry(
                            TargetPath: targetPath,
                            FieldPath: "PrivateIP",
                            FieldKind: rule.FieldKind,
                            Action: rule.Action,
                            Reason: rule.Reason,
                            FieldHashBefore: hashBefore));
                        return rule.MaskPattern ?? "[REDACTED-IP]";
                    });
                    break;
            }
        }

        return result;
    }

    public static RedactionManifest CreateRedactionManifest(
        string originalSessionId,
        string originalManifestSha256,
        RedactionPolicy policy,
        IReadOnlyList<RedactionEntry> entries,
        IReadOnlyDictionary<string, string> redactedFiles)
    {
        ArgumentNullException.ThrowIfNull(originalSessionId);
        ArgumentNullException.ThrowIfNull(originalManifestSha256);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(redactedFiles);

        return new RedactionManifest(
            SchemaVersion: 1,
            PackageId: $"redacted-pkg-{Guid.NewGuid():N}",
            OriginalSessionId: originalSessionId,
            OriginalManifestSha256: originalManifestSha256,
            RedactionPolicyId: policy.PolicyId,
            RedactionPolicyVersion: policy.PolicyVersion,
            RedactionPolicyHash: policy.PolicyHash,
            RedactedAtUtc: DateTimeOffset.UtcNow,
            RedactedEntries: entries,
            RedactedFileHashes: redactedFiles);
    }
}
