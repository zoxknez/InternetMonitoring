using IEM.Core.Time;

namespace IEM.Core.Probes;

/// <param name="Reconnected">The WAN uptime went backwards since the previous reading.</param>
public sealed record RouterReading(WanStatus? Status, bool Reconnected);

/// <summary>
/// Polls the router's own WAN status in the background.
/// <para>
/// Off the sampling path for the same reason as the deep probes: discovery alone takes
/// three seconds, and a SOAP round trip to a busy home router is not something to put in
/// front of a hundred-millisecond sampling cycle.
/// </para>
/// <para>
/// Tracks the reported WAN uptime across readings. A drop means the router re-established
/// its connection, and that single fact separates two outages that look identical from the
/// computer: a fault on the operator's line, and a router that restarted itself.
/// </para>
/// </summary>
public sealed class RouterStatusCache : IAsyncDisposable
{
    private static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UrgentInterval = TimeSpan.FromSeconds(5);

    /// <summary>Past this age the reading stops counting as current.</summary>
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);

    private readonly UpnpGateway _gateway;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private WanStatus? _latest;
    private long _readAtTicks;
    private bool _everRead;
    private bool _reconnected;
    private TimeSpan? _previousUptime;
    private volatile bool _urgent;
    private Task? _loop;

    public RouterStatusCache(IClock? clock = null, UpnpGateway? gateway = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _gateway = gateway ?? new UpnpGateway();
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_shutdown.Token), CancellationToken.None);

    /// <summary>Asks for a reading sooner, because connectivity looks wrong.</summary>
    public void RequestUrgentRead()
    {
        _urgent = true;

        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // One pending wake is enough.
            }
        }
    }

    /// <summary>
    /// The most recent reading, or an empty one when the router does not answer or the
    /// last reading is too old to rely on.
    /// </summary>
    public RouterReading Current()
    {
        lock (_gate)
        {
            if (!_everRead || _clock.MonotonicElapsedSince(_readAtTicks) > MaximumAge)
            {
                return new RouterReading(null, false);
            }

            return new RouterReading(_latest, _reconnected);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var wasUrgent = _urgent;
            _urgent = false;

            await ReadAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _wake.WaitAsync(wasUrgent ? UrgentInterval : HealthyInterval, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await _gateway.ReadAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                // A shorter uptime than last time means the connection was re-established
                // between the two readings. Only meaningful once there is a previous one.
                //
                // Updated only from a reading that actually carries an uptime. A failed
                // read used to clear the flag about five seconds after it was set - and a
                // router that reboots is precisely a router that stops answering, so the
                // one fact that separates "operator line fault" from "router restarted
                // itself" was systematically erased by the very event it describes.
                if (status?.Uptime is { } current && _previousUptime is { } previous)
                {
                    _reconnected = current < previous;
                }

                if (status?.Uptime is { } uptime)
                {
                    _previousUptime = uptime;
                }

                _latest = status;
                _readAtTicks = _clock.MonotonicTicks;
                _everRead = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A router that misbehaves must not take the monitor with it.
        catch (Exception)
        {
            // Home routers vary wildly in how well they implement this. A malformed answer
            // is treated as no answer, which the age guard turns into "could not check".
        }
#pragma warning restore CA1031
    }

    private bool _disposed;

    /// <summary>Idempotent: the session close path stops probing before the usual dispose runs.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _shutdown.Dispose();
        _wake.Dispose();
    }
}
