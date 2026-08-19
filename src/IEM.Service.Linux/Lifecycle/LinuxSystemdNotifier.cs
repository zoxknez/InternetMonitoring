using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IEM.Service.Linux.Lifecycle;

/// <summary>
/// Robust, direct systemd sd_notify implementation for Type=notify services.
/// Guarantees READY=1 and STOPPING=1 signals are sent to $NOTIFY_SOCKET
/// regardless of IHostLifetime container replacement quirks.
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

        try
        {
            EndPoint endpoint = socketPath.StartsWith('@')
                ? new UnixDomainSocketEndPoint("\0" + socketPath[1..])
                : new UnixDomainSocketEndPoint(socketPath);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
            var payload = Encoding.UTF8.GetBytes(state.EndsWith('\n') ? state : state + "\n");
            socket.SendTo(payload, endpoint);
            log?.LogInformation("systemd sd_notify('{State}') uspešno poslat na {Socket}", state.TrimEnd(), socketPath);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Greška prilikom slanja systemd sd_notify('{State}') na {Socket}", state.TrimEnd(), socketPath);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() =>
        {
            SendNotify("READY=1\nSTATUS=Monitor internet dokaza je spreman i aktivan.", logger);
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
