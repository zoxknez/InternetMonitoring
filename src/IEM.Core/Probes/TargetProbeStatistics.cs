namespace IEM.Core.Probes;

/// <summary>
/// Persisted methodology definition for a target probe sample.
/// </summary>
public sealed record ProbeMethodology(
    int ProbeCount,
    int IntervalMs,
    int TimeoutMs,
    int PayloadBytes = 32,
    string SamplingMethod = "FixedInterval");

/// <summary>
/// RTT statistics computed strictly over received replies.
/// Invariant 35: TIMEOUT_IS_NEVER_SYNTHESIZED_AS_RTT.
/// </summary>
public sealed record RttStatistics(
    int SampleCount,
    double MinMs,
    double MedianMs,
    double P95Ms,
    double MaxMs,
    string PercentileMethod = ProbePercentileCalculator.MethodName);

/// <summary>
/// Statistical summary of multiple probe attempts for a single target and address family.
/// Invariants:
/// 33. LOCAL_PROBE_FAILURE_IS_NEVER_NETWORK_LOSS
/// 34. LOSS_RATIO_IS_NEVER_AVERAGED_ACROSS_TARGETS
/// 35. TIMEOUT_IS_NEVER_SYNTHESIZED_AS_RTT
/// 36. DELAY_VARIATION_ALWAYS_NAMES_ITS_METHOD
/// 37. PROBE_RESULT_PRESERVES_TARGET_AND_ADDRESS_FAMILY
/// 38. ICMP_NO_REPLY_DOES_NOT_PROVE_PACKET_DROP_LOCATION
/// </summary>
public sealed record TargetProbeStatistics(
    string TargetId,
    string TargetAddress,
    TargetAddressFamily AddressFamily,
    TargetProbeType ProbeType,
    DateTimeOffset SampleStartedUtc,
    DateTimeOffset SampleEndedUtc,
    ProbeMethodology Methodology,
    int ScheduledCount,
    int ExecutedCount,
    int EligibleCount,
    int ReplyCount,
    int NoReplyCount,
    int ExplicitErrorCount,
    int LocalFailureCount,
    double? NoReplyRatio,
    RttStatistics? Rtt,
    DelayVariationResult? DelayVariation)
{
    public static TargetProbeStatistics CreateFromAttempts(
        string targetId,
        string targetAddress,
        TargetAddressFamily addressFamily,
        TargetProbeType probeType,
        ProbeMethodology methodology,
        IReadOnlyList<TargetProbeAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        ArgumentNullException.ThrowIfNull(methodology);

        var startedUtc = attempts.Count > 0 ? attempts.Min(a => a.StartedUtc) : DateTimeOffset.UtcNow;
        var endedUtc = attempts.Count > 0 ? attempts.Max(a => a.StartedUtc) : startedUtc;

        var scheduled = methodology.ProbeCount;
        var executed = attempts.Count;
        var localFailures = attempts.Count(a => a.Outcome == ProbeOutcomeType.LocalExecutionFailure);
        var explicitErrors = attempts.Count(a => a.Outcome == ProbeOutcomeType.DestinationUnreachable);
        var noReplies = attempts.Count(a => a.Outcome == ProbeOutcomeType.NoReplyBeforeTimeout);
        var replies = attempts.Count(a => a.Outcome == ProbeOutcomeType.ReplyReceived);

        // Invariant 33: LOCAL_PROBE_FAILURE_IS_NEVER_NETWORK_LOSS
        // Local failures are removed from the eligible denominator
        var eligible = executed - localFailures;
        double? noReplyRatio = eligible > 0 ? (double)noReplies / eligible : null;

        // Invariant 35: TIMEOUT_IS_NEVER_SYNTHESIZED_AS_RTT
        // Extract chronological RTTs from successful replies only
        var successfulRtts = attempts
            .Where(a => a.Outcome == ProbeOutcomeType.ReplyReceived && a.RoundTripTimeMs.HasValue)
            .OrderBy(a => a.StartedUtc)
            .Select(a => a.RoundTripTimeMs!.Value)
            .ToList();

        RttStatistics? rttStats = null;
        if (successfulRtts.Count > 0)
        {
            var min = successfulRtts.Min();
            var max = successfulRtts.Max();
            var median = ProbePercentileCalculator.ComputePercentile(successfulRtts, 50);
            var p95 = ProbePercentileCalculator.ComputePercentile(successfulRtts, 95);

            rttStats = new RttStatistics(
                SampleCount: successfulRtts.Count,
                MinMs: min,
                MedianMs: median,
                P95Ms: p95,
                MaxMs: max);
        }

        // Invariant 36: DELAY_VARIATION_ALWAYS_NAMES_ITS_METHOD
        var delayVariation = RoundTripDelayVariationCalculator.Compute(successfulRtts);

        return new TargetProbeStatistics(
            TargetId: targetId,
            TargetAddress: targetAddress,
            AddressFamily: addressFamily,
            ProbeType: probeType,
            SampleStartedUtc: startedUtc,
            SampleEndedUtc: endedUtc,
            Methodology: methodology,
            ScheduledCount: scheduled,
            ExecutedCount: executed,
            EligibleCount: eligible,
            ReplyCount: replies,
            NoReplyCount: noReplies,
            ExplicitErrorCount: explicitErrors,
            LocalFailureCount: localFailures,
            NoReplyRatio: noReplyRatio,
            Rtt: rttStats,
            DelayVariation: delayVariation);
    }

    public string ToPresentationText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Cilj: {TargetAddress} ({AddressFamily})");
        sb.AppendLine();
        sb.AppendLine($"Probe                         {ExecutedCount}");
        sb.AppendLine($"Odgovori                      {ReplyCount}");
        sb.AppendLine($"Bez odgovora                   {NoReplyCount}");
        if (ExplicitErrorCount > 0)
        {
            sb.AppendLine($"Mrežne greške                 {ExplicitErrorCount}");
        }
        if (LocalFailureCount > 0)
        {
            sb.AppendLine($"Lokalni neuspesi probe        {LocalFailureCount}");
        }

        if (NoReplyRatio.HasValue)
        {
            sb.AppendLine($"Udeo bez odgovora            {NoReplyRatio.Value * 100.0:F1} %");
        }
        else
        {
            sb.AppendLine("Udeo bez odgovora            N/A (nema izvršenih proba)");
        }

        sb.AppendLine();
        sb.AppendLine("RTT");
        if (Rtt is not null)
        {
            sb.AppendLine($"  minimum                    {Rtt.MinMs:F0} ms");
            sb.AppendLine($"  medijana                   {Rtt.MedianMs:F0} ms");
            sb.AppendLine($"  P95                        {Rtt.P95Ms:F0} ms (N = {Rtt.SampleCount})");
            sb.AppendLine($"  maksimum                   {Rtt.MaxMs:F0} ms");
        }
        else
        {
            sb.AppendLine("  (nema primljenih odgovora za merenje RTT-a)");
        }

        sb.AppendLine();
        sb.AppendLine("Varijacija RTT-a (Metod: " + (DelayVariation?.Method ?? "N/A") + ")");
        if (DelayVariation?.MedianMs.HasValue == true)
        {
            sb.AppendLine($"  medijana                    {DelayVariation.MedianMs.Value:F0} ms");
            sb.AppendLine($"  P95                         {DelayVariation.P95Ms!.Value:F0} ms (N = {DelayVariation.SampleCount})");
        }
        else
        {
            sb.AppendLine("  (nedovoljno uzastopnih odgovora)");
        }

        sb.AppendLine();
        sb.AppendLine("Napomena: Izostanak ICMP odgovora ne određuje sam po sebi gde je paket izgubljen niti da li cilj filtrira ICMP.");

        return sb.ToString();
    }
}
