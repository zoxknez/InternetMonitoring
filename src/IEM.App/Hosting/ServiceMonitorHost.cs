using System.Diagnostics;
using System.IO;
using IEM.Core;
using IEM.Storage;

namespace IEM.App.Hosting;

/// <summary>
/// Watches the Windows service.
/// <para>
/// Polls rather than subscribes. A push channel would be marginally prettier and would
/// add a reconnect protocol, a backlog policy and an ordering guarantee to get right -
/// for a window that redraws once a second and whose worst failure mode is showing a
/// figure that is one second stale.
/// </para>
/// </summary>
public sealed class ServiceMonitorHost : IMonitorHost
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly ServicePipeClient _client = new();
    private readonly string _outputRoot;
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _loop;
    private string? _fault;

    public ServiceMonitorHost(string outputRoot) => _outputRoot = outputRoot;

    public HostKind Kind => HostKind.Service;

    public bool IsRunning { get; private set; }

    public event Action<MonitorSnapshot>? Updated;

    public event Action<string?>? FaultChanged;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _loop ??= Task.Run(() => PollAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a session request and starts the service.
    /// <para>
    /// Starting a service needs administrator rights, so this raises the elevation prompt
    /// once. The request itself is written by this user, which is why it has to be a file
    /// the ordinary account can create.
    /// </para>
    /// </summary>
    public async Task<bool> StartSessionAsync(TimeSpan duration, string? interfaceName, CancellationToken cancellationToken)
    {
        new SessionRequest(duration, interfaceName, DateTimeOffset.UtcNow).Write(_outputRoot);

        if (!TryControlService("start"))
        {
            SetFault("Servis nije mogao da se pokrene. Proverite da li je instaliran.");
            return false;
        }

        // Give the service a moment to open its pipe before the first poll looks for it.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task StopSessionAsync(CancellationToken cancellationToken)
    {
        // Stopping the service leaves the session open, so it resumes on the next start.
        // Deliberate: a user closing the window for the evening should not destroy a test.
        TryControlService("stop");
        return Task.CompletedTask;
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _client.QueryAsync<MonitorSnapshot>("LIVE", cancellationToken).ConfigureAwait(false);

                if (snapshot is null)
                {
                    IsRunning = false;
                    SetFault(null);
                }
                else
                {
                    IsRunning = true;
                    SetFault(null);
                    Updated?.Invoke(snapshot);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // A polling failure must not take the window down.
            catch (Exception ex)
            {
                SetFault(Describe(ex));
            }
#pragma warning restore CA1031

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

    /// <summary>Runs an elevated service control command. Returns false if the user declines.</summary>
    private static bool TryControlService(string verb)
    {
        try
        {
            var startInfo = new ProcessStartInfo("sc.exe", $"{verb} InternetEvidenceMonitor")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(startInfo);
            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Win32Exception here is almost always the user clicking No on the elevation
            // prompt, which is a decision rather than a failure.
            return false;
        }
    }

    /// <summary>
    /// Says what went wrong in the language the rest of the window is written in.
    /// <para>
    /// The exception's own message went straight onto the screen, which put "Pipe is broken."
    /// in a red banner in front of someone whose entire application is in Serbian. Worse than
    /// untranslated: it names an implementation detail instead of the thing that matters,
    /// which is that the window and the service lost touch while the test itself carried on.
    /// </para>
    /// </summary>
    private static string Describe(Exception exception) => exception switch
    {
        IOException or ObjectDisposedException =>
            "Prekinuta je veza između prozora i servisa. Nadzor se nastavlja i evidencija se " +
            "i dalje snima; prozor pokušava ponovo da se poveže.",

        TimeoutException =>
            "Servis ne odgovara na upit o stanju. Nadzor se nastavlja; prozor pokušava ponovo.",

        UnauthorizedAccessException =>
            "Nema prava pristupa servisu. Pokrenite ponovo instalaciju, koja dodeljuje prava korisnicima.",

        // Anything unforeseen still says what it means for the evidence before it says what
        // it was, because that is the part the reader needs.
        _ => $"Prozor ne može da očita stanje od servisa. Nadzor i evidencija se nastavljaju. " +
             $"Tehnički detalj: {exception.Message}",
    };

    private void SetFault(string? fault)
    {
        if (_fault == fault)
        {
            return;
        }

        _fault = fault;
        FaultChanged?.Invoke(fault);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A poll caught mid-read on a pipe the service has stopped answering cannot
                // always be cancelled through the token alone. The client is disposed below,
                // which breaks the pipe out from under the read and ends the loop on its
                // own; the process is leaving either way, and the session is the service's,
                // not ours.
            }
            catch (OperationCanceledException)
            {
                // Expected while shutting down.
            }
        }

        _shutdown.Dispose();
        _client.Dispose();
    }
}
