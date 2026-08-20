namespace IEM.Linux.Storage;

/// <summary>
/// Canonical Linux storage paths and directory names.
/// Invariant 277 / Phase 3.1-8A:
/// - System StateDirectory: /var/lib/internet-evidence-monitor
/// - System SessionsRoot:   /var/lib/internet-evidence-monitor/sessions
/// - System KeysRoot:       /var/lib/internet-evidence-monitor/keys
/// - System CasesRoot:      /var/lib/internet-evidence-monitor/cases
/// - System StateDataRoot:  /var/lib/internet-evidence-monitor/state
/// - System RuntimeDir:     /run/internet-evidence-monitor
/// - Portable StateRoot:    ${XDG_STATE_HOME}/internet-evidence-monitor or $HOME/.local/state/internet-evidence-monitor
/// - MUST NEVER use XDG_DATA_HOME or $HOME/.local/share.
/// - MUST NEVER fallback to /tmp.
/// </summary>
public static class LinuxStoragePaths
{
    public const string DefaultSystemStateRoot = "/var/lib/internet-evidence-monitor";
    public const string DefaultSystemSessionsRoot = "/var/lib/internet-evidence-monitor/sessions";
    public const string DefaultSystemKeysRoot = "/var/lib/internet-evidence-monitor/keys";
    public const string DefaultSystemCasesRoot = "/var/lib/internet-evidence-monitor/cases";
    public const string DefaultSystemStateDataRoot = "/var/lib/internet-evidence-monitor/state";
    public const string DefaultSystemRuntimeDir = "/run/internet-evidence-monitor";

    public const string AppDirectoryName = "internet-evidence-monitor";
    public const string SessionsDirName = "sessions";
    public const string KeysDirName = "keys";
    public const string CasesDirName = "cases";
    public const string StateDirName = "state";
    public const string SigningKeyFileName = "evidence-signing-v1.p8";

    /// <summary>
    /// Joins POSIX path segments deterministically using '/' separator across all operating systems.
    /// </summary>
    public static string CombinePosix(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath)) return relativePath;
        if (string.IsNullOrEmpty(relativePath)) return basePath;
        return $"{basePath.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    /// <summary>
    /// Attempts to resolve the canonical portable state root directory.
    /// Invariant:
    /// 1. Uses XDG_STATE_HOME if set, non-empty, and valid absolute path.
    /// 2. Else uses $HOME/.local/state/internet-evidence-monitor if HOME is set, non-empty, and valid absolute path.
    /// 3. MUST NEVER use XDG_DATA_HOME or $HOME/.local/share.
    /// 4. MUST NEVER fallback to /tmp or any other directory.
    /// 5. Returns null if neither valid XDG_STATE_HOME nor HOME is available (fail-closed).
    /// </summary>
    public static string? TryResolvePortableStateRoot(
        Func<string, string?>? getEnvironmentVariable = null)
    {
        var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        // 1. Check XDG_STATE_HOME
        var xdgStateHome = getEnv("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdgStateHome) &&
            !xdgStateHome.Contains('\0') &&
            IsPosixOrRootedPath(xdgStateHome))
        {
            return CombinePosix(xdgStateHome, AppDirectoryName);
        }

        // 2. Check HOME
        var home = getEnv("HOME");
        if (!string.IsNullOrWhiteSpace(home) &&
            !home.Contains('\0') &&
            IsPosixOrRootedPath(home))
        {
            return CombinePosix(CombinePosix(home, ".local/state"), AppDirectoryName);
        }

        // Fail closed - no /tmp fallback
        return null;
    }

    private static bool IsPosixOrRootedPath(string path)
    {
        return path.StartsWith('/') || Path.IsPathRooted(path);
    }
}
