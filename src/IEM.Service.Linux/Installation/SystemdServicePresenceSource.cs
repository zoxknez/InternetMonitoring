using System.Runtime.InteropServices;
using IEM.Linux.Installation;
using IEM.Storage;

namespace IEM.Service.Linux.Installation;

/// <summary>
/// Authoritative Linux system service presence source inspecting systemd unit registration.
/// Invariant 8E-J: Determines presence strictly from systemd unit installation, never from StateRoot or socket existence.
/// </summary>
public sealed class SystemdServicePresenceSource : ILinuxSystemServicePresenceSource
{
    public const string DefaultServiceName = "internet-evidence-monitor.service";

    private readonly string _serviceName;
    private readonly string[] _unitSearchPaths;

    public SystemdServicePresenceSource(
        string serviceName = DefaultServiceName,
        string[]? unitSearchPaths = null)
    {
        _serviceName = serviceName;
        _unitSearchPaths = unitSearchPaths ?? new[]
        {
            "/etc/systemd/system",
            "/lib/systemd/system",
            "/usr/lib/systemd/system"
        };
    }

    public static readonly SystemdServicePresenceSource Default = new();

    public InstallationPresence ProbePresence()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return InstallationPresence.PortableOnly;
        }

        try
        {
            foreach (var path in _unitSearchPaths)
            {
                var unitFilePath = Path.Combine(path, _serviceName);
                if (File.Exists(unitFilePath))
                {
                    return InstallationPresence.InstalledSystemService;
                }
            }

            return InstallationPresence.PortableOnly;
        }
        catch
        {
            return InstallationPresence.Unknown;
        }
    }

    public Task<InstallationPresence> ProbePresenceAsync(CancellationToken ct = default)
    {
        return Task.FromResult(ProbePresence());
    }
}
