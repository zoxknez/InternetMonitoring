using IEM.Storage.Layout;

namespace IEM.Service.Linux.Storage;

/// <summary>
/// Linux system storage layout implementation for systemd service mode.
/// Invariant 277 / Roadmap §6:
/// System mode writes exclusively to /var/lib/internet-evidence-monitor/ (StateDirectory)
/// and prepares /run/internet-evidence-monitor/ (RuntimeDirectory).
/// </summary>
public sealed class LinuxSystemStorageLayout : IPlatformStorageLayout
{
    public static readonly LinuxSystemStorageLayout Instance = new();

    public const string DefaultSystemStateDir = "/var/lib/internet-evidence-monitor";
    public const string DefaultSystemRuntimeDir = "/run/internet-evidence-monitor";

    public string DefaultOutputRoot { get; }

    public string PortableOutputRoot { get; }

    public string RuntimeDirectory { get; }

    public LinuxSystemStorageLayout(
        string? stateDir = null,
        string? runtimeDir = null,
        string? portableDir = null)
    {
        DefaultOutputRoot = stateDir ?? DefaultSystemStateDir;
        RuntimeDirectory = runtimeDir ?? DefaultSystemRuntimeDir;

        if (!string.IsNullOrWhiteSpace(portableDir))
        {
            PortableOutputRoot = portableDir;
        }
        else
        {
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdgDataHome))
            {
                PortableOutputRoot = Path.Combine(xdgDataHome, "internet-evidence-monitor");
            }
            else
            {
                var home = Environment.GetEnvironmentVariable("HOME") ?? "/tmp";
                PortableOutputRoot = Path.Combine(home, ".local", "share", "internet-evidence-monitor");
            }
        }
    }

    public string ResolveOutputRoot(bool isInstalled) =>
        isInstalled ? DefaultOutputRoot : PortableOutputRoot;

    public string GetSessionDirectory(string sessionId, bool isInstalled) =>
        Path.Combine(ResolveOutputRoot(isInstalled), $"Sesija_{sessionId}");
}
