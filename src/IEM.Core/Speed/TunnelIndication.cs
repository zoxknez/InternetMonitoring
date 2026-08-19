namespace IEM.Core.Speed;

/// <summary>
/// Whether the network path appears to be passing through a tunnel or VPN.
/// </summary>
public enum TunnelState
{
    /// <summary>
    /// Not established or not checked. Default resting state; never a pass.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// No tunnel indicators were found for the examined interfaces.
    /// </summary>
    NotDetected,

    /// <summary>
    /// Tunnel or VPN characteristics were detected on the examined network path.
    /// </summary>
    Detected,
}

/// <summary>
/// What was established about possible tunnels on the path.
/// <para>
/// An inference, NOT a fact. Tunnel detection rests on heuristics, interface names, linkinfo
/// kinds and routing indicators that can change over time. Path agreement is an observation
/// and must never depend on tunnel detection.
/// </para>
/// </summary>
/// <param name="State">The conclusion about tunnel presence.</param>
/// <param name="Signals">Observed signals (e.g. interface names, linkinfo.kind, routing rules).</param>
/// <param name="DetectorVersion">Version of the heuristic detector that produced this conclusion.</param>
/// <param name="Reason">Human-readable explanation of the conclusion.</param>
public sealed record TunnelIndication(
    TunnelState State,
    IReadOnlyList<string> Signals,
    string DetectorVersion,
    string? Reason = null)
{
    public const string CurrentDetectorVersion = "1.0.0";

    public static readonly TunnelIndication Unknown = new(TunnelState.Unknown, [], CurrentDetectorVersion);
    public static readonly TunnelIndication NotDetected = new(TunnelState.NotDetected, [], CurrentDetectorVersion);

    public static TunnelIndication FromSignals(IReadOnlyList<string> signals, string? reason = null) =>
        new(
            signals.Count > 0 ? TunnelState.Detected : TunnelState.NotDetected,
            signals,
            CurrentDetectorVersion,
            reason);

    public bool Equals(TunnelIndication? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return State == other.State &&
               DetectorVersion == other.DetectorVersion &&
               Reason == other.Reason &&
               Signals.SequenceEqual(other.Signals);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(State);
        hash.Add(DetectorVersion);
        hash.Add(Reason);
        foreach (var signal in Signals)
        {
            hash.Add(signal);
        }
        return hash.ToHashCode();
    }
}

