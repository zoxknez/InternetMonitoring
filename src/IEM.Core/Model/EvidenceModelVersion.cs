namespace IEM.Core.Model;

/// <summary>
/// Which version of the reasoning produced a conclusion.
/// <para>
/// The raw probe results mean the same thing forever: an address answered, or it did not.
/// The algorithm that turns those into "the fault was isolated beyond your router" does not
/// - it has already changed once, in exactly the direction that matters, when
/// <c>InternetDown</c> stopped counting as proven and a cached success stopped proving
/// reachability. A report produced before that change reached different conclusions from the
/// same measurements.
/// </para>
/// <para>
/// So every report carries these. Years later it must still be possible to say which
/// reasoning produced a given figure, rather than leaving a reader to assume the current one
/// and find the numbers do not reproduce.
/// </para>
/// </summary>
public static class EvidenceModelVersion
{
    /// <summary>
    /// Layout of the raw chain.
    /// <para>
    /// 2 adds per-sample path (<c>iface</c>, <c>src</c>, <c>bound</c>), outage segments with
    /// a correlation id in place of a single incident record, and the environment baseline.
    /// </para>
    /// <para>
    /// 3 adds <c>localBps</c>: how much traffic the machine itself was putting through the
    /// link at that sample. Without it there is no way to tell a genuine outage from the
    /// computer having filled the line on its own - and the confidence signal that claimed
    /// to have ruled that out had never had anything to read.
    /// </para>
    /// </summary>
    public const int SchemaVersion = 3;

    /// <summary>
    /// Rules mapping observations to a <see cref="NetworkState"/>.
    /// <para>
    /// 2.2 stops counting a success measured before the trouble began, which is what kept
    /// short outages being reclassified as harmless filtering.
    /// </para>
    /// </summary>
    public const string ClassifierVersion = "2.2.0";

    /// <summary>
    /// Rules mapping a state to a <see cref="FaultDomain"/>.
    /// <para>
    /// 2.0 replaces the binary "operator or not" with how far along the path the fault was
    /// isolated, and stops treating <c>InternetDown</c> as proven against the operator.
    /// </para>
    /// </summary>
    public const string AttributionModelVersion = "2.0";

    /// <summary>
    /// Rules turning an incident's evidence into a support and coverage band.
    /// <para>
    /// 1.1 makes each signal's direction depend on what is being argued. Until then a
    /// signal counted as support by being true whatever the conclusion, so the SSID
    /// vanishing, the gateway falling silent, the router restarting and the adapter
    /// dropping were each scored against the very conclusion they established. Reports
    /// written by 1.0 carry a band one step low for those four states.
    /// </para>
    /// </summary>
    public const string ConfidenceModelVersion = "1.1";
}
