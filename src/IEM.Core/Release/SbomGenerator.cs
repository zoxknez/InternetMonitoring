using System.Security.Cryptography;
using System.Text;

namespace IEM.Core.Release;

/// <summary>
/// Generates Software Bill of Materials (SBOM) for a release.
/// Invariants:
/// 200. SBOM_IS_GENERATED_FROM_THE_RELEASE_BEING_DISTRIBUTED
/// 201. SBOM_FAILURE_NEVER_PRODUCES_A_FALSE_COMPLETE_SBOM
/// </summary>
public static class SbomGenerator
{
    public static SoftwareBillOfMaterials Generate(
        ReleaseIdentity release,
        IReadOnlyList<SbomComponent> components,
        string format = "IEM-SBOM-1")
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count == 0)
        {
            throw new InvalidOperationException("SBOM se ne može generisati bez detektovanih komponenti (Invariant 201).");
        }

        var sb = new StringBuilder();
        sb.Append($"format={format};rel={release.ProductVersion}-{release.GitCommit};components=");
        foreach (var c in components)
        {
            sb.Append($"[{c.Name}:{c.Version}:{c.Sha256Hash}];");
        }

        var sbomSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));

        return new SoftwareBillOfMaterials(
            SbomFormat: format,
            DocumentNamespace: $"https://github.com/zoxknez/InternetMonitoring/sbom/{release.ProductVersion}/{release.BuildId}",
            Release: release,
            Components: components,
            SbomSha256: sbomSha256);
    }
}
