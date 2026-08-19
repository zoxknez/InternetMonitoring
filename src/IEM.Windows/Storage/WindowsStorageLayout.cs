using System.Runtime.Versioning;
using IEM.Storage.Layout;

namespace IEM.Windows.Storage;

/// <summary>
/// Windows platform implementation of <see cref="IPlatformStorageLayout"/>.
/// Preserves exact 3.0 Windows output locations:
/// - Installed: %ProgramData%\InternetEvidenceMonitor\Sesije
/// - Portable:  %Desktop%\InternetEvidence
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStorageLayout : IPlatformStorageLayout
{
    public static readonly WindowsStorageLayout Instance = new();

    public string DefaultOutputRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "InternetEvidenceMonitor",
        "Sesije");

    public string PortableOutputRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "InternetEvidence");

    public string ResolveOutputRoot(bool isInstalled) =>
        isInstalled ? DefaultOutputRoot : PortableOutputRoot;

    public string GetSessionDirectory(string sessionId, bool isInstalled) =>
        Path.Combine(ResolveOutputRoot(isInstalled), $"Sesija_{sessionId}");
}
