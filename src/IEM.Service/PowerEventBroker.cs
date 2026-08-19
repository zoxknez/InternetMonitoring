using IEM.Core.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

namespace IEM.Service;

/// <summary>
/// Carries suspend and resume notifications from whatever the host happens to be to
/// whoever is monitoring.
/// </summary>
public sealed class PowerEventBroker : IPowerEventSource
{
    private readonly Lock _gate = new();
    private readonly List<Action> _suspendCallbacks = [];
    private readonly ILogger<PowerEventBroker> _logger;
    private readonly bool _usingSystemEvents;

    public PowerEventBroker(ILogger<PowerEventBroker> logger)
    {
        _logger = logger;

        // A Windows service gets suspend notifications through the service control
        // handler. Running as a plain console process there is no such handler, so the
        // desktop notification is used instead - which is how this behaves under a
        // debugger, and it should behave the same either way.
        if (!OperatingSystem.IsWindows() || WindowsServiceHelpers.IsWindowsService())
        {
            return;
        }

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _usingSystemEvents = true;
        }
        catch (PlatformNotSupportedException)
        {
            _logger.LogDebug("Obaveštenja o stanju napajanja nisu dostupna u ovom okruženju.");
        }
    }

    /// <summary>Registers a callback and returns a handle that unregisters it.</summary>
    public IDisposable OnSuspending(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            _suspendCallbacks.Add(callback);
        }

        return new Subscription(this, callback);
    }

    /// <summary>Called by the service lifetime when Windows announces a suspend.</summary>
    public void RaiseSuspending()
    {
        _logger.LogInformation("Sistem prelazi u stanje spavanja. Pauza nadzora biće zabeležena kao spavanje.");

        Action[] callbacks;
        lock (_gate)
        {
            callbacks = [.. _suspendCallbacks];
        }

        foreach (var callback in callbacks)
        {
            try
            {
                callback();
            }
#pragma warning disable CA1031 // A misbehaving subscriber must not block the suspend path.
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Greška pri obradi obaveštenja o spavanju.");
            }
#pragma warning restore CA1031
        }
    }

    public void RaiseResumed() =>
        _logger.LogInformation("Sistem je nastavio rad nakon spavanja.");

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                RaiseSuspending();
                break;

            case PowerModes.Resume:
                RaiseResumed();
                break;

            default:
                break;
        }
    }

    public void Dispose()
    {
        if (_usingSystemEvents && OperatingSystem.IsWindows())
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }

        lock (_gate)
        {
            _suspendCallbacks.Clear();
        }
    }

    private sealed class Subscription(PowerEventBroker broker, Action callback) : IDisposable
    {
        public void Dispose()
        {
            lock (broker._gate)
            {
                broker._suspendCallbacks.Remove(callback);
            }
        }
    }
}
