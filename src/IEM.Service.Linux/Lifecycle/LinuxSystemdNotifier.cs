using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IEM.Service.Linux.Lifecycle;

/// <summary>
/// Robust, direct systemd sd_notify implementation for Type=notify services.
/// Uses native libsystemd sd_notify as primary with zero-dependency POSIX socket fallback.
/// </summary>
public sealed class LinuxSystemdNotifier(
    ILogger<LinuxSystemdNotifier> logger,
    IHostApplicationLifetime lifetime) : IHostedService
{
    private delegate int SdNotifyFn(int unset_environment, [MarshalAs(UnmanagedType.LPStr)] string state);

    public static void SendNotify(string state, ILogger? log = null)
    {
        var socketPath = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return;
        }

        // 1. Try canonical libsystemd sd_notify via NativeLibrary
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (NativeLibrary.TryLoad("libsystemd.so.0", out var handle) ||
                    NativeLibrary.TryLoad("libsystemd.so", out handle) ||
                    NativeLibrary.TryLoad("systemd", out handle))
                {
                    if (NativeLibrary.TryGetExport(handle, "sd_notify", out var funcPtr))
                    {
                        var notifyFunc = Marshal.GetDelegateForFunctionPointer<SdNotifyFn>(funcPtr);
                        var ret = notifyFunc(0, state);
                        if (ret > 0)
                        {
                            Console.WriteLine($"[systemd] sd_notify('{state.TrimEnd()}') via libsystemd success (ret={ret})");
                            log?.LogInformation("libsystemd sd_notify('{State}') uspešno poslat (ret={Ret})", state.TrimEnd(), ret);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.LogDebug(ex, "libsystemd sd_notify attempt failed; falling back to UnixDomainSocket");
            }
        }

        // 2. Direct managed UnixDomainSocket fallback (.NET natively handles @ abstract sockets)
        try
        {
            EndPoint endpoint = new UnixDomainSocketEndPoint(socketPath);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var payload = Encoding.UTF8.GetBytes(state.EndsWith('\n') ? state : state + "\n");
            socket.SendTo(payload, endpoint);
            Console.WriteLine($"[systemd] sd_notify('{state.TrimEnd()}') via socket success to {socketPath}");
            log?.LogInformation("systemd sd_notify('{State}') poslat preko soketa na {Socket}", state.TrimEnd(), socketPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[systemd] sd_notify('{state.TrimEnd()}') error to {socketPath}: {ex.Message}");
            log?.LogWarning(ex, "Greška prilikom slanja systemd sd_notify('{State}') na {Socket}", state.TrimEnd(), socketPath);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Notify immediately on service start
        SendNotify("READY=1\nSTATUS=Monitor internet dokaza je spreman i aktivan.", logger);

        lifetime.ApplicationStarted.Register(() =>
        {
            SendNotify("READY=1\nSTATUS=Monitor internet dokaza je aktivan.", logger);
        });

        lifetime.ApplicationStopping.Register(() =>
        {
            SendNotify("STOPPING=1\nSTATUS=Zaustavljanje servisa...", logger);
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        SendNotify("STOPPING=1", logger);
        return Task.CompletedTask;
    }
}
