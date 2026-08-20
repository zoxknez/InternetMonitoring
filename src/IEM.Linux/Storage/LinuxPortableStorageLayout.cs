using IEM.Storage.Layout;

namespace IEM.Linux.Storage;

/// <summary>
/// Linux portable user storage layout implementation.
/// Canonical hierarchy:
/// StateRoot:      ${XDG_STATE_HOME}/internet-evidence-monitor OR $HOME/.local/state/internet-evidence-monitor
/// SessionsRoot:   StateRoot/sessions
/// KeysRoot:       StateRoot/keys
/// CasesRoot:      StateRoot/cases
/// StateDataRoot:  StateRoot/state
/// DefaultOutputRoot = SessionsRoot
/// PortableOutputRoot = SessionsRoot
/// </summary>
public sealed class LinuxPortableStorageLayout : IPlatformStorageLayout
{
    private readonly string? _explicitStateRoot;
    private readonly Func<string, string?>? _getEnv;

    public bool IsAvailable => TryGetStateRoot() != null;

    public string StateRoot =>
        TryGetStateRoot() ?? throw new InvalidOperationException(
            "Portable storage location is unavailable (neither valid XDG_STATE_HOME nor HOME is set).");

    public string SessionsRoot => LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.SessionsDirName);
    public string KeysRoot => LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.KeysDirName);
    public string CasesRoot => LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.CasesDirName);
    public string StateDataRoot => LinuxStoragePaths.CombinePosix(StateRoot, LinuxStoragePaths.StateDirName);

    public string DefaultOutputRoot => SessionsRoot;
    public string PortableOutputRoot => SessionsRoot;

    public LinuxPortableStorageLayout(
        string? stateRoot = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _explicitStateRoot = stateRoot;
        _getEnv = getEnvironmentVariable;
    }

    private string? TryGetStateRoot()
    {
        if (!string.IsNullOrWhiteSpace(_explicitStateRoot))
        {
            return _explicitStateRoot;
        }
        return LinuxStoragePaths.TryResolvePortableStateRoot(_getEnv);
    }

    public string ResolveOutputRoot(bool isInstalled) => SessionsRoot;

    public string GetSessionDirectory(string sessionId, bool isInstalled) =>
        LinuxStoragePaths.CombinePosix(SessionsRoot, $"Sesija_{sessionId}");
}
