using System.Diagnostics;
using System.Runtime.Versioning;
using ManagedNativeWifi;

namespace IEM.Windows;

/// <summary>
/// Keeps a recent list of networks in range.
/// <para>
/// This exists to answer the single most useful question in the whole fault catalogue:
/// when the adapter drops, is the network still being broadcast? If the SSID has vanished
/// from the air, the router's radio failed and the operator is not involved. If it is
/// still there, the fault is between the device and the access point. Those two look
/// identical from the adapter's point of view and lead to completely different actions.
/// </para>
/// <para>
/// Scans run on a background loop rather than on demand. Windows rate-limits them - a scan
/// takes about four seconds and cannot be repeated freely - so asking for one inside a
/// sampling cycle would stall exactly the measurement it was meant to inform.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
/// <param name="interfaceId">
/// The adapter whose airwaves this cache reports. Null reads the whole machine - the
/// portable shape - but a monitoring session is about one link, and on a machine with two
/// radios the other adapter's scan must not answer for the one being watched.
/// </param>
public sealed class WlanScanCache(Guid? interfaceId = null) : IAsyncDisposable
{
    /// <summary>
    /// How often to rescan while everything is healthy. Windows will not honour a much
    /// tighter interval anyway, and scanning briefly disrupts the connection.
    /// </summary>
    private static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How often to rescan once something looks wrong. Faster, because this is the moment
    /// the answer decides who is at fault, but still above what Windows will refuse.
    /// </summary>
    private static readonly TimeSpan UrgentInterval = TimeSpan.FromSeconds(8);

    /// <summary>Past this age the answer is stale and is reported as unknown rather than guessed.</summary>
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(3);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private HashSet<string> _visible = new(StringComparer.OrdinalIgnoreCase);
    private long _scannedAtTicks;
    private bool _everScanned;
    private volatile bool _urgent;
    private Task? _loop;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_shutdown.Token), CancellationToken.None);

    /// <summary>Asks for a scan sooner than the healthy interval, because something looks wrong.</summary>
    public void RequestUrgentScan()
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
                // One pending wake is all that is wanted.
            }
        }
    }

    /// <summary>
    /// Whether the named network was in range at the last scan.
    /// <para>
    /// Returns <see langword="null"/> when there has been no scan yet or the last one is
    /// too old to rely on. That distinction matters: the classifier treats "not seen" as
    /// a failed router radio, and it must never reach that conclusion from stale data.
    /// </para>
    /// </summary>
    public bool? IsVisible(string? ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_everScanned || Stopwatch.GetElapsedTime(_scannedAtTicks) > MaximumAge)
            {
                return null;
            }

            return _visible.Contains(ssid);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var wasUrgent = _urgent;
            _urgent = false;

            await ScanAsync(cancellationToken).ConfigureAwait(false);

            var interval = wasUrgent ? UrgentInterval : HealthyInterval;

            try
            {
                await _wake.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Asking the adapter to rescan rather than reading its cache. The cache can be
            // minutes old, and a stale entry would keep reporting a network that stopped
            // being broadcast - precisely the failure this is here to detect.
            //
            // Scoped to the monitored adapter where one is known: a machine with a built-in
            // card and a USB dongle otherwise answers "is the SSID still on the air" from
            // whichever radio happened to scan last, and the one fact that separates a dead
            // router radio from a dead link comes from the wrong link entirely.
            if (interfaceId is { } monitored)
            {
                await NativeWifi.ScanNetworksAsync(
                    ScanMode.All, [monitored], TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await NativeWifi.ScanNetworksAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            // Per-adapter BSS enumeration returns the scan of that one radio; the
            // machine-wide SSID list is the portable shape for when no adapter was named.
            // Both shapes read BSS entries: the per-adapter one from the monitored radio,
            // the fallback as a union across radios. The SSID list API returned nothing on
            // real hardware while BSS enumeration saw the network - found live, 16.08.
            var found = (interfaceId is { } only
                    ? NativeWifi.EnumerateBssNetworks(only).Item2.Select(n => n.Ssid.ToString())
                    : NativeWifi.EnumerateBssNetworks().Select(n => n.Ssid.ToString()))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            lock (_gate)
            {
                _visible = found;
                _scannedAtTicks = Stopwatch.GetTimestamp();
                _everScanned = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed scan must never take the monitor down.
        catch (Exception)
        {
            // No wireless adapter, the service lacking rights to scan, or the radio being
            // switched off. The age guard in IsVisible turns this into "unknown", which is
            // the honest answer and keeps the classifier from inventing a router fault.
        }
#pragma warning restore CA1031
    }

    public async ValueTask DisposeAsync()
    {
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
