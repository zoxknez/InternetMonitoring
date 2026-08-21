namespace IEM.Linux.Composition;

/// <summary>
/// Execution mode for Linux evidence engine hosting.
/// Invariant 8E-A: Exactly two immutable production modes, with no third mode or silent fallback.
/// </summary>
public enum LinuxExecutionMode
{
    /// <summary>
    /// System service mode executing as dedicated unprivileged daemon under /var/lib/internet-evidence-monitor.
    /// </summary>
    SystemInstallation,

    /// <summary>
    /// Portable user mode executing as standard interactive user under ${XDG_STATE_HOME}/internet-evidence-monitor.
    /// </summary>
    PortableUser,
}
