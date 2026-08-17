using IEM.Core.Model;
using IEM.Core.Time;

namespace IEM.Core.Probes;

/// <summary>
/// Holds the most recent result of every probe, and decides how far each can still be
/// trusted when it is read.
/// <para>
/// This is what lets the probes run free of the sampling tick. A TCP connect may take two
/// seconds; it simply writes its answer here when it arrives, and the aggregator reads
/// whatever is present each time it fires. Nothing waits for anything.
/// </para>
/// <para>
/// The store also decides freshness, and that is the more important job. It remembers the
/// moment trouble started, and from then on refuses to let any measurement taken
/// <em>before</em> that moment count as evidence the connection is working. Without that
/// rule a handshake from five seconds ago keeps insisting the line is fine while every
/// live probe is failing, and a genuine outage disappears from the record - reclassified
/// as harmless filtering, which is not an outage at all.
/// </para>
/// </summary>
public sealed class ObservationStore(IClock? clock = null)
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<ObservationKey, ProbeResult> _latest = [];

    private long? _suspicionStartedAtTicks;

    /// <summary>
    /// When the current trouble began, on the monotonic axis. Null while everything the
    /// fast probes can see is working.
    /// </summary>
    public long? SuspicionStartedAtTicks
    {
        get
        {
            lock (_gate)
            {
                return _suspicionStartedAtTicks;
            }
        }
    }

    /// <summary>
    /// Families whose failure is what raises suspicion. Only the cheap, frequent probes
    /// qualify: the slow ones run too rarely to mark the start of anything precisely.
    /// </summary>
    private static bool IsFastFamily(ProbeFamily family) =>
        family is ProbeFamily.GatewayIcmp or ProbeFamily.ExternalIcmp or ProbeFamily.Tcp;

    /// <summary>Files a completed measurement, replacing any earlier one for the same probe.</summary>
    public void Record(ProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            _latest[ObservationKey.For(result)] = result;
            UpdateSuspicion(result);
        }
    }

    /// <summary>Files several measurements as one, so a reader cannot see half a round.</summary>
    public void RecordAll(IEnumerable<ProbeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        lock (_gate)
        {
            foreach (var result in results)
            {
                _latest[ObservationKey.For(result)] = result;
                UpdateSuspicion(result);
            }
        }
    }

    /// <summary>
    /// Everything currently on file, each tagged with how far it can be trusted.
    /// <para>
    /// Freshness is decided here rather than when the measurement was filed, because it
    /// depends on how much time has passed and on whether trouble has since begun.
    /// </para>
    /// </summary>
    public IReadOnlyList<ProbeResult> Snapshot()
    {
        var now = _clock.MonotonicTicks;

        lock (_gate)
        {
            var snapshot = new List<ProbeResult>(_latest.Count);

            foreach (var result in _latest.Values)
            {
                var age = _clock.MonotonicElapsed(result.CompletedAtTicks, now);
                snapshot.Add(result with { Age = age, Freshness = Judge(result, age) });
            }

            return snapshot;
        }
    }

    private Freshness Judge(ProbeResult result, TimeSpan age)
    {
        if (!result.WasAttempted)
        {
            return Freshness.Unknown;
        }

        // Absolute first: a result older than its family's lifetime has stopped describing
        // the present regardless of what else is going on.
        if (age > result.ProbeFamily.Lifetime())
        {
            return Freshness.Expired;
        }

        // Relative second, and this is the rule that matters. A measurement taken before
        // the trouble started cannot testify about the trouble.
        if (_suspicionStartedAtTicks is { } suspicion && result.CompletedAtTicks < suspicion)
        {
            return Freshness.Stale;
        }

        return Freshness.Fresh;
    }

    /// <summary>
    /// Tracks when trouble starts and when it clears.
    /// <para>
    /// Suspicion begins at the moment the failing probe finished, not at the moment we
    /// noticed - the connection was already in trouble while that probe was timing out.
    /// </para>
    /// </summary>
    private void UpdateSuspicion(ProbeResult result)
    {
        if (!IsFastFamily(result.ProbeFamily) || !result.WasAttempted)
        {
            return;
        }

        if (!result.Succeeded)
        {
            _suspicionStartedAtTicks ??= result.CompletedAtTicks;
            return;
        }

        // Clears only once every fast probe on file is succeeding again. Clearing on the
        // first success would let one lucky ping restore trust in a batch of stale results
        // while the rest of the link is still down.
        if (_suspicionStartedAtTicks is not null && AllFastProbesSucceeding(result.CompletedAtTicks))
        {
            _suspicionStartedAtTicks = null;
        }
    }

    /// <summary>
    /// Whether every fast probe that still describes the present is succeeding.
    /// <para>
    /// Expired results are skipped, and that exclusion is load-bearing. A target can stop
    /// being probed entirely - the IPv6 addresses are dropped from the round the moment the
    /// machine loses its global IPv6 address, which is exactly what happens when a router
    /// restarts mid-outage. Its last recorded answer was a failure, nothing ever overwrites
    /// it, and without this check that one dead entry holds suspicion open forever: every
    /// later handshake is marked stale, nothing can prove reachability again, and the tool
    /// reports a permanent outage on a connection that recovered minutes ago.
    /// </para>
    /// </summary>
    private bool AllFastProbesSucceeding(long now)
    {
        foreach (var result in _latest.Values)
        {
            if (!IsFastFamily(result.ProbeFamily) || !result.WasAttempted || result.Succeeded)
            {
                continue;
            }

            if (_clock.MonotonicElapsed(result.CompletedAtTicks, now) <= result.ProbeFamily.Lifetime())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Forgets everything. Used when a session restarts rather than resumes.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _latest.Clear();
            _suspicionStartedAtTicks = null;
        }
    }

    /// <summary>Identifies one probe across rounds, so a new result replaces its predecessor.</summary>
    private readonly record struct ObservationKey(ProbeKind Kind, ProbeScope Scope, string Target, DnsResolverRole? DnsRole)
    {
        public static ObservationKey For(ProbeResult result) =>
            new(result.Kind, result.Scope, result.Target, result.DnsRole);
    }
}
