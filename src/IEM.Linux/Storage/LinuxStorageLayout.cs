using IEM.Storage.Layout;

namespace IEM.Linux.Storage;

/// <summary>
/// Linux system storage layout implementation for systemd service mode.
/// Canonical hierarchy:
/// StateRoot:      /var/lib/internet-evidence-monitor
/// SessionsRoot:   /var/lib/internet-evidence-monitor/sessions
/// KeysRoot:       /var/lib/internet-evidence-monitor/keys
/// CasesRoot:      /var/lib/internet-evidence-monitor/cases
/// StateDataRoot:  /var/lib/internet-evidence-monitor/state
/// RuntimeRoot:    /run/internet-evidence-monitor
/// DefaultOutputRoot = SessionsRoot
/// </summary>
public sealed class LinuxStorageLayout : IPlatformStorageLayout
{
    public static readonly LinuxStorageLayout Instance = new();

    public string StateRoot { get; }
    public string SessionsRoot { get; }
    public string KeysRoot { get; }
    public string CasesRoot { get; }
    public string StateDataRoot { get; }
    public string RuntimeDirectory { get; }

    public string DefaultOutputRoot => SessionsRoot;

    public string PortableOutputRoot
    {
        get
        {
            var portable = LinuxStoragePaths.TryResolvePortableStateRoot(_getEnv);
            if (portable == null)
            {
                throw new InvalidOperationException(
                    "Portable storage location is unavailable (neither valid XDG_STATE_HOME nor HOME is set).");
            }
            return LinuxStoragePaths.CombinePosix(portable, LinuxStoragePaths.SessionsDirName);
        }
    }

    private readonly Func<string, string?>? _getEnv;

    public LinuxStorageLayout(
        string? stateRoot = null,
        string? runtimeDir = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _getEnv = getEnvironmentVariable;
        StateRoot = stateRoot ?? LinuxStoragePaths.DefaultSystemStateRoot;
        RuntimeDirectory = runtimeDir ?? LinuxStoragePaths.DefaultSystemRuntimeDir;

        SessionsRoot = LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.SessionsDirName);
        KeysRoot = LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.KeysDirName);
        CasesRoot = LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.CasesDirName);
        StateDataRoot = LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.StateDirName);
    }

    public string ResolveOutputRoot(bool isInstalled) =>
        isInstalled ? DefaultOutputRoot : PortableOutputRoot;

    public string GetSessionDirectory(string sessionId, bool isInstalled) =>
        LinuxStoragePaths.CombinePosix(ResolveOutputRoot(isInstalled), $"Sesija_{sessionId}");
}
