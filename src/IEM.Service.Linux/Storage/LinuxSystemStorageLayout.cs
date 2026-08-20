using IEM.Linux.Storage;
using IEM.Storage.Layout;

namespace IEM.Service.Linux.Storage;

/// <summary>
/// Backward-compatible wrapper for <see cref="LinuxStorageLayout"/>.
/// Invariant 277 / Phase 3.1-8A:
/// System mode writes sessions to /var/lib/internet-evidence-monitor/sessions
/// and prepares /run/internet-evidence-monitor/ (RuntimeDirectory).
/// </summary>
public sealed class LinuxSystemStorageLayout : IPlatformStorageLayout
{
    public static readonly LinuxSystemStorageLayout Instance = new();

    public const string DefaultSystemStateDir = LinuxStoragePaths.DefaultSystemStateRoot;
    public const string DefaultSystemRuntimeDir = LinuxStoragePaths.DefaultSystemRuntimeDir;

    private readonly LinuxStorageLayout _underlying;

    public string DefaultOutputRoot => _underlying.DefaultOutputRoot;

    public string PortableOutputRoot => _underlying.PortableOutputRoot;

    public string RuntimeDirectory => _underlying.RuntimeDirectory;

    public LinuxSystemStorageLayout(
        string? stateDir = null,
        string? runtimeDir = null,
        string? portableDir = null)
    {
        _underlying = new LinuxStorageLayout(
            stateRoot: stateDir,
            runtimeDir: runtimeDir,
            getEnvironmentVariable: !string.IsNullOrWhiteSpace(portableDir)
                ? key => key == "XDG_STATE_HOME" ? portableDir : Environment.GetEnvironmentVariable(key)
                : null);
    }

    public string ResolveOutputRoot(bool isInstalled) => _underlying.ResolveOutputRoot(isInstalled);

    public string GetSessionDirectory(string sessionId, bool isInstalled) => _underlying.GetSessionDirectory(sessionId, isInstalled);
}
