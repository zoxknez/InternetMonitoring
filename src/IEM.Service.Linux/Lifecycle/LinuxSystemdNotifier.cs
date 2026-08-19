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
    public static void SendNotify(string state, ILogger? log = null)
    {
        var socketPath = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return;
        }

        // 1. Try canonical libsystemd.so.0 sd_notify
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                var ret = sd_notify(0, state);
                if (ret > 0)
                {
                    log?.LogInformation("libsystemd sd_notify('{State}') uspešno poslat (ret={Ret})", state.TrimEnd(), ret);
                    return;
                }
            }
            catch
            {
                // Fallback to direct managed socket
            }
        }

        // 2. Direct managed socket fallback
        try
        {
            var cleanPath = socketPath.StartsWith('@') ? "\0" + socketPath[1..] : socketPath;
            EndPoint endpoint = new UnixDomainSocketEndPoint(cleanPath);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var payload = Encoding.UTF8.GetBytes(state.EndsWith('\n') ? state : state + "\n");
            socket.SendTo(payload, endpoint);
            log?.LogInformation("systemd sd_notify('{State}') poslat preko soketa na {Socket}", state.TrimEnd(), socketPath);
        }
        catch (Exception ex)
        {
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

    [DllImport("libsystemd.so.0", EntryPoint = "sd_notify", SetLastError = true)]
    private static extern int sd_notify(int unset_environment, [MarshalAs(UnmanagedType.LPStr)] string state);
}
