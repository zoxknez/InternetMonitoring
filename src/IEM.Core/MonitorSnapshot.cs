using IEM.Core.Model;

namespace IEM.Core;

/// <summary>
/// Everything an interface needs to draw the current state, in one value.
/// <para>
/// Defined in the core rather than in the interface project on purpose: the same shape is
/// produced by the Windows service over its pipe and by the engine running in-process, and
/// the interface must not be able to tell which one it is talking to. Anything that only
/// one of the two could supply has no place here.
/// </para>
/// </summary>
public sealed record MonitorSnapshot
{
    public static readonly MonitorSnapshot Empty = new();

    public string? SessionId { get; init; }

    public string? Directory { get; init; }

    public DateTimeOffset? StartedUtc { get; init; }

    /// <summary>Null means the session runs until stopped.</summary>
    public TimeSpan? PlannedDuration { get; init; }

    /// <summary>Session-relative time, including anything carried over a restart.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>True when this session was picked up after an interruption.</summary>
    public bool Resumed { get; init; }

    // ---- Live ---------------------------------------------------------------

    public NetworkState CurrentState { get; init; } = NetworkState.Ok;

    public TimeSpan? CurrentLatency { get; init; }

    public long SampleCount { get; init; }

    /// <summary>
    /// The share of external targets that did not answer on the most recent sample.
    /// <para>
    /// Not packet loss. One probe per destination, so this counts destinations that went
    /// quiet - which for three targets moves in thirds and says nothing about the proportion
    /// of packets that survived.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Null until a sample has been taken. UNKNOWN_NEVER_BECOMES_ZERO: before the first
    /// cycle this was reported as <c>0 d</c> and the tile read "0 %", which is the reassuring
    /// answer to a question nobody had asked yet.
    /// </remarks>
    public double? UnreachableTargetShare { get; init; }

    // ---- Totals -------------------------------------------------------------

    public TimeSpan MonitoredTime { get; init; }

    public TimeSpan GapTime { get; init; }

    public double AvailabilityPercent { get; init; } = 100d;

    public double UpstreamAvailabilityPercent { get; init; } = 100d;

    public int IncidentCount { get; init; }

    public int UpstreamIncidentCount { get; init; }

    public TimeSpan UpstreamDowntime { get; init; }

    public TimeSpan LocalDowntime { get; init; }

    public TimeSpan LongestUpstreamOutage { get; init; }

    // ---- Link ---------------------------------------------------------------

    public string? InterfaceName { get; init; }

    public LinkMedium Medium { get; init; } = LinkMedium.Unknown;

    public string? GatewayAddress { get; init; }

    /// <summary>Per-probe-family tallies for the most recent sample, for the layer panel.</summary>
    public ProbeTally Gateway { get; init; }

    public ProbeTally ExternalIcmp { get; init; }

    public ProbeTally ExternalTcp { get; init; }

    public ProbeTally Dns { get; init; }

    public ProbeTally Http { get; init; }

    /// <summary>How much of the planned window has run, 0 to 1. Null for an open-ended session.</summary>
    public double? Progress =>
        PlannedDuration is { } planned && planned > TimeSpan.Zero
            ? Math.Clamp(Elapsed / planned, 0d, 1d)
            : null;

    public TimeSpan? Remaining =>
        PlannedDuration is { } planned && planned > Elapsed ? planned - Elapsed : null;

    /// <summary>Builds a snapshot from a live engine and its most recent sample.</summary>
    public static MonitorSnapshot From(MonitorEngine engine, MonitorSample? latest, string? sessionId, string? directory)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var statistics = engine.Statistics;
        var cycle = latest?.Cycle;

        return new MonitorSnapshot
        {
            SessionId = sessionId,
            Directory = directory,
            Elapsed = engine.SessionElapsed,
            Resumed = statistics.IsResumed,

            CurrentState = latest?.Verdict.State ?? NetworkState.Ok,
            CurrentLatency = cycle?.AverageExternalRoundTrip,
            SampleCount = engine.LastSampleSequence,
            UnreachableTargetShare = cycle?.ExternalIcmp.UnreachableShare,

            MonitoredTime = statistics.MonitoredTime,
            GapTime = statistics.GapTime,
            AvailabilityPercent = statistics.AvailabilityPercent,
            UpstreamAvailabilityPercent = statistics.UpstreamAvailabilityPercent,
            IncidentCount = statistics.IncidentCount,
            UpstreamIncidentCount = statistics.UpstreamIncidentCount,
            UpstreamDowntime = statistics.UpstreamDowntime,
            LocalDowntime = statistics.LocalDowntime,
            LongestUpstreamOutage = statistics.LongestUpstreamOutage,

            InterfaceName = cycle?.Link.InterfaceName,
            Medium = cycle?.Link.Medium ?? LinkMedium.Unknown,
            GatewayAddress = cycle?.Link.GatewayAddress,

            Gateway = cycle?.Gateway ?? default,
            ExternalIcmp = cycle?.ExternalIcmp ?? default,
            ExternalTcp = cycle?.ExternalTcp ?? default,
            Dns = Combine(cycle?.DnsIsp, cycle?.DnsPublic, cycle?.DnsSystem),
            Http = cycle?.Http ?? default,
        };
    }

    /// <summary>
    /// Folds the three resolver probes into one figure for the layer panel. The split
    /// between them still drives classification; it is only the summary display that
    /// treats DNS as a single layer, because that is how a person thinks about it.
    /// </summary>
    private static ProbeTally Combine(params ProbeTally?[] tallies)
    {
        var attempted = 0;
        var succeeded = 0;

        foreach (var tally in tallies)
        {
            if (tally is not { } value)
            {
                continue;
            }

            attempted += value.Attempted;
            succeeded += value.Succeeded;
        }

        return new ProbeTally(attempted, succeeded);
    }
}
