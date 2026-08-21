using IEM.Storage;

namespace IEM.Linux.Installation;

/// <summary>
/// Pre-composition platform installation probe for Linux hosts.
/// Invariants 8E-J, 276, 282:
/// - Determines composite PlatformInstallationState by combining independent presence and reachability observations.
/// - StateRoot existence MUST NOT determine InstallationPresence.
/// - InstalledSystemService + Unreachable MUST NEVER be normalized to PortableOnly.
/// - PortableOnly does not probe reachability and returns NotApplicable.
/// </summary>
public sealed class LinuxInstallationProbe : IPlatformInstallationProbe
{
    private readonly ILinuxSystemServicePresenceSource _presenceSource;
    private readonly ILinuxServiceReachabilitySource _reachabilitySource;

    public LinuxInstallationProbe(
        ILinuxSystemServicePresenceSource presenceSource,
        ILinuxServiceReachabilitySource reachabilitySource)
    {
        _presenceSource = presenceSource ?? throw new ArgumentNullException(nameof(presenceSource));
        _reachabilitySource = reachabilitySource ?? throw new ArgumentNullException(nameof(reachabilitySource));
    }

    public PlatformInstallationState Probe()
    {
        var presence = _presenceSource.ProbePresence();

        switch (presence)
        {
            case InstallationPresence.InstalledSystemService:
                var reachability = _reachabilitySource.ProbeReachability();
                return reachability switch
                {
                    ServiceReachability.Reachable => new PlatformInstallationState(
                        InstallationPresence.InstalledSystemService,
                        ServiceReachability.Reachable,
                        "Linux sistemski servis je registrovan i dostupan preko IPC transporta."),
                    ServiceReachability.Unreachable => new PlatformInstallationState(
                        InstallationPresence.InstalledSystemService,
                        ServiceReachability.Unreachable,
                        "Linux sistemski servis je registrovan ali IPC transport nije dostupan."),
                    _ => new PlatformInstallationState(
                        InstallationPresence.Unknown,
                        ServiceReachability.Unreachable,
                        "Nevažeće stanje dostupnosti za registrovani sistemski servis.")
                };

            case InstallationPresence.PortableOnly:
                return new PlatformInstallationState(
                    InstallationPresence.PortableOnly,
                    ServiceReachability.NotApplicable,
                    "Linux sistemski servis nije registrovan (PortableUser mod).");

            case InstallationPresence.Unknown:
            default:
                return new PlatformInstallationState(
                    InstallationPresence.Unknown,
                    ServiceReachability.Unreachable,
                    "Nije bilo moguće pouzdano utvrditi stanje registracije Linux servisa.");
        }
    }

    public async Task<PlatformInstallationState> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var presence = await _presenceSource.ProbePresenceAsync(cancellationToken).ConfigureAwait(false);

        switch (presence)
        {
            case InstallationPresence.InstalledSystemService:
                var reachability = await _reachabilitySource.ProbeReachabilityAsync(cancellationToken).ConfigureAwait(false);
                return reachability switch
                {
                    ServiceReachability.Reachable => new PlatformInstallationState(
                        InstallationPresence.InstalledSystemService,
                        ServiceReachability.Reachable,
                        "Linux sistemski servis je registrovan i dostupan preko IPC transporta."),
                    ServiceReachability.Unreachable => new PlatformInstallationState(
                        InstallationPresence.InstalledSystemService,
                        ServiceReachability.Unreachable,
                        "Linux sistemski servis je registrovan ali IPC transport nije dostupan."),
                    _ => new PlatformInstallationState(
                        InstallationPresence.Unknown,
                        ServiceReachability.Unreachable,
                        "Nevažeće stanje dostupnosti za registrovani sistemski servis.")
                };

            case InstallationPresence.PortableOnly:
                return new PlatformInstallationState(
                    InstallationPresence.PortableOnly,
                    ServiceReachability.NotApplicable,
                    "Linux sistemski servis nije registrovan (PortableUser mod).");

            case InstallationPresence.Unknown:
            default:
                return new PlatformInstallationState(
                    InstallationPresence.Unknown,
                    ServiceReachability.Unreachable,
                    "Nije bilo moguće pouzdano utvrditi stanje registracije Linux servisa.");
        }
    }
}
