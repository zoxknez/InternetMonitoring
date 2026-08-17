using IEM.Core.Incidents;

namespace IEM.Core.Probes;

/// <param name="Phase">Whether this trace was taken as the outage began or after it cleared.</param>
public sealed record IncidentTrace(int IncidentNumber, TracePhase Phase, DateTimeOffset TakenUtc, TraceResult Result);

public enum TracePhase
{
    /// <summary>Taken while the outage was in progress. The one that matters.</summary>
    DuringOutage,

    /// <summary>Taken once service returned, for comparison.</summary>
    AfterRecovery,
}

public sealed record PathTracerOptions
{
    public static readonly PathTracerOptions Default = new();

    public string Target { get; init; } = "1.1.1.1";

    public int MaxHops { get; init; } = 20;

    public TimeSpan PerHopTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Shortest gap between traces.
    /// <para>
    /// A trace costs up to twenty pings. A link flapping every few seconds would otherwise
    /// spawn one on every flap, flooding the log and adding traffic to a connection that
    /// is already struggling.
    /// </para>
    /// </summary>
    public TimeSpan MinimumInterval { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Takes a trace when an outage starts and again when it clears.
/// <para>
/// The trace answers the question the ping counts cannot: not whether the connection is
/// down, but where it stops. A trace that dies at the router points at the customer's own
/// equipment; one that dies at the operator's first hop points at them. Without it, an
/// outage record says only that nothing answered - which is exactly the ambiguity an
/// operator will exploit.
/// </para>
/// <para>
/// Runs entirely off the sampling path. A trace takes seconds, and the sampling loop is
/// running at a tenth of a second precisely because an incident is in progress.
/// </para>
/// </summary>
public sealed class IncidentPathTracer : IAsyncDisposable
{
    private readonly PathTracerOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private DateTimeOffset _lastTraceAt = DateTimeOffset.MinValue;
    private int _pendingIncidentNumber;

    public IncidentPathTracer(PathTracerOptions? options = null) =>
        _options = options ?? PathTracerOptions.Default;

    /// <summary>Raised once a trace finishes. Never on the sampling thread.</summary>
    public event Action<IncidentTrace>? TraceCompleted;

    /// <summary>
    /// Attaches to an engine so traces follow its incidents.
    /// <para>
    /// The during-outage trace is triggered from the first failing sample rather than from
    /// the closed incident record, because by the time an incident closes the outage is
    /// over and a trace would describe a working connection.
    /// </para>
    /// </summary>
    public void Attach(MonitorEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        engine.SampleRecorded += sample =>
        {
            if (sample.Verdict.IsOutage)
            {
                // Numbered one past the last closed incident, so the trace can be matched
                // to the incident record that will be written when this outage ends.
                RequestTrace(engine.Statistics.IncidentCount + 1, TracePhase.DuringOutage);
            }
        };

        engine.IncidentClosed += incident => RequestTrace(incident.Number, TracePhase.AfterRecovery);
    }

    private void RequestTrace(int incidentNumber, TracePhase phase)
    {
        var now = DateTimeOffset.UtcNow;

        if (now - _lastTraceAt < _options.MinimumInterval)
        {
            return;
        }

        // Only one trace in flight. A second would compete with the first for the same
        // struggling link and produce two half-useful pictures instead of one good one.
        if (!_oneAtATime.Wait(0))
        {
            return;
        }

        _lastTraceAt = now;
        _pendingIncidentNumber = incidentNumber;

        _ = Task.Run(() => RunAsync(incidentNumber, phase, now), CancellationToken.None);
    }

    private async Task RunAsync(int incidentNumber, TracePhase phase, DateTimeOffset takenAt)
    {
        try
        {
            var result = await PathTracer
                .TraceAsync(_options.Target, _options.MaxHops, _options.PerHopTimeout, _shutdown.Token)
                .ConfigureAwait(false);

            TraceCompleted?.Invoke(new IncidentTrace(incidentNumber, phase, takenAt, result));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // A failed trace must never disturb the monitoring.
        catch (Exception)
        {
            // A trace is supporting evidence. Losing one costs detail on a single incident;
            // letting it propagate would cost the whole session.
        }
#pragma warning restore CA1031
        finally
        {
            _oneAtATime.Release();
        }
    }

    private bool _disposed;

    /// <summary>
    /// Idempotent: the session-close path disposes the tracer explicitly - probing has to
    /// stop before the evidence is written - while the usual <c>await using</c> disposes it
    /// again on the way out. The second call used to throw ObjectDisposedException from the
    /// already-disposed cancellation source, taking the worker down right after a session
    /// finished cleanly.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _shutdown.Dispose();
        _oneAtATime.Dispose();
    }
}
