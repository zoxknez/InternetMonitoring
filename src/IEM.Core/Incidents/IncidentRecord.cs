using IEM.Core.Classification;
using IEM.Core.Model;

namespace IEM.Core.Incidents;

/// <summary>
/// A point in time on both axes: the monotonic axis, which is authoritative for every
/// duration, and the wall clock, which exists so the record can be lined up against the
/// operator's own logs.
/// </summary>
public readonly record struct SampleInstant(TimeSpan Monotonic, DateTimeOffset Wall);

/// <summary>A stretch of the monotonic axis, used to prove that two periods do not overlap.</summary>
public readonly record struct MonotonicInterval(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    /// <summary>True when the two stretches share any time at all.</summary>
    public bool Intersects(MonotonicInterval other) => Start < other.End && other.Start < End;
}

/// <summary>
/// One confirmed outage segment.
/// <para>
/// Sampling is discrete, so the true start sits somewhere between the last healthy sample
/// and the first failing one, and the true end sits between the last failing sample and
/// the first healthy one. Reporting a single number pretends that uncertainty away, and a
/// single number is exactly what an operator can pick apart. So all three are carried: a
/// floor that is beyond dispute, a ceiling, and a central estimate that always lies
/// between them.
/// </para>
/// <para>
/// A <em>segment</em>, not an incident, because a pause in monitoring cuts one. Nothing was
/// observed during the pause, so no duration may span it. Segments of the same event share
/// a <see cref="CorrelationId"/> and are shown together, but their measured time is only
/// ever the sum of what was actually watched.
/// </para>
/// </summary>
public sealed record IncidentRecord
{
    public required int Number { get; init; }

    /// <summary>
    /// Groups segments of one event that a pause in monitoring split apart. Segments that
    /// stand alone get their own value.
    /// </summary>
    public required Guid CorrelationId { get; init; }

    /// <summary>Last sample before the outage that was confirmed healthy, if there was one.</summary>
    public SampleInstant? LastGood { get; init; }

    public required SampleInstant FirstBad { get; init; }

    public required SampleInstant LastBad { get; init; }

    /// <summary>First healthy sample after the outage. Null while the segment is still open.</summary>
    public SampleInstant? FirstGood { get; init; }

    /// <summary>
    /// Where monitoring stopped, when a pause is what ended this segment. Bounds the
    /// ceiling: nothing after this moment was observed, so nothing after it may be claimed.
    /// </summary>
    public TimeSpan? GapStartedAt { get; init; }

    public required NetworkState WorstState { get; init; }

    public required IReadOnlyList<NetworkState> StatesSeen { get; init; }

    public required int SampleCount { get; init; }

    /// <summary>The first classification reason recorded, kept for the raw log.</summary>
    public string TechnicalDetail { get; init; } = string.Empty;

    /// <summary>Monitoring stopped while this segment was in progress.</summary>
    public bool EndedByGap => GapStartedAt is not null;

    /// <summary>This segment picked up where a pause in monitoring left off.</summary>
    public bool StartedAfterGap { get; init; }

    /// <summary>Still in progress when the session ended, and not cut short by a pause.</summary>
    public bool IsOpen => FirstGood is null && !EndedByGap;

    public ConfidenceScore? Confidence { get; init; }

    /// <summary>
    /// Busiest second of the machine's own traffic during this segment, in bytes per second,
    /// or null when the adapter counters were never read.
    /// <para>
    /// Kept on the record rather than only inside the confidence score, so the report can
    /// print the figure beside the incident. "The line was busy with your own traffic" invites
    /// the question "how busy", and the answer separates a stream in another room from a
    /// download that was using the whole connection.
    /// </para>
    /// </summary>
    public long? PeakLocalTrafficBytesPerSecond { get; init; }

    // ---- Path, for P0-8 ------------------------------------------------------

    /// <summary>Adapter carrying traffic at the last healthy sample before this segment.</summary>
    public string? InterfaceAtLastGood { get; init; }

    public string? InterfaceAtFirstBad { get; init; }

    /// <summary>Adapter carrying traffic when service returned.</summary>
    public string? InterfaceAtFirstGood { get; init; }

    /// <summary>
    /// Traffic changed adapters across this segment.
    /// <para>
    /// When it does, the segment stops being clean evidence about any one link: Windows
    /// switching from a dead Wi-Fi to a live Ethernet looks exactly like a two-second
    /// recovery, and the link under test never recovered at all.
    /// </para>
    /// </summary>
    public bool RouteChanged =>
        (InterfaceAtLastGood is not null && InterfaceAtFirstBad is not null &&
         !string.Equals(InterfaceAtLastGood, InterfaceAtFirstBad, StringComparison.OrdinalIgnoreCase)) ||
        (InterfaceAtFirstBad is not null && InterfaceAtFirstGood is not null &&
         !string.Equals(InterfaceAtFirstBad, InterfaceAtFirstGood, StringComparison.OrdinalIgnoreCase));

    // ---- Durations -----------------------------------------------------------

    /// <summary>
    /// Where the segment ends as far as measurement is concerned: the first healthy sample,
    /// or where monitoring stopped, or the last failing sample if neither happened.
    /// </summary>
    private TimeSpan MeasuredEnd => FirstGood?.Monotonic ?? GapStartedAt ?? LastBad.Monotonic;

    /// <summary>
    /// Shortest duration consistent with the observations: the span of samples that were
    /// definitely failing. This is the figure to put in a complaint, because it cannot be
    /// argued down.
    /// </summary>
    public TimeSpan DurationMin => LastBad.Monotonic - FirstBad.Monotonic;

    /// <summary>
    /// Longest duration consistent with the observations, bounded by where observation
    /// stopped. Never spans a pause in monitoring.
    /// </summary>
    public TimeSpan DurationMax => MeasuredEnd - (LastGood?.Monotonic ?? FirstBad.Monotonic);

    /// <summary>
    /// Central estimate: midpoint of the start window to midpoint of the end window.
    /// Provably lies between <see cref="DurationMin"/> and <see cref="DurationMax"/>.
    /// </summary>
    public TimeSpan DurationReported => MeasuredInterval.Duration;

    /// <summary>
    /// The stretch of time this segment's reported duration actually covers.
    /// <para>
    /// Exposed so the invariant can be asserted directly: no measured interval may ever
    /// overlap a period during which nothing was being measured.
    /// </para>
    /// </summary>
    public MonotonicInterval MeasuredInterval
    {
        get
        {
            var startMid = Midpoint(LastGood?.Monotonic ?? FirstBad.Monotonic, FirstBad.Monotonic);
            var endMid = Midpoint(LastBad.Monotonic, MeasuredEnd);
            return new MonotonicInterval(startMid, endMid);
        }
    }

    /// <summary>Half-width of the uncertainty around <see cref="DurationReported"/>.</summary>
    public TimeSpan DurationUncertainty => (DurationMax - DurationMin) / 2;

    /// <summary>The fault sits upstream of the customer's own equipment.</summary>
    public bool IsUpstream => WorstState.IsUpstream();

    public DateTimeOffset StartedAtUtc => FirstBad.Wall;

    public DateTimeOffset EndedAtUtc => (FirstGood ?? LastBad).Wall;

    private static TimeSpan Midpoint(TimeSpan a, TimeSpan b) => a + ((b - a) / 2);
}
