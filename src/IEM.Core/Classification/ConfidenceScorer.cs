using IEM.Core.Model;

namespace IEM.Core.Classification;

/// <summary>
/// Signals gathered over the life of an incident.
/// <para>
/// Every property is nullable on purpose. <see langword="null"/> means "we could not
/// check this", which is materially different from "we checked and it was false". The
/// scorer reports the difference instead of hiding it, because a report that quietly
/// treats unavailable evidence as supporting evidence is exactly the kind of thing an
/// operator is entitled to tear apart.
/// </para>
/// </summary>
public sealed record IncidentEvidence
{
    // ---- Local device: rules out the customer's own machine ------------------

    /// <summary>The adapter stayed up for the whole incident, so this was not a local link drop.</summary>
    public bool? LinkStayedUp { get; init; }

    /// <summary>Wired connection, so no Wi-Fi ambiguity at all.</summary>
    public bool? WiredConnection { get; init; }

    /// <summary>No adapter reset or driver fault was logged during the incident.</summary>
    public bool? NoAdapterReset { get; init; }

    /// <summary>The machine did not sleep or hibernate during the incident.</summary>
    public bool? NoSystemSleep { get; init; }

    /// <summary>The wall clock was not corrected during the incident.</summary>
    public bool? NoClockJump { get; init; }

    /// <summary>The link was not saturated by the customer's own traffic.</summary>
    public bool? NoLocalSaturation { get; init; }

    /// <summary>
    /// Busiest second of the machine's own traffic during the incident, in bytes per second,
    /// or null when the counters were never read.
    /// <para>
    /// Carried alongside the signal above rather than folded into it, because a reader
    /// deserves the figure and not only the verdict: "the line was busy" invites the question
    /// "how busy", and the answer is the difference between a stream in another room and a
    /// download that was using the whole connection.
    /// </para>
    /// </summary>
    public long? PeakLocalTrafficBytesPerSecond { get; init; }

    // ---- Wi-Fi link: rules out the radio between device and router -----------

    /// <summary>The SSID stayed visible in scans, so the access point kept broadcasting.</summary>
    public bool? SsidRemainedVisible { get; init; }

    /// <summary>Signal strength stayed healthy, so this was not a range problem.</summary>
    public bool? SignalHealthy { get; init; }

    /// <summary>The access point did not change, so this was not a roaming artefact.</summary>
    public bool? NoRoaming { get; init; }

    // ---- Router: places the fault beyond the CPE -----------------------------

    /// <summary>The gateway kept answering, so the path to the router was healthy.</summary>
    public bool? GatewayRemainedReachable { get; init; }

    /// <summary>Router-side status reported the WAN connection as down.</summary>
    public bool? RouterReportsWanDown { get; init; }

    /// <summary>The router did not restart during the incident.</summary>
    public bool? NoCpeReboot { get; init; }

    // ---- Upstream: positive evidence the fault is out on the operator's side --

    public bool? AllExternalIcmpFailed { get; init; }

    public bool? AllExternalTcpFailed { get; init; }

    public bool? TlsFailed { get; init; }

    public bool? PublicDnsFailed { get; init; }

    public bool? HttpFailed { get; init; }

    /// <summary>
    /// The trace reached a hop outside the home network.
    /// <para>
    /// Read in one direction only, which is why it is no longer phrased as "stopped at the
    /// operator's hop". A hop answering proves the packets got that far; nothing answering
    /// beyond it proves nothing about the next device, since routers are widely configured
    /// not to reply to expiring packets at all.
    /// </para>
    /// </summary>
    public bool? TraceLeftHomeNetwork { get; init; }

    /// <summary>The public address changed across the incident, so the WAN session was re-established.</summary>
    public bool? PublicAddressChanged { get; init; }
}

/// <summary>
/// Scores an incident from its evidence, against a specific conclusion.
/// Deterministic and side-effect free.
/// <para>
/// Aware of what is being proved, because the same signals do not mean the same thing for
/// every conclusion. The router answering throughout is the central fact when arguing the
/// fault lay beyond it, and completely beside the point when the router is the thing that
/// failed. Scoring both against one fixed list produced numbers that looked precise and
/// meant very little.
/// </para>
/// </summary>
public static class ConfidenceScorer
{
    private sealed record Signal(string Key, int Weight, Func<IncidentEvidence, bool?> Read);

