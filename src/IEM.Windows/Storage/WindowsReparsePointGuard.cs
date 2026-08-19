using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace IEM.Windows.Storage;

/// <summary>
/// Guard against junction, symlink, and reparse point attacks on Windows.
/// Invariant 77: PRIVILEGED_EVIDENCE_WRITES_NEVER_FOLLOW_UNTRUSTED_REPARSE_POINTS.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsReparsePointGuard
{
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;
    private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFileAttributesW(string lpFileName);

    /// <summary>
    /// Checks if a file or directory is a reparse point (junction, mount point, or symbolic link).
    /// </summary>
    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var attr = GetFileAttributesW(path);
        if (attr == INVALID_FILE_ATTRIBUTES)
        {
            return false;
        }

        return (attr & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
    }

    /// <summary>
    /// Validates that no directory segment along the path from sessionRoot to targetPath is a reparse point.
    /// </summary>
    public static bool ValidateNoReparsePointsAlongPath(string sessionRoot, string targetPath, out string? violation)
    {
        violation = null;
        var fullTarget = Path.GetFullPath(targetPath);
        var fullRoot = Path.GetFullPath(sessionRoot);

        if (IsReparsePoint(fullRoot))
        {
            violation = $"Koren sesije '{sessionRoot}' je reparse point (junction/symlink).";
            return false;
        }

        var current = fullTarget;
        while (!string.IsNullOrEmpty(current) && current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                if (IsReparsePoint(current))
                {
                    violation = $"Detektovan reparse point (junction/symlink) na putanji: '{current}'.";
                    return false;
                }
            }

            var parent = Path.GetDirectoryName(current);
            if (parent == current || string.IsNullOrEmpty(parent))
            {
                break;
            }
            current = parent;
        }

        return true;
    }
}
