using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace IEM.Service.Linux.Lifecycle.Logind;

/// <summary>
/// Managed D-Bus transport using Tmds.DBus.Protocol on the Linux system bus.
/// Listens exclusively for org.freedesktop.login1.Manager.PrepareForSleep(bool).
/// </summary>
internal sealed class TmdsLogindSignalTransport : ILogindSignalTransport
{
    private DBusConnection? _connection;
    private IDisposable? _matchSubscription;

    public async Task ObservePrepareForSleepAsync(
        Func<bool, ValueTask> handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // System bus endpoint
        var address = DBusAddress.System;
        if (string.IsNullOrEmpty(address))
        {
            throw new PlatformNotSupportedException("System bus address is not available on this platform.");
        }

        _connection = new DBusConnection(address);
        await _connection.ConnectAsync().ConfigureAwait(false);

        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = "org.freedesktop.login1",
            Path = "/org/freedesktop/login1",
            Interface = "org.freedesktop.login1.Manager",
            Member = "PrepareForSleep"
        };

        var matchSub = await _connection.AddMatchAsync(
            rule,
            (Message message, object? state) => message.GetBodyReader().ReadBool(),
            (Notification<bool> notification) =>
            {
                if (notification.Value is bool isSuspending)
                {
                    _ = handler(isSuspending);
                }
            }).ConfigureAwait(false);

        _matchSubscription = matchSub;

        // Keep running until cancellation
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = cancellationToken.Register(() => tcs.TrySetResult(true));
        await tcs.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _matchSubscription?.Dispose();
        _matchSubscription = null;

        if (_connection is not null)
        {
            _connection.Dispose();
            _connection = null;
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