    // Weights encode how much each signal actually narrows the cause. Ruling out the
    // customer's own equipment is worth more than another failing external target,
    // because additional external failures are highly correlated with each other
    // while each local exclusion removes a whole competing explanation.
    private static readonly Signal[] All =
    [
        new("link.stayedUp",            10, e => e.LinkStayedUp),
        new("link.wired",                6, e => e.WiredConnection),
        new("device.noAdapterReset",     8, e => e.NoAdapterReset),
        new("device.noSleep",            8, e => e.NoSystemSleep),
        new("device.noClockJump",        5, e => e.NoClockJump),
        new("device.noSaturation",       5, e => e.NoLocalSaturation),

        new("wifi.ssidVisible",         10, e => e.SsidRemainedVisible),
        new("wifi.signalHealthy",        7, e => e.SignalHealthy),
        new("wifi.noRoaming",            5, e => e.NoRoaming),

        new("cpe.gatewayReachable",     12, e => e.GatewayRemainedReachable),
        new("cpe.wanReportedDown",       9, e => e.RouterReportsWanDown),
        new("cpe.noReboot",              8, e => e.NoCpeReboot),

        new("upstream.icmpFailed",       6, e => e.AllExternalIcmpFailed),
        new("upstream.tcpFailed",        7, e => e.AllExternalTcpFailed),
        new("upstream.tlsFailed",        4, e => e.TlsFailed),
        new("upstream.publicDnsFailed",  5, e => e.PublicDnsFailed),
        new("upstream.httpFailed",       4, e => e.HttpFailed),
        new("upstream.traceLeftNetwork",10, e => e.TraceLeftHomeNetwork),
        new("upstream.publicIpChanged",  6, e => e.PublicAddressChanged),
    ];

    /// <summary>
    /// Which signals bear on which conclusion, and which way each one has to point.
    /// <para>
    /// Relevance alone was not enough, and the gap ran the wrong way for every conclusion
    /// except the upstream one. A signal is phrased once - "the SSID stayed visible", "the
    /// gateway kept answering", "the router did not restart" - and true was taken as support
    /// whatever was being argued. But those readings mean opposite things depending on the
    /// claim. The SSID vanishing is the whole case for a failed access-point radio, and it
    /// was being scored as evidence against it; so was the gateway falling silent under
    /// <c>GatewayDown</c>, the router restarting under <c>CpeReboot</c>, and the adapter
    /// dropping under <c>AdapterDown</c>. Each of those conclusions was penalised by the one
    /// fact that established it, and the band printed in the report came out a step low.
    /// </para>
    /// <para>
    /// So each conclusion states the value it expects. Anything not listed is scored
    /// <see cref="EvidenceOutcome.NotApplicable"/> and stays out of both numbers entirely -
    /// it neither supports the conclusion nor counts as a gap, because it was never relevant.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, bool> RelevantTo(NetworkState target) => target switch
    {
        // The fault is past the router, so the case rests on ruling out everything nearer
        // and on several independent external targets failing at once. Every signal here
        // supports the conclusion by being true.
        NetworkState.CpeUpstreamUnreachable => Expect(
            ("link.stayedUp", true), ("link.wired", true), ("device.noAdapterReset", true),
            ("device.noSleep", true), ("device.noClockJump", true), ("device.noSaturation", true),
            ("wifi.ssidVisible", true), ("wifi.signalHealthy", true), ("wifi.noRoaming", true),
            ("cpe.gatewayReachable", true), ("cpe.wanReportedDown", true), ("cpe.noReboot", true),
            ("upstream.icmpFailed", true), ("upstream.tcpFailed", true), ("upstream.tlsFailed", true),
            ("upstream.publicDnsFailed", true), ("upstream.httpFailed", true),
            ("upstream.traceLeftNetwork", true), ("upstream.publicIpChanged", true)),

        // The access point stopped serving. External failures say nothing here - of course
        // they failed, the radio was gone - so counting them would inflate the score with
        // signals that follow automatically from the fault itself. The SSID being absent is
        // the case, not a mark against it.
        NetworkState.WifiRadioDown => Expect(
            ("device.noAdapterReset", true), ("device.noSleep", true), ("device.noClockJump", true),
            ("wifi.ssidVisible", false), ("wifi.signalHealthy", true), ("wifi.noRoaming", true)),

        // The router restarted, so its not having restarted is what would contradict this.
        NetworkState.CpeReboot => Expect(
            ("link.stayedUp", true), ("device.noAdapterReset", true), ("device.noSleep", true),
            ("device.noClockJump", true),
            ("cpe.noReboot", false), ("cpe.wanReportedDown", true), ("upstream.publicIpChanged", true)),

        // The path to the router failed, so nothing beyond it is informative, and the
        // gateway falling silent is the finding rather than a problem with it.
        NetworkState.GatewayDown => Expect(
            ("link.stayedUp", true), ("link.wired", true), ("device.noAdapterReset", true),
            ("device.noSleep", true),
            ("wifi.ssidVisible", true), ("wifi.signalHealthy", true), ("wifi.noRoaming", true),
            ("cpe.gatewayReachable", false)),

        // This machine's own adapter went down, which is precisely the link not staying up.
        // Nothing about the network beyond it can help or hurt.
        NetworkState.AdapterDown => Expect(
            ("link.stayedUp", false), ("device.noAdapterReset", true), ("device.noSleep", true),
            ("device.noClockJump", true)),

        // Nothing answered and the router could not be tested. Little is provable, and the
        // narrow signal set is what makes coverage - and so the band - come out low.
        NetworkState.InternetDown => Expect(
            ("link.stayedUp", true), ("device.noAdapterReset", true), ("device.noSleep", true),
            ("device.noClockJump", true),
            ("upstream.icmpFailed", true), ("upstream.tcpFailed", true), ("upstream.httpFailed", true)),

        _ => All.ToDictionary(s => s.Key, _ => true, StringComparer.Ordinal),
    };

