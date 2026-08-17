using IEM.Core.Classification;
using IEM.Core.Model;

namespace IEM.Core.Scheduling;

public enum CadencePhase
{
    /// <summary>Nothing wrong. Sample slowly and stay out of the way.</summary>
    Stable,

    /// <summary>Something failed once. Speed up before deciding anything.</summary>
    Suspect,

    /// <summary>Repeated failures. Sample as fast as practical to pin down the edges.</summary>
    Burst,

    /// <summary>A confirmed outage is in progress.</summary>
    Incident,

    /// <summary>Service came back. Keep sampling fast for a while to catch a flapping link.</summary>
    Recovery,
}

public sealed record CadenceOptions
{
    public static readonly CadenceOptions Default = new();

    public TimeSpan StableInterval { get; init; } = TimeSpan.FromMilliseconds(1000);

    public TimeSpan SuspectInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan BurstInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan IncidentInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to keep sampling fast after service returns.</summary>
    public TimeSpan RecoveryHold { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive non-clean samples before escalating from Suspect to Burst.</summary>
    public int BurstThreshold { get; init; } = 2;
}

public sealed record CadenceDecision(CadencePhase Phase, TimeSpan Interval, bool PhaseChanged);

/// <summary>
/// Decides how fast to sample next.
/// <para>
/// The point is precision where it matters. A fixed slow interval smears the edges of
/// every outage; a fixed fast interval burns battery and bandwidth for two days to
/// measure nothing. This escalates only once something actually looks wrong, so outage
/// boundaries get sub-second resolution while a healthy link is sampled once a second.
/// </para>
/// <para>
/// Driven entirely by observations and an externally supplied monotonic timestamp, so
/// the whole state machine is testable without waiting on a real clock.
/// </para>
/// </summary>
public sealed class AdaptiveCadenceController(CadenceOptions? options = null)
{
    private readonly CadenceOptions _options = options ?? CadenceOptions.Default;

    private int _consecutiveUnclean;
    private TimeSpan _recoveryStartedAt;

    public CadencePhase Phase { get; private set; } = CadencePhase.Stable;

    public TimeSpan Interval => IntervalFor(Phase);

    /// <param name="verdict">The verdict for the sample just taken.</param>
    /// <param name="monotonicNow">Monotonic time since monitoring started.</param>
    public CadenceDecision Observe(SampleVerdict verdict, TimeSpan monotonicNow)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var previous = Phase;
        Phase = NextPhase(verdict, monotonicNow, previous);

        return new CadenceDecision(Phase, IntervalFor(Phase), Phase != previous);
    }

    private CadencePhase NextPhase(SampleVerdict verdict, TimeSpan monotonicNow, CadencePhase previous)
    {
        if (verdict.IsOutage)
        {
            _consecutiveUnclean++;
            return CadencePhase.Incident;
        }

        // Info-level states - roaming, a monitoring gap, our own speed test - are noted
        // elsewhere but are not signs of a failing link, so they must not drive escalation.
        var isClean = verdict.Severity is Severity.Ok or Severity.Info;

        if (!isClean)
        {
            _consecutiveUnclean++;
            return _consecutiveUnclean >= _options.BurstThreshold
                ? CadencePhase.Burst
                : CadencePhase.Suspect;
        }

        _consecutiveUnclean = 0;

        if (previous is CadencePhase.Incident or CadencePhase.Burst or CadencePhase.Suspect)
        {
            _recoveryStartedAt = monotonicNow;
            return CadencePhase.Recovery;
        }

        if (previous == CadencePhase.Recovery)
        {
            return monotonicNow - _recoveryStartedAt >= _options.RecoveryHold
                ? CadencePhase.Stable
                : CadencePhase.Recovery;
        }

        return CadencePhase.Stable;
    }

    private TimeSpan IntervalFor(CadencePhase phase) => phase switch
    {
        CadencePhase.Stable => _options.StableInterval,
        CadencePhase.Suspect => _options.SuspectInterval,
        CadencePhase.Burst => _options.BurstInterval,
        CadencePhase.Incident => _options.IncidentInterval,
        CadencePhase.Recovery => _options.RecoveryInterval,
        _ => _options.StableInterval,
    };
}
