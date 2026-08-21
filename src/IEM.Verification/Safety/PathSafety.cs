namespace IEM.Verification.Safety;

/// <summary>
/// Hardened path validation ensuring forensic verifier never accesses files outside the package root.
/// Invariant 29: VERIFIER_NEVER_READS_OUTSIDE_PACKAGE_ROOT.
/// Policy: Rejects absolute paths, directory traversals, and any symbolic links or reparse points.
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

        // Canonical lexical containment check (Ordinal for Linux, OrdinalIgnoreCase for Windows)
        var stringComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(fullRoot, stringComparison))
        {
            violationReason = $"Putanja '{relativePath}' izlazi van korenskog direktorijuma paketa.";
            return false;
        }

        // Physical containment: reject any symbolic link, junction, or reparse point along the path
        var currentWalk = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var segment in segments)
        {
            currentWalk = Path.Combine(currentWalk, segment);

            if (File.Exists(currentWalk))
            {
                var fileInfo = new FileInfo(currentWalk);
                if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0 || fileInfo.LinkTarget != null)
                {
                    violationReason = $"Putanja '{relativePath}' sadrži simbolički link ili reparse tačku ('{segment}'), što je zabranjeno u paketu dokaza.";
                    return false;
                }
            }
            else if (Directory.Exists(currentWalk))
            {
                var dirInfo = new DirectoryInfo(currentWalk);
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0 || dirInfo.LinkTarget != null)
                {
                    violationReason = $"Putanja '{relativePath}' sadrži direktorijumski spoj (junction) ili reparse tačku ('{segment}'), što je zabranjeno u paketu dokaza.";
                    return false;
                }
            }
        }

        resolvedFullPath = combined;
        return true;
    }
}