    private static IReadOnlyDictionary<string, bool> Expect(params (string Key, bool Supporting)[] signals) =>
        signals.ToDictionary(s => s.Key, s => s.Supporting, StringComparer.Ordinal);

    /// <summary>
    /// How far the evidence goes towards <paramref name="target"/> being the right reading
    /// of this incident.
    /// <para>
    /// Returns support and coverage separately. Support is the share of what was checked
    /// that backs the conclusion; coverage is the share of what mattered that could be
    /// checked at all. Two supporting signals out of nineteen relevant ones is not a strong
    /// case, however clean those two were, and only the second number says so.
    /// </para>
    /// </summary>
    public static ConfidenceScore Score(NetworkState target, IncidentEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var relevant = RelevantTo(target);

        var items = new List<EvidenceItem>(All.Length);
        var supportingWeight = 0;
        var checkedWeight = 0;
        var relevantWeight = 0;

        foreach (var signal in All)
        {
            if (!relevant.TryGetValue(signal.Key, out var supporting))
            {
                items.Add(new EvidenceItem(signal.Key, EvidenceOutcome.NotApplicable, signal.Weight));
                continue;
            }

            relevantWeight += signal.Weight;
            var value = signal.Read(evidence);

            // Measured against the value this conclusion expects, not against true.
            items.Add(new EvidenceItem(signal.Key, value switch
            {
                null => EvidenceOutcome.Unavailable,
                _ when value.Value == supporting => EvidenceOutcome.Supports,
                _ => EvidenceOutcome.Contradicts,
            }, signal.Weight));

            if (value is null)
            {
                continue;
            }

            checkedWeight += signal.Weight;

            if (value.Value == supporting)
            {
                supportingWeight += signal.Weight;
            }
        }

        // Nothing relevant could be checked. Claiming any confidence would be an invention.
        if (checkedWeight == 0 || relevantWeight == 0)
        {
            return new ConfidenceScore(0, 0, items);
        }

        var support = (int)Math.Round(100d * supportingWeight / checkedWeight, MidpointRounding.AwayFromZero);
        var coverage = (int)Math.Round(100d * checkedWeight / relevantWeight, MidpointRounding.AwayFromZero);

        return new ConfidenceScore(Math.Clamp(support, 0, 100), Math.Clamp(coverage, 0, 100), items);
    }
}
