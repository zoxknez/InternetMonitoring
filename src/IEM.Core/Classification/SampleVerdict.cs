using IEM.Core.Model;

namespace IEM.Core.Classification;

/// <summary>
/// The verdict for one sample.
/// <para>
/// <see cref="State"/> is the stable machine-readable key; it is what goes into the raw
/// evidence log and what every downstream consumer keys off. <see cref="TechnicalDetail"/>
/// is a short English note for the raw log and for an operator's own technician. All
/// user-facing Serbian wording is rendered from <see cref="State"/> at presentation time,
/// so re-wording the UI can never alter recorded evidence.
/// </para>
/// </summary>
public sealed record SampleVerdict(NetworkState State, string TechnicalDetail)
{
    public Severity Severity => State.SeverityOf();

    public bool IsOutage => State.IsOutage();
}

/// <summary>
/// Signals that cannot be derived from a single cycle and must be carried across samples.
/// </summary>
public sealed record ClassificationContext
{
    public static readonly ClassificationContext Empty = new();

    /// <summary>The access point changed under the same SSID since the previous cycle.</summary>
    public bool BssidChanged { get; init; }

    /// <summary>Router-side signals indicate the CPE restarted (uptime reset, ARP relearned, DHCP renewed).</summary>
    public bool CpeRebootDetected { get; init; }

    /// <summary>Variation in external round trip over the recent window.</summary>
    public TimeSpan? Jitter { get; init; }
}

public sealed record ClassifierOptions
{
    public static readonly ClassifierOptions Default = new();

    public TimeSpan HighLatencyThreshold { get; init; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan HighJitterThreshold { get; init; } = TimeSpan.FromMilliseconds(50);
}
