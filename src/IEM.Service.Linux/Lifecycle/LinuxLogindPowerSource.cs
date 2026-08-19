using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Hosting;
using IEM.Service.Linux.Lifecycle.Logind;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IEM.Service.Linux.Lifecycle;

/// <summary>
/// Linux host power source observing systemd-logind via D-Bus PrepareForSleep signal.
/// Maps PrepareForSleep(true) -> Suspend and PrepareForSleep(false) -> Resume.
/// Runs as a BackgroundService with automatic exponential backoff reconnection.
/// Failure of D-Bus / logind degrades observability gracefully without impacting probing or service life.
/// </summary>
public sealed class LinuxLogindPowerSource : BackgroundService, IPowerEventSource
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Lock _gate = new();
    private readonly List<Action> _suspendCallbacks = [];
    private readonly List<Action> _resumeCallbacks = [];
    private readonly ILogger<LinuxLogindPowerSource> _logger;
    private readonly Func<ILogindSignalTransport> _transportFactory;
    private ILogindSignalTransport? _activeTransport;

    public LinuxLogindPowerSource(ILogger<LinuxLogindPowerSource> logger)
        : this(logger, () => new TmdsLogindSignalTransport())
    {
    }

    internal LinuxLogindPowerSource(
        ILogger<LinuxLogindPowerSource> logger,
        Func<ILogindSignalTransport> transportFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }

    /// <summary>Registers a callback invoked when the host system is suspending.</summary>
    public IDisposable OnSuspending(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            _suspendCallbacks.Add(callback);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _suspendCallbacks.Remove(callback);
            }
        });
    }

    /// <summary>Registers a callback invoked when the host system has resumed from suspend.</summary>
    public IDisposable OnResumed(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            _resumeCallbacks.Add(callback);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _resumeCallbacks.Remove(callback);
            }
        });
    }

    /// <summary>Dispatches suspend event to all registered suspend callbacks.</summary>
    internal void RaiseSuspending()
    {
        _logger.LogInformation("systemd-logind: Sistem prelazi u stanje spavanja (PrepareForSleep=true).");

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
#pragma warning disable CA1031 // Misbehaving subscriber must not block or crash power pipeline
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Greška pri obradi logind obaveštenja o spavanju.");
            }
#pragma warning restore CA1031
        }
    }

    /// <summary>Dispatches resume event to all registered resume callbacks.</summary>
    internal void RaiseResumed()
    {
        _logger.LogInformation("systemd-logind: Sistem je nastavio rad nakon spavanja (PrepareForSleep=false).");

        Action[] callbacks;
        lock (_gate)
        {
            callbacks = [.. _resumeCallbacks];
        }

        foreach (var callback in callbacks)
        {
            try
            {
                callback();
            }
#pragma warning disable CA1031 // Misbehaving subscriber must not block or crash power pipeline
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Greška pri obradi logind obaveštenja o buđenju.");
            }
#pragma warning restore CA1031
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryIndex = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            ILogindSignalTransport? transport = null;

            try
            {
                transport = _transportFactory();
                lock (_gate)
                {
                    _activeTransport = transport;
                }

                retryIndex = 0; // Reset retry counter on successful transport instantiation
                await transport.ObservePrepareForSleepAsync(
                    isSuspending =>
                    {
                        if (isSuspending)
                        {
                            RaiseSuspending();
                        }
                        else
                        {
                            RaiseResumed();
                        }

                        return ValueTask.CompletedTask;
                    },
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "systemd-logind D-Bus transport prekinut ili nedostupan. Pokušaj ponovnog povezivanja...");
            }
            finally
            {
                if (transport is not null)
                {
                    try
                    {
                        await transport.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Dispose errors on degraded transport must not crash loop
                    }
                }

                lock (_gate)
                {
                    if (ReferenceEquals(_activeTransport, transport))
                    {
                        _activeTransport = null;
                    }
                }
            }

            if (stoppingToken.IsCancellationRequested) break;

            var delay = RetryDelays[Math.Min(retryIndex++, RetryDelays.Length - 1)];
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();

        lock (_gate)
        {
            _suspendCallbacks.Clear();
            _resumeCallbacks.Clear();
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
