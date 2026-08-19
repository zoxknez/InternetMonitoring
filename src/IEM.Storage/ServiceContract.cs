namespace IEM.Storage;

/// <summary>
/// Names and protocol constants shared between the service, the interface and the console.
/// Platform-neutral contract. Platform layout and installation detection are handled
/// via <see cref="IPlatformInstallationProbe"/> and <see cref="Layout.IPlatformStorageLayout"/>.
/// </summary>
public static class ServiceContract
{
    public const string ServiceName = "InternetEvidenceMonitor";

    public const string DisplayName = "Monitor internet dokaza";

    public const string Description =
        "Beleži prekide i kvalitet internet veze i priprema dokumentaciju za prigovor operateru. " +
        "Radi bez otvorenog prozora i nastavlja rad nakon restarta.";

    /// <summary>Named pipe the service publishes its status on.</summary>
    public const string StatusPipeName = "InternetEvidenceMonitor.status";

    /// <summary>
    /// Version of the request and response shapes on that pipe.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Whether this end can talk to a peer speaking <paramref name="theirVersion"/>.
    /// </summary>
    public static bool SupportsProtocol(int theirVersion) => theirVersion == ProtocolVersion;

    /// <summary>Version of the application, as reported over the pipe.</summary>
    public static string AppVersion { get; } =
        typeof(ServiceContract).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
