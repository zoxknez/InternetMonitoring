using IEM.Storage;

namespace IEM.Linux.Installation;

/// <summary>
/// Authoritative source for Linux system service installation presence truth.
/// Invariant 8E-J: Presence is derived from systemd / service registration truth, NEVER from StateRoot existence.
/// </summary>
public interface ILinuxSystemServicePresenceSource
{
    InstallationPresence ProbePresence();
    Task<InstallationPresence> ProbePresenceAsync(CancellationToken ct = default);
}
