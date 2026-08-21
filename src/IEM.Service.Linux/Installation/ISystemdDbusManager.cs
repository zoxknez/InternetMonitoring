namespace IEM.Service.Linux.Installation;

/// <summary>
/// Abstraction for querying systemd manager over D-Bus (org.freedesktop.systemd1.Manager).
/// Invariant 8E-R1-A: Enables authoritative systemd unit presence discovery and deterministic unit testing.
/// </summary>
public interface ISystemdDbusManager
{
    Task<string?> GetUnitAsync(string unitName, CancellationToken ct = default);
    Task<string?> GetUnitFileStateAsync(string unitName, CancellationToken ct = default);
}
