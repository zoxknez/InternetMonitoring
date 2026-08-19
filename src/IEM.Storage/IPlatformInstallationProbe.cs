namespace IEM.Storage;

/// <summary>
/// Status of service registration/presence on this machine.
/// Invariant 282: INSTALLATION_PROBE_IS_NOT_A_BOOLEAN.
/// Canonical state model (§15A.5):
/// - <see cref="InstalledSystemService"/>: The system service is officially registered and installed.
/// - <see cref="PortableOnly"/>: The host does not have an installed service; portable execution only.
/// - <see cref="Unknown"/>: Installation state cannot be determined (e.g. permission denial, security lockdown).
/// </summary>
public enum InstallationPresence
{
    InstalledSystemService,
    PortableOnly,
    Unknown,
}

/// <summary>
/// Status of service reachability over the platform IPC transport.
/// Invariant 282: Presence and Reachability are distinct facts.
/// Canonical state model (§15A.5):
/// - <see cref="Reachable"/>: Service responds over the IPC transport and passes protocol handshake.
/// - <see cref="Unreachable"/>: Service is not answering, pipe/socket absent, or handshake fails.
/// - <see cref="NotApplicable"/>: Not applicable because the service is not installed (<see cref="InstallationPresence.PortableOnly"/>).
/// </summary>
public enum ServiceReachability
{
    Reachable,
    Unreachable,
    NotApplicable,
}

/// <summary>
/// Composite platform installation state.
/// Valid matrix combinations:
/// - PortableOnly + NotApplicable -> InProcessMonitorHost
/// - InstalledSystemService + Reachable -> ServiceMonitorHost
/// - InstalledSystemService + Unreachable -> ServiceUnavailableMonitorHost
/// - Unknown + * -> ServiceUnavailableMonitorHost (fail-closed)
/// </summary>
public sealed record PlatformInstallationState
{
    public InstallationPresence Presence { get; }
    public ServiceReachability Reachability { get; }
    public string? Detail { get; }

    public PlatformInstallationState(
        InstallationPresence presence,
        ServiceReachability reachability,
        string? detail = null)
    {
        // Enforce valid matrix combinations or normalize fail-closed
        if (presence == InstallationPresence.PortableOnly && reachability != ServiceReachability.NotApplicable)
        {
            // Invalid: PortableOnly cannot have Reachable or Unreachable reachability -> fail-closed to Unknown
            Presence = InstallationPresence.Unknown;
            Reachability = ServiceReachability.Unreachable;
            Detail = detail ?? "Nevažeća kombinacija: PortableOnly sa eksplicitnom dostupnošću servisa.";
            return;
        }

        if (presence == InstallationPresence.InstalledSystemService && reachability == ServiceReachability.NotApplicable)
        {
            // Invalid: Installed service cannot have NotApplicable reachability -> fail-closed to Unknown
            Presence = InstallationPresence.Unknown;
            Reachability = ServiceReachability.Unreachable;
            Detail = detail ?? "Nevažeća kombinacija: InstalledSystemService sa NotApplicable dostupnošću.";
            return;
        }

        Presence = presence;
        Reachability = reachability;
        Detail = detail;
    }

    public bool IsUsableService =>
        Presence == InstallationPresence.InstalledSystemService && Reachability == ServiceReachability.Reachable;

    public bool IsExplicitlyPortable =>
        Presence == InstallationPresence.PortableOnly && Reachability == ServiceReachability.NotApplicable;

    public bool IsValid =>
        (Presence == InstallationPresence.PortableOnly && Reachability == ServiceReachability.NotApplicable) ||
        (Presence == InstallationPresence.InstalledSystemService && Reachability is ServiceReachability.Reachable or ServiceReachability.Unreachable) ||
        (Presence == InstallationPresence.Unknown);
}

/// <summary>
/// Probes the local machine to determine service presence and reachability.
/// Replaces the legacy boolean IsInstalled() method.
/// </summary>
public interface IPlatformInstallationProbe
{
    PlatformInstallationState Probe();
    Task<PlatformInstallationState> ProbeAsync(CancellationToken cancellationToken = default);
}
