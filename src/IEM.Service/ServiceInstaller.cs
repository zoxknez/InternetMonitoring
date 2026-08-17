using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using IEM.Storage;

namespace IEM.Service;

/// <summary>
/// Registers and removes the Windows service.
/// <para>
/// The account is <c>NT AUTHORITY\LocalService</c> rather than LocalSystem. A monitor
/// that sends pings, reads the event log and writes files into its own folder has no need
/// for the most powerful account on the machine, and an application distributed to
/// strangers should ask for the least it can work with. Anything that genuinely needs
/// more should be isolated rather than used to justify raising the whole service.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceInstaller
{
    private const string ServiceAccount = @"NT AUTHORITY\LocalService";

    public static int Install(string outputRoot)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "Instalacija servisa zahteva administratorska prava. " +
                "Pokrenite komandnu liniju kao administrator i ponovite.");
            return 1;
        }

        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Nije moguće odrediti putanju izvršnog fajla.");
            return 1;
        }

        // The folder has to exist and be writable before the service first starts.
        // A service that silently cannot write is the worst possible failure here: the
        // customer believes a two-day test is running and ends up with nothing.
        Directory.CreateDirectory(outputRoot);

        if (!GrantWriteAccess(outputRoot))
        {
            Console.Error.WriteLine(
                $"Upozorenje: nije uspelo dodeljivanje prava upisa nalogu {ServiceAccount} " +
                $"na folder {outputRoot}. Servis možda neće moći da snima sesije.");
        }

        RegisterEventLogSource();

        var create = RunSc(
            $"create {ServiceContract.ServiceName} binPath= \"\\\"{executable}\\\"\" " +
            $"DisplayName= \"{ServiceContract.DisplayName}\" start= auto obj= \"{ServiceAccount}\"");

        if (create != 0)
        {
            Console.Error.WriteLine("Kreiranje servisa nije uspelo. Možda već postoji - probajte 'uninstall' pa ponovo.");
            return create;
        }

        RunSc($"description {ServiceContract.ServiceName} \"{ServiceContract.Description}\"");

        // Restart on failure. A monitor that quietly dies at hour thirty of a forty-eight
        // hour test is worse than one that never started, because nobody finds out until
        // the evidence is needed.
        RunSc($"failure {ServiceContract.ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/120000");

        // Without this flag the recovery actions above only fire when the process dies
        // outright. A service that catches its own fatal error, reports a clean stop and
        // returns a failure code counts as having stopped normally, so nothing restarts it
        // - which is precisely how the engine failing at hour thirty of a two-day test
        // would leave the service dead and the user none the wiser.
        RunSc($"failureflag {ServiceContract.ServiceName} 1");

        Console.WriteLine($"Servis '{ServiceContract.DisplayName}' je instaliran.");
        Console.WriteLine($"Nalog:   {ServiceAccount}");
        Console.WriteLine($"Sesije:  {outputRoot}");
        Console.WriteLine();
        Console.WriteLine($"Pokretanje:   sc start {ServiceContract.ServiceName}");
        Console.WriteLine($"Zaustavljanje: sc stop {ServiceContract.ServiceName}");
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("Uklanjanje servisa zahteva administratorska prava.");
            return 1;
        }

        // Stopping first is not optional: deleting a running service leaves it marked for
        // deletion until the next reboot, and a reinstall in that state fails confusingly.
        RunSc($"stop {ServiceContract.ServiceName}", ignoreFailure: true);

        var delete = RunSc($"delete {ServiceContract.ServiceName}");
        if (delete != 0)
        {
            Console.Error.WriteLine("Uklanjanje servisa nije uspelo.");
            return delete;
        }

        Console.WriteLine($"Servis '{ServiceContract.DisplayName}' je uklonjen.");
        Console.WriteLine("Snimljene sesije nisu obrisane.");
        return 0;
    }

    /// <summary>
    /// Creates the event log source while we still have the rights to do it.
    /// <para>
    /// Creating a source writes to the registry, which a low-privilege service account
    /// cannot do. Skipping this at install time means the service's own diagnostics go
    /// nowhere - and if it fails at three in the morning on hour thirty of a two-day test,
    /// the event log is the only place the owner would think to look.
    /// </para>
    /// </summary>
    private static void RegisterEventLogSource()
    {
        try
        {
            if (!EventLog.SourceExists(ServiceContract.ServiceName))
            {
                EventLog.CreateEventSource(ServiceContract.ServiceName, "Application");
                Console.WriteLine("Izvor za Event Log je registrovan.");
            }
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(
                $"Upozorenje: nije uspela registracija Event Log izvora ({ex.Message}). " +
                "Servis će raditi, ali njegove poruke neće biti vidljive u Event Vieweru.");
        }
    }

    /// <summary>
    /// Grants the service account write access to the session folder.
    /// <para>
    /// LocalService is a low-privilege account with no rights to ProgramData subfolders by
    /// default, so without this the service starts cleanly and then fails on its first write.
    /// </para>
    /// </summary>
    private static bool GrantWriteAccess(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            var security = info.GetAccessControl();

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            // And the people who have to use what the service writes.
            //
            // Without this the sessions are readable but not writable by the person whose
            // connection was measured: the service creates them as LocalService, and
            // rebuilding a report over an existing one then fails with access denied. That
            // is not an edge case - reopening a finished session to regenerate the report or
            // to prepare a complaint is the ordinary thing to do with it.
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            info.SetAccessControl(security);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RunSc(string arguments, bool ignoreFailure = false)
    {
        var startInfo = new ProcessStartInfo("sc.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return 1;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && !ignoreFailure)
        {
            Console.Error.WriteLine(output.Trim());
        }

        return process.ExitCode;
    }
}
