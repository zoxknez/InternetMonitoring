using Microsoft.Win32;

namespace IEM.Storage;

/// <summary>
/// Names and locations shared between the service, the interface and the console.
/// <para>
/// Kept here rather than in the service project on purpose. The interface needs to know
/// the service's name, its pipe and where it writes sessions - but it has no business
/// referencing the hosting stack to find that out, and duplicating the strings would mean
/// a rename silently breaking the connection between them.
/// </para>
/// </summary>
public static class ServiceContract
{
    public const string ServiceName = "InternetEvidenceMonitor";

    public const string DisplayName = "Internet Monitoring";

    public const string Description =
        "Beleži prekide i kvalitet internet veze i priprema dokumentaciju za prigovor operateru. " +
        "Radi bez otvorenog prozora i nastavlja rad nakon restarta.";

    /// <summary>Named pipe the service publishes its status on.</summary>
    public const string StatusPipeName = "InternetEvidenceMonitor.status";

    /// <summary>
    /// Version of the request and response shapes on that pipe.
    /// <para>
    /// Versioned from the outset because the two ends update separately: the service is
    /// replaced by an installer that needs administrator rights, the interface by whatever
    /// the user runs. A 2.3 window talking to a 2.2 service is the ordinary state of affairs
    /// for as long as it takes someone to get round to the second half of an update, and it
    /// has to say so plainly rather than fail in a way that looks like the service is down.
    /// </para>
    /// <para>
    /// Raised only when an existing field changes meaning or disappears. Adding a field does
    /// not raise it: readers ignore what they do not recognise.
    /// </para>
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Whether this end can talk to a peer speaking <paramref name="theirVersion"/>.
    /// <para>
    /// Equality for now, deliberately. A range would be a guess about compatibility nobody
    /// has tested; when a version 2 exists and is genuinely backward compatible, this is the
    /// single place that says so.
    /// </para>
    /// </summary>
    public static bool SupportsProtocol(int theirVersion) => theirVersion == ProtocolVersion;

    /// <summary>Version of the application, as reported over the pipe.</summary>
    public static string AppVersion { get; } =
        typeof(ServiceContract).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Where the service writes sessions.
    /// <para>
    /// Under ProgramData rather than a user's profile: the service runs without a
    /// logged-in user, and a profile that may not be loaded is how a background service
    /// ends up silently recording nothing.
    /// </para>
    /// </summary>
    public static string DefaultOutputRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "InternetEvidenceMonitor",
        "Sesije");

    /// <summary>Where a session goes when nothing is installed and the app records on its own.</summary>
    public static string PortableOutputRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "InternetEvidence");

    /// <summary>
    /// Whether the service is registered on this machine.
    /// <para>
    /// Read from the registry rather than through the service control APIs, which would
    /// mean another package for a single boolean. The key is readable by ordinary users,
    /// so this works without elevation - which matters, because the interface must never
    /// need administrator rights just to decide how to display itself.
    /// </para>
    /// </summary>
    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ServiceName}", writable: false);

            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // A machine locked down enough to refuse this read is one where assuming the
            // service is absent is the safe answer: the app then records on its own.
            return false;
        }
    }
}
