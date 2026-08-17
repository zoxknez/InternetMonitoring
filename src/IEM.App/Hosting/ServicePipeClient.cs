using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace IEM.App.Hosting;

/// <summary>
/// Talks to the Windows service over its named pipe.
/// <para>
/// Strictly a reader. There is no command here that could stop a running session, and
/// that is deliberate: the window is a view onto the monitoring, never its owner. A user
/// who closes this application in the middle of a two-day test must not thereby lose the
/// test.
/// </para>
/// </summary>
public sealed class ServicePipeClient : IDisposable
{
    private static readonly string PipeName = IEM.Storage.ServiceContract.StatusPipeName;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Set when the service speaks a protocol this build does not, cleared on any successful
    /// exchange. Shown to the user, because "update the other half" and "the service is not
    /// running" call for entirely different actions.
    /// </summary>
    public string? Incompatibility { get; private set; }

    /// <summary>Version of the service last spoken to, for the diagnostics view.</summary>
    public string? ServiceVersion { get; private set; }

    /// <summary>
    /// Sends a command and reads the reply, reconnecting first if the pipe has dropped.
    /// Returns null when the service is not reachable.
    /// </summary>
    public async Task<T?> QueryAsync<T>(string command, CancellationToken cancellationToken)
        where T : class
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            // The version travels with every request rather than once per connection, so a
            // service replaced underneath a long-lived window is noticed on the next query
            // rather than at some point after the next reconnect.
            var request = IEM.Storage.StatusRequest.For(command).ToLine();

            await _writer!.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            var line = await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                // The service closed the pipe, most likely because its session finished
                // and it stopped itself. Drop the handles so the next call reconnects.
                Disconnect();
                return null;
            }

            var envelope = IEM.Storage.StatusEnvelope<T>.Parse(line);

            // A mismatch is reported rather than passed off as the service being absent.
            // Told "not running", a user reinstalls something that works perfectly well;
            // told the two halves are different versions, they update the other half.
            if (envelope?.IsIncompatible == true)
            {
                Incompatibility = envelope.Message
                    ?? "Interfejs i servis su različitih verzija. Ažurirajte obe strane.";

                return null;
            }

            Incompatibility = null;
            ServiceVersion = envelope?.AppVersion;

            return envelope?.Success == true ? envelope.Data : null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
        {
            Disconnect();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true)
        {
            return true;
        }

        Disconnect();

        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            // Short timeout on purpose. The service simply not running is the normal case
            // when someone opens this window, not an error worth stalling the interface for.
            await pipe.ConnectAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        return true;
    }

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();

        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }

}
