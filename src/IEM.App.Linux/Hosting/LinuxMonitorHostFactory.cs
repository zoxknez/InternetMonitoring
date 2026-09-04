using IEM.Presentation.Hosting;

namespace IEM.App.Linux.Hosting;

internal static class LinuxMonitorHostFactory
{
    private static readonly string[] SystemdUnitPaths =
    [
        "/etc/systemd/system/internet-evidence-monitor.service",
        "/usr/lib/systemd/system/internet-evidence-monitor.service",
        "/lib/systemd/system/internet-evidence-monitor.service",
        "/usr/local/lib/systemd/system/internet-evidence-monitor.service",
    ];

    public static IMonitorHost Create()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new UnavailableMonitorHost("Linux izdanje može da se pokrene samo na Linux sistemu.");
        }

        if (File.Exists(LinuxServiceMonitorHost.DefaultSocketPath) || SystemdUnitPaths.Any(File.Exists))
        {
            return new LinuxServiceMonitorHost();
        }

        try
        {
            return new LinuxPortableMonitorHost();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new UnavailableMonitorHost(
                $"Sistemski servis nije pronađen, a prenosivi režim nije dostupan: {ex.Message}");
        }
    }
}
