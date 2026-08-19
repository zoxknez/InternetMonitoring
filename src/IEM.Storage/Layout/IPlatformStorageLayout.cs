namespace IEM.Storage.Layout;

/// <summary>
/// Platform-specific storage layout contract providing root paths for installed and portable execution.
/// Invariant 67 / Invariant 277: Platform path resolution is decoupled from canonical storage logic.
/// </summary>
public interface IPlatformStorageLayout
{
    /// <summary>System/installed service session output root.</summary>
    string DefaultOutputRoot { get; }

    /// <summary>Portable in-process session output root.</summary>
    string PortableOutputRoot { get; }

    /// <summary>Resolves the appropriate output root based on whether the host runs as installed service.</summary>
    string ResolveOutputRoot(bool isInstalled);

    /// <summary>Resolves the session directory path for a given session identifier.</summary>
    string GetSessionDirectory(string sessionId, bool isInstalled);
}
