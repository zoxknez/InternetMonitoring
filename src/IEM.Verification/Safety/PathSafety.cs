namespace IEM.Verification.Safety;

/// <summary>
/// Hardened path validation ensuring forensic verifier never accesses files outside the package root.
/// Invariant 29: VERIFIER_NEVER_READS_OUTSIDE_PACKAGE_ROOT.
/// </summary>
public static class PathSafety
{
    public static bool TryResolveSafeRelativePath(
        string packageRoot,
        string relativePath,
        out string resolvedFullPath,
        out string? violationReason)
    {
        resolvedFullPath = string.Empty;
        violationReason = null;

        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            violationReason = "Korenski direktorijum paketa nije naveden.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            violationReason = "Relativna putanja je prazna.";
            return false;
        }

        if (relativePath.Contains('\0'))
        {
            violationReason = "Putanja sadrži nevažeći NUL karakter.";
            return false;
        }

        // Reject absolute paths
        if (Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.StartsWith('\\') ||
            relativePath.Contains(':'))
        {
            violationReason = $"Putanja '{relativePath}' je apsolutna, što je zabranjeno u paketu dokaza.";
            return false;
        }

        // Normalize separators
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment == ".." || segment == ".")
            {
                violationReason = $"Putanja '{relativePath}' sadrži zabranjene segmente za kretanje kroz direktorijume ('{segment}').";
                return false;
            }
        }

        var fullRoot = Path.GetFullPath(packageRoot);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        var combined = Path.GetFullPath(Path.Combine(fullRoot, normalized));

        // Invariant 29: The canonical resolved path must be inside package root
        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            violationReason = $"Putanja '{relativePath}' izlazi van korenskog direktorijuma paketa.";
            return false;
        }

        resolvedFullPath = combined;
        return true;
    }
}
