using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Text.Json;
using IEM.Storage;
using Microsoft.Win32;

namespace IEM.Windows;

/// <summary>
/// Windows platform implementation of <see cref="IPlatformInstallationProbe"/>.
/// Determines whether the Windows service is registered in the SCM registry key,
/// and whether its named pipe IPC endpoint is reachable and passing the protocol handshake.
/// Invariants 276 and 282: Presence and Reachability are distinct factual determinations.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInstallationProbe : IPlatformInstallationProbe
{
    private readonly string _serviceName;
    private readonly string _pipeName;
    private readonly TimeSpan _probeTimeout;

    public WindowsInstallationProbe(
        string serviceName = ServiceContract.ServiceName,
        string pipeName = ServiceContract.StatusPipeName,
        TimeSpan? probeTimeout = null)
    {
        _serviceName = serviceName;
        _pipeName = pipeName;
        _probeTimeout = probeTimeout ?? TimeSpan.FromMilliseconds(500);
    }

    public static readonly WindowsInstallationProbe Default = new();

    public PlatformInstallationState Probe()
    {
        var presence = ProbePresence();

        if (presence == InstallationPresence.PortableOnly)
        {
            return new PlatformInstallationState(
                InstallationPresence.PortableOnly,
                ServiceReachability.NotApplicable,
                "Windows servis nije registrovan u sistemu (Portable mod).");
        }

        if (presence == InstallationPresence.Unknown)
        {
            return new PlatformInstallationState(
                InstallationPresence.Unknown,
                ServiceReachability.Unreachable,
                "Nije bilo moguće pročitati stanje registracije servisa (odbijen pristup ili greška).");
        }

        // Service is registered / installed: probe pipe reachability with protocol handshake
        var reachable = ProbePipeReachabilityWithHandshake(_pipeName, (int)_probeTimeout.TotalMilliseconds);

        return new PlatformInstallationState(
            InstallationPresence.InstalledSystemService,
            reachable ? ServiceReachability.Reachable : ServiceReachability.Unreachable,
            reachable
                ? "Windows servis je instaliran i funkcionalan (IPC handshake uspešan)."
                : "Windows servis je instaliran, ali trenutno nije pokrenut, ne odgovara ili je handshake neuspešan.");
    }

    public Task<PlatformInstallationState> ProbeAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Probe());
    }

    private InstallationPresence ProbePresence()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{_serviceName}", writable: false);

            return key is not null
                ? InstallationPresence.InstalledSystemService
                : InstallationPresence.PortableOnly;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            return InstallationPresence.Unknown;
        }
        catch (Exception)
        {
            return InstallationPresence.Unknown;
        }
    }

    /// <summary>
    /// Executes a bounded probe against the status named pipe, sending a HELLO command
    /// and validating that the service returns a valid protocol response.
    /// A successful connect without a valid response is NOT considered Reachable.
    /// </summary>
    public static bool ProbePipeReachabilityWithHandshake(string pipeName, int timeoutMs)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(timeoutMs);

            if (!client.IsConnected)
            {
                return false;
            }

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };

            var request = StatusRequest.For("HELLO").ToLine();
            writer.WriteLine(request);

            // Read response with bounded timeout
            using var cts = new CancellationTokenSource(timeoutMs);
            var readTask = reader.ReadLineAsync(cts.Token);
            readTask.AsTask().Wait(cts.Token);

            var responseLine = readTask.Result;
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return false;
            }

            var envelope = StatusEnvelope<JsonElement>.Parse(responseLine);
            if (envelope is null || !envelope.Success || envelope.IsIncompatible)
            {
                return false;
            }

            return ServiceContract.SupportsProtocol(envelope.ProtocolVersion);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
