using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using IEM.Core;
using IEM.Core.Ipc;
using IEM.Presentation.Hosting;
using IEM.Service.Runtime;

namespace IEM.App.Linux.Hosting;

internal sealed class LinuxServiceMonitorHost : IMonitorHost
{
    public const string DefaultSocketPath = "/run/internet-evidence-monitor/control.sock";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly LinuxIpcClient _client;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pollLoop;
    private string? _sessionId;
    private string? _fault;

    public LinuxServiceMonitorHost(string socketPath = DefaultSocketPath) =>
        _client = new LinuxIpcClient(socketPath);

    public HostKind Kind => HostKind.Service;
    public bool IsRunning { get; private set; }

    public event Action<MonitorSnapshot>? Updated;
    public event Action<string?>? FaultChanged;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _pollLoop ??= Task.Run(() => PollAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<bool> StartSessionAsync(
        TimeSpan duration,
        string? interfaceName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.SendAsync(
                "StartSession",
                sessionId: null,
                new StartSessionCommandPayload
                {
                    Duration = FormatDuration(duration),
                    InterfaceName = interfaceName,
                },
                cancellationToken).ConfigureAwait(false);

            if (response.Status != IpcResponseStatus.Success)
            {
                SetFault(response.ErrorMessage ?? "Servis je odbio pokretanje sesije.");
                return false;
            }

            _sessionId = response.SessionId;
            IsRunning = true;
            SetFault(null);
            return true;
        }
        catch (Exception ex) when (IsExpectedConnectionFailure(ex))
        {
            SetFault(Describe(ex));
            return false;
        }
    }

    public async Task StopSessionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
        {
            return;
        }

        try
        {
            var response = await _client.SendAsync(
                "FinalizeSession",
                _sessionId,
                payload: null,
                cancellationToken).ConfigureAwait(false);

            if (response.Status != IpcResponseStatus.Success)
            {
                SetFault(response.ErrorMessage ?? "Servis nije prihvatio završavanje sesije.");
                return;
            }

            SetFault(null);
        }
        catch (Exception ex) when (IsExpectedConnectionFailure(ex))
        {
            SetFault(Describe(ex));
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await _client.SendAsync(
                    "GetServiceStatus",
                    sessionId: null,
                    payload: null,
                    cancellationToken).ConfigureAwait(false);
                var wire = LinuxIpcClient.DeserializePayload<ServiceStatusWire>(response);

                _sessionId = wire.Status.SessionId;
                IsRunning = wire.Status.State is SessionState.Running or SessionState.Finalizing;
                SetFault(wire.Status.Fault);
                Updated?.Invoke(wire.Snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsExpectedConnectionFailure(ex))
            {
                IsRunning = false;
                SetFault(Describe(ex));
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SetFault(string? fault)
    {
        if (string.Equals(_fault, fault, StringComparison.Ordinal))
        {
            return;
        }

        _fault = fault;
        FaultChanged?.Invoke(fault);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration == Timeout.InfiniteTimeSpan)
        {
            return "infinite";
        }

        return duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    private static string Describe(Exception exception) => exception switch
    {
        LinuxIpcException ipc => ipc.Message,
        UnauthorizedAccessException =>
            "Nemate pravo pristupa servisu. Proverite članstvo u grupi iem-users i ponovo se prijavite.",
        SocketException =>
            "Sistemski servis trenutno nije dostupan. Proverite: systemctl status internet-evidence-monitor.",
        OperationCanceledException => "Servis nije odgovorio na vreme.",
        _ => $"Veza sa servisom nije uspela: {exception.Message}",
    };

    private static bool IsExpectedConnectionFailure(Exception exception) =>
        exception is IOException or SocketException or OperationCanceledException or
            UnauthorizedAccessException or LinuxIpcException or JsonException;

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_pollLoop is not null)
        {
            try
            {
                await _pollLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _shutdown.Dispose();
    }

    private sealed record ServiceStatusWire(
        ServiceStatus Status,
        SpeedStatus SpeedStatus,
        MonitorSnapshot Snapshot);
}
