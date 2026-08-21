using System.Runtime.InteropServices;
using IEM.Linux.Installation;
using IEM.Storage;
using Tmds.DBus.Protocol;

namespace IEM.Service.Linux.Installation;

/// <summary>
/// Authoritative Linux system service presence source inspecting systemd Manager D-Bus truth.
/// Invariants 8E-J, 8E-R1-A, 8E-R1-R1:
/// - Determines presence strictly from systemd unit/file registration truth via D-Bus.
/// - Absence is recognized ONLY via exact D-Bus ErrorName (NoSuchUnit / NoSuchUnitFile).
/// - StateRoot and socket existence MUST NOT determine InstallationPresence.
/// - D-Bus failure, permission error, or unexpected errors fall closed to Unknown, NEVER to PortableOnly.
/// </summary>
public sealed class SystemdServicePresenceSource : ILinuxSystemServicePresenceSource
{
    public const string DefaultServiceName = "internet-evidence-monitor.service";

    private readonly string _serviceName;
    private readonly ISystemdDbusManager? _dbusManager;

    public SystemdServicePresenceSource(
        string serviceName = DefaultServiceName,
        ISystemdDbusManager? dbusManager = null)
    {
        _serviceName = serviceName;
        _dbusManager = dbusManager;
    }

    public static readonly SystemdServicePresenceSource Default = new();

    public async Task<InstallationPresence> ProbePresenceAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && _dbusManager == null)
        {
            return InstallationPresence.PortableOnly;
        }

        try
        {
            var manager = _dbusManager ?? new SystemdDbusManagerClient();

            // 1. Check if unit is actively loaded in systemd
            try
            {
                var unitPath = await manager.GetUnitAsync(_serviceName, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(unitPath))
                {
                    return InstallationPresence.InstalledSystemService;
                }
            }
            catch (DBusErrorReplyException ex) when (ex.ErrorName == SystemdDbusManagerClient.NoSuchUnitError)
            {
                // Explicit NoSuchUnit -> fall through to inspect unit file registry
            }

            // 2. Check if unit file is registered/known in systemd unit search paths
            try
            {
                var unitFileState = await manager.GetUnitFileStateAsync(_serviceName, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(unitFileState))
                {
                    // Valid unit file state (e.g. enabled, disabled, static, indirect, masked, generated, transient)
                    return InstallationPresence.InstalledSystemService;
                }

                // Explicit null / no-such-file from systemd manager
                return InstallationPresence.PortableOnly;
            }
            catch (DBusErrorReplyException ex) when (ex.ErrorName == SystemdDbusManagerClient.UnitFileNotFoundError)
            {
                return InstallationPresence.PortableOnly;
            }
        }
        catch
        {
            // D-Bus unavailable, permission denied, or protocol error -> Unknown (fail closed)
            return InstallationPresence.Unknown;
        }
    }

    public InstallationPresence ProbePresence()
    {
        try
        {
            return ProbePresenceAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return InstallationPresence.Unknown;
        }
    }
}
