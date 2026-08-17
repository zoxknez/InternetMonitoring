using IEM.Core.Classification;
using IEM.Core.Model;

namespace IEM.Core.Incidents;

/// <summary>
/// Groups consecutive failing samples into outage segments and records the boundary samples
/// on both sides, so durations can be reported with honest bounds rather than as a single
/// invented number.
/// <para>
/// A pause in monitoring <em>closes</em> the current segment. Nothing was observed during the
/// pause, so nothing about it may be claimed: a laptop asleep from midnight to six is not six
/// hours of the operator failing to deliver service. When failures continue after the pause,
/// the new segment carries the same <see cref="IncidentRecord.CorrelationId"/>, so the two are
/// presented as one event while only the watched time is ever counted.
/// </para>
/// </summary>
/// <param name="alreadyClosed">
/// Segments closed before this instance existed. Non-zero when a session is resumed after a
/// restart, so numbering continues instead of colliding with segments already in the log.
/// </param>
public sealed class IncidentDetector(int alreadyClosed = 0)
{
    private readonly List<NetworkState> _statesSeen = [];

    private SampleInstant? _lastGood;
    private SampleInstant? _firstBad;
    private SampleInstant _lastBad;
    private string _firstDetail = string.Empty;
    private int _sampleCount;
    private int _closedCount = alreadyClosed;

    private Guid _correlationId = Guid.NewGuid();
    private bool _startedAfterGap;

    /// <summary>
    /// Set when a pause closed a segment, and cleared by whatever the next sample turns out
    /// to be. If failures continue, the event is the same one and keeps its identity; if the
    /// connection is healthy, the event ended somewhere inside the pause and is over.
    /// </summary>
    private Guid? _correlationCarriedOverFromGap;

    private string? _interfaceAtLastGood;
    private string? _interfaceAtFirstBad;

    public bool HasOpenIncident => _firstBad is not null;

    /// <summary>Segments closed so far.</summary>
    public int ClosedCount => _closedCount;

    /// <summary>
    /// Feeds one classified sample in.
    /// Returns a segment only on the sample that closes one; otherwise null.
    /// </summary>
    /// <param name="interfaceId">Adapter that carried traffic for this sample, when known.</param>
    public IncidentRecord? Observe(SampleInstant instant, SampleVerdict verdict, string? interfaceId = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        // Gaps arrive through ObserveGap, which knows when the pause began. A gap verdict
        // reaching this path carries no such boundary, so it is not evidence of anything.
        if (verdict.State == NetworkState.MonitoringGap)
        {
            return null;
        }

        if (verdict.IsOutage)
        {
            AccumulateBadSample(instant, verdict, interfaceId);
            return null;
        }

        // Healthy sample: any event that survived a pause has now ended.
        _correlationCarriedOverFromGap = null;

        var closed = _firstBad is not null ? Close(instant, interfaceId, gapStartedAt: null) : null;

        _lastGood = instant;
        _interfaceAtLastGood = interfaceId;

        return closed;
    }

    /// <summary>
    /// Reports that monitoring stopped for a while, closing any segment in progress at the
    /// moment observation ended.
    /// </summary>
    /// <param name="gapStartedAt">
    /// Monotonic position of the last sample before the pause. The closed segment's ceiling
    /// stops here, because nothing past it was watched.
    /// </param>
    /// <returns>The segment the pause cut short, or null if nothing was in progress.</returns>
    public IncidentRecord? ObserveGap(TimeSpan gapStartedAt)
    {
        IncidentRecord? cut = null;

        if (_firstBad is not null)
        {
            // Carry the identity across, so a failure continuing after the pause is
            // recognised as the same event rather than filed as a second, unrelated outage.
            _correlationCarriedOverFromGap = _correlationId;
            cut = Close(firstGood: null, interfaceAtFirstGood: null, gapStartedAt: gapStartedAt);
        }

        // The last healthy sample is on the far side of the pause now, and says nothing about
        // when a later outage began. Keeping it as a baseline was the whole bug: a segment
        // starting six hours after that sample inherited it and reported a six-hour ceiling.
        _lastGood = null;
        _interfaceAtLastGood = null;

        return cut;
    }

    /// <summary>
    /// Closes a segment that was still running when monitoring stopped. The record is marked
    /// open, because service was never observed to return.
    /// </summary>
    public IncidentRecord? CloseOpenIncident() =>
        _firstBad is null ? null : Close(firstGood: null, interfaceAtFirstGood: null, gapStartedAt: null);

    /// <summary>
    /// Restores a segment that was in progress when the process died, so failures already in
    /// the raw chain are not dropped from the statistics when the session resumes.
    /// </summary>
    public void RestoreOpenIncident(
        SampleInstant? lastGood,
        SampleInstant firstBad,
        SampleInstant lastBad,
        IReadOnlyList<NetworkState> statesSeen,
        int sampleCount,
        string technicalDetail,
        Guid? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(statesSeen);

        _lastGood = lastGood;
        _firstBad = firstBad;
        _lastBad = lastBad;
        _sampleCount = sampleCount;
        _firstDetail = technicalDetail;
        _correlationId = correlationId ?? Guid.NewGuid();
        _startedAfterGap = false;

        _statesSeen.Clear();
        _statesSeen.AddRange(statesSeen);
    }

    private void AccumulateBadSample(SampleInstant instant, SampleVerdict verdict, string? interfaceId)
    {
        if (_firstBad is null)
        {
            _firstBad = instant;
            _firstDetail = verdict.TechnicalDetail;
            _sampleCount = 0;
            _interfaceAtFirstBad = interfaceId;
            _statesSeen.Clear();

            // Either this continues an event a pause interrupted, or it is a new one.
            _startedAfterGap = _correlationCarriedOverFromGap is not null;
            _correlationId = _correlationCarriedOverFromGap ?? Guid.NewGuid();
            _correlationCarriedOverFromGap = null;
        }

        _lastBad = instant;
        _sampleCount++;

        if (!_statesSeen.Contains(verdict.State))
        {
            _statesSeen.Add(verdict.State);
        }
    }

    private IncidentRecord Close(SampleInstant? firstGood, string? interfaceAtFirstGood, TimeSpan? gapStartedAt)
    {
        var record = new IncidentRecord
        {
            Number = ++_closedCount,
            CorrelationId = _correlationId,
            LastGood = _lastGood,
            FirstBad = _firstBad!.Value,
            LastBad = _lastBad,
            FirstGood = firstGood,
            GapStartedAt = gapStartedAt,
            StartedAfterGap = _startedAfterGap,
            WorstState = _statesSeen.MaxBy(s => s.Rank()),
            StatesSeen = [.. _statesSeen],
            SampleCount = _sampleCount,
            TechnicalDetail = _firstDetail,
            InterfaceAtLastGood = _interfaceAtLastGood,
            InterfaceAtFirstBad = _interfaceAtFirstBad,
            InterfaceAtFirstGood = interfaceAtFirstGood,
        };

        _firstBad = null;
        _sampleCount = 0;
        _startedAfterGap = false;
        _interfaceAtFirstBad = null;
        _statesSeen.Clear();

        return record;
    }
}
