using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using IEM.Storage;

namespace IEM.Service;

/// <summary>
/// Publishes the service's status over a local named pipe.
/// <para>
/// The relationship is deliberately one-way: an interface is a reader of the monitoring,
/// never its owner. Closing a window, logging out, or crashing the interface has no
/// effect on a test in progress, which is the entire reason monitoring lives in a service.
/// </para>
/// <para>
/// The protocol is one JSON object per line, request then response. Plain enough to drive
/// from a script or read with a text editor while diagnosing, and extensible without
/// breaking older clients: unknown commands are answered, not dropped.
/// </para>
/// </summary>
public sealed class StatusPipeServer(
    MonitorWorker worker,
    SpeedWorker speedWorker,
    ILogger<StatusPipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            logger.LogDebug("Kanal za status je dostupan samo na Windows-u.");
            return;
        }

        // Several listeners wait at once, each accepting and serving independently.
        //
        // One listener is not enough even when serving is concurrent, and the difference is
        // easy to miss: between accepting a connection and creating the replacement listener
        // there is an instant with nothing listening at all, and a client arriving in it is
        // simply refused. With the window, the tray icon and a console check all polling,
        // that instant gets hit - and a refused connection is indistinguishable from the
        // service not running, which sends people off to reinstall something that works.
        var listeners = Enumerable
            .Range(0, ListenerCount)
            .Select(index => ListenAsync(index == 0, stoppingToken))
            .ToArray();

        await Task.WhenAll(listeners).ConfigureAwait(false);
    }

    /// <summary>
    /// How many listeners wait at once. Must not exceed the instance limit the pipe was
    /// created with, or the surplus fail to open.
    /// </summary>
    private const int ListenerCount = 4;

    /// <summary>Signalled once the instance carrying the security descriptor exists.</summary>
    private readonly TaskCompletionSource _securityEstablished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="ownsSecurity">
    /// Whether this listener is the one that defines who may reach the pipe.
    /// <para>
    /// Only one instance of a named pipe may carry a security descriptor; the others inherit
    /// it. Having every listener try to set it produced an access-denied exception on each
    /// of the rest, every two seconds, for the whole length of a session - a log full of
    /// alarming errors, and fewer listeners than intended behind them.
    /// </para>
    /// </param>
    [SupportedOSPlatform("windows")]
    private async Task ListenAsync(bool ownsSecurity, CancellationToken stoppingToken)
    {
        if (!ownsSecurity)
        {
            // Waits for the first instance to exist. Creating one before it would define the
            // pipe without a descriptor, and the person whose connection is being measured
            // could then not read their own status - a failure that looks like the service
            // being down and would depend on which task happened to start first.
            await _securityEstablished.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The listener owns the instance for its whole life, including the paths
                // that never reach a client.
                //
                // Disposing it only after a connection was served leaked one pipe instance
                // every time WaitForConnectionAsync threw - and the pipe is created with a
                // hard limit of ListenerCount instances. Enough failures in a row and every
                // instance is spoken for by an abandoned one, CreatePipe then fails with
                // "all pipe instances are busy", and the status channel stays dead for the
                // rest of the session while the monitoring behind it runs on. The window
                // reports that as a lost connection to the service, which points suspicion
                // at the one component that was working.
                await using var pipe = CreatePipe(ownsSecurity);

                if (ownsSecurity)
                {
                    _securityEstablished.TrySetResult();
                }

                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);

                // Served on this loop rather than handed off: the other listeners are
                // already waiting, so nothing is lost by this one being busy for a moment.
                await ServeAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Losing the status channel must never stop the monitoring.
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Greška na kanalu za status. Kanal se ponovo otvara.");

                // Back off rather than spinning: if the pipe cannot be created at all,
                // retrying flat out would burn a core for the length of the test.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
#pragma warning restore CA1031
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        try
        {
            await ConverseAsync(pipe, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // One client misbehaving must not take the channel down.
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Veza sa jednim klijentom je prekinuta.");
        }
#pragma warning restore CA1031
        finally
        {
            // Disconnected, not disposed: the listener owns the instance and disposes it on
            // every path, including the ones that never got here.
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task ConverseAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
        };

        while (!stoppingToken.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            await writer.WriteLineAsync(Handle(line).ToLine()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates the pipe with an access rule for the interactive user.
    /// <para>
    /// The service runs under a service account, and a pipe created with default security
    /// would be reachable only by that account - so the person whose connection is being
    /// measured could not read their own status. Access is granted to interactive users
    /// specifically rather than to everyone: this is local diagnostic data about someone's
    /// home network, and it has no business being readable by every account on the machine.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreatePipe(bool ownsSecurity)
    {
        if (!ownsSecurity)
        {
            // Inherits the descriptor the first instance set. Attempting to set it again
            // here is refused, which is what filled the log with access-denied errors.
            return new NamedPipeServerStream(
                ServiceContract.StatusPipeName,
                PipeDirection.InOut,
                ListenerCount,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // The account this service actually runs under.
        //
        // Its absence was invisible while there was only one listener: the process that
        // creates a pipe may always use that first instance, whatever the descriptor says.
        // Every additional instance is checked against it - so as soon as more than one
        // listener was kept waiting, the rest were denied access to a pipe this very
        // process had just created, retrying and failing every two seconds for the length
        // of a session.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // And whoever is actually running this process, when that is somebody else.
        //
        // The rule above names LocalService because that is the account the installer
        // registers the service under - which quietly made it the only account it works
        // under. Run the same executable from a console to diagnose something, or install
        // it under another account, and the first listener is created while every other
        // one is refused: creating a further instance of an existing pipe needs
        // CreateNewInstance, and the interactive rule below grants only read and write.
        // The result is the exact failure this block was written to prevent, one account
        // over - an access-denied warning every two seconds for the length of the session,
        // and a status channel served by a single listener.
        //
        // Found by running the service in a console under an ordinary account, 17.08.
        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            security.AddAccessRule(new PipeAccessRule(
                self,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            ServiceContract.StatusPipeName,
            PipeDirection.InOut,
            // Matches the number of listeners kept waiting. More than one, because the
            // window, the tray icon and a console check all poll this, and a client that
            // finds no listener free cannot tell that apart from the service being down.
            maxNumberOfServerInstances: ListenerCount,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    /// <summary>
    /// Answers one request.
    /// <para>
    /// The parsing and the wire shape live in <see cref="StatusProtocol"/>, shared with the
    /// window and testable without a pipe; what is left here is the one thing only the
    /// service can answer - what it is currently doing.
    /// </para>
    /// </summary>
    private StatusResponse Handle(string line)
    {
        var request = StatusProtocol.ParseRequest(line);

        // A client that states a version it does not share is told so plainly. Left to fail
        // on a field it does not recognise, it would report the service as unreachable - and
        // send the user to reinstall something that is working perfectly well.
        if (!request.SpeaksOurProtocol)
        {
            return StatusResponse.Refused(request.IncompatibilityMessage);
        }

        return request.Normalised switch
        {
            "STATUS" => StatusResponse.Ok(worker.Status),
            "LIVE" => StatusResponse.Ok(worker.Live),
            "PING" => StatusResponse.Ok(new { pong = true }),

            // Where a scheduled measurement stands.
            "SPEED" => StatusResponse.Ok(speedWorker.Status),

            // Lets a client establish what it is talking to before sending anything that
            // depends on the answer.
            "HELLO" => StatusResponse.Ok(new
            {
                protocolVersion = ServiceContract.ProtocolVersion,
                appVersion = ServiceContract.AppVersion,
                commands = StatusProtocol.Commands,
            }),

            null or "" => StatusResponse.Error("Prazna komanda."),
            _ => StatusResponse.Unknown(request.Command),
        };
    }
}
