using System.Text.Json;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// Unit and forensic tests for Phase 3.0-6: Target Probe Loss & Round-Trip Delay Variation.
/// Invariants 33-38.
/// </summary>
public sealed class TargetProbeTests
{
    private static readonly ProbeMethodology DefaultMethodology = new(
        ProbeCount: 20,
        IntervalMs: 500,
        TimeoutMs: 1500,
        PayloadBytes: 32,
        SamplingMethod: "FixedInterval");

    [Fact]
    public void Twenty_successes_give_zero_no_reply_ratio()
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = Enumerable.Range(1, 20).Select(i => new TargetProbeAttempt(
            AttemptId: $"att-{i}",
            TargetId: "CloudflareDNS",
            TargetAddress: "1.1.1.1",
            AddressFamily: TargetAddressFamily.IPv4,
            ProbeType: TargetProbeType.IcmpEcho,
            Sequence: i,
            PayloadBytes: 32,
            StartedUtc: now.AddMilliseconds(i * 500),
            TimeoutMs: 1500,
            Outcome: ProbeOutcomeType.ReplyReceived,
            ReplyAddress: "1.1.1.1",
            RoundTripTimeMs: 15.0 + i)).ToList();

        var stats = TargetProbeStatistics.CreateFromAttempts(
            "CloudflareDNS",
            "1.1.1.1",
            TargetAddressFamily.IPv4,
            TargetProbeType.IcmpEcho,
            DefaultMethodology,
            attempts);

        Assert.Equal(20, stats.ScheduledCount);
        Assert.Equal(20, stats.ExecutedCount);
        Assert.Equal(20, stats.EligibleCount);
        Assert.Equal(20, stats.ReplyCount);
        Assert.Equal(0, stats.NoReplyCount);
        Assert.Equal(0.0, stats.NoReplyRatio);
        Assert.NotNull(stats.Rtt);
        Assert.Equal(20, stats.Rtt.SampleCount);
    }

    [Fact]
    public void Nineteen_replies_and_one_timeout_give_five_percent()
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = new List<TargetProbeAttempt>();

        for (var i = 1; i <= 19; i++)
        {
            attempts.Add(new TargetProbeAttempt(
                AttemptId: $"att-{i}",
                TargetId: "GoogleDNS",
                TargetAddress: "8.8.8.8",
                AddressFamily: TargetAddressFamily.IPv4,
                ProbeType: TargetProbeType.IcmpEcho,
                Sequence: i,
                PayloadBytes: 32,
                StartedUtc: now.AddMilliseconds(i * 500),
                TimeoutMs: 1500,
                Outcome: ProbeOutcomeType.ReplyReceived,
                ReplyAddress: "8.8.8.8",
                RoundTripTimeMs: 20.0));
        }

        attempts.Add(new TargetProbeAttempt(
            AttemptId: "att-20",
            TargetId: "GoogleDNS",
            TargetAddress: "8.8.8.8",
            AddressFamily: TargetAddressFamily.IPv4,
            ProbeType: TargetProbeType.IcmpEcho,
            Sequence: 20,
            PayloadBytes: 32,
            StartedUtc: now.AddMilliseconds(20 * 500),
            TimeoutMs: 1500,
            Outcome: ProbeOutcomeType.NoReplyBeforeTimeout));

        var stats = TargetProbeStatistics.CreateFromAttempts(
            "GoogleDNS",
            "8.8.8.8",
            TargetAddressFamily.IPv4,
            TargetProbeType.IcmpEcho,
            DefaultMethodology,
            attempts);

        Assert.Equal(20, stats.ExecutedCount);
        Assert.Equal(19, stats.ReplyCount);
        Assert.Equal(1, stats.NoReplyCount);
        Assert.Equal(0.05, stats.NoReplyRatio);
        Assert.NotNull(stats.Rtt);
        Assert.Equal(19, stats.Rtt.SampleCount); // Only successful replies in RTT
    }

    [Fact]
    public void Local_failure_is_excluded_from_network_loss_denominator()
    {
        // Invariant 33: LOCAL_PROBE_FAILURE_IS_NEVER_NETWORK_LOSS
        // 10 scheduled: 8 replies, 1 timeout, 1 local socket failure
        // Executed = 10, LocalFailures = 1 -> Eligible = 9, NoReplies = 1 -> NoReplyRatio = 1 / 9 = 11.11%
        var now = DateTimeOffset.UtcNow;
        var attempts = new List<TargetProbeAttempt>();

        for (var i = 1; i <= 8; i++)
        {
            attempts.Add(new TargetProbeAttempt(
                $"att-{i}", "TargetA", "10.0.0.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
                i, 32, now.AddMilliseconds(i * 500), 1500, ProbeOutcomeType.ReplyReceived, "10.0.0.1", 10.0));
        }

        attempts.Add(new TargetProbeAttempt(
            "att-9", "TargetA", "10.0.0.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            9, 32, now.AddMilliseconds(9 * 500), 1500, ProbeOutcomeType.NoReplyBeforeTimeout));

        attempts.Add(new TargetProbeAttempt(
            "att-10", "TargetA", "10.0.0.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            10, 32, now.AddMilliseconds(10 * 500), 1500, ProbeOutcomeType.LocalExecutionFailure, DiagnosticMessage: "WSAENOBUFS"));

        var stats = TargetProbeStatistics.CreateFromAttempts(
            "TargetA", "10.0.0.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            new ProbeMethodology(10, 500, 1500), attempts);

        Assert.Equal(10, stats.ExecutedCount);
        Assert.Equal(1, stats.LocalFailureCount);
        Assert.Equal(9, stats.EligibleCount);
        Assert.Equal(8, stats.ReplyCount);
        Assert.Equal(1, stats.NoReplyCount);
        Assert.NotNull(stats.NoReplyRatio);
        Assert.Equal(1.0 / 9.0, stats.NoReplyRatio.Value, 5);
    }

    [Fact]
    public void Destination_unreachable_is_not_silently_counted_as_timeout()
    {
        var now = DateTimeOffset.UtcNow;
        var attempts = new List<TargetProbeAttempt>
        {
            new("att-1", "TargetB", "192.168.1.50", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, 1, 32, now, 1500, ProbeOutcomeType.ReplyReceived, "192.168.1.50", 5.0),
            new("att-2", "TargetB", "192.168.1.50", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, 2, 32, now.AddSeconds(1), 1500, ProbeOutcomeType.DestinationUnreachable, "192.168.1.1", DiagnosticMessage: "HostUnreachable"),
            new("att-3", "TargetB", "192.168.1.50", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, 3, 32, now.AddSeconds(2), 1500, ProbeOutcomeType.NoReplyBeforeTimeout),
        };

        var stats = TargetProbeStatistics.CreateFromAttempts(
            "TargetB", "192.168.1.50", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            new ProbeMethodology(3, 1000, 1500), attempts);

        Assert.Equal(1, stats.ReplyCount);
        Assert.Equal(1, stats.ExplicitErrorCount);
        Assert.Equal(1, stats.NoReplyCount);
        Assert.Equal(3, stats.EligibleCount);
        Assert.Equal(1.0 / 3.0, stats.NoReplyRatio!.Value, 5); // 1 timeout out of 3 eligible
    }

    [Fact]
    public void Empty_sample_has_no_loss_ratio()
    {
        var stats = TargetProbeStatistics.CreateFromAttempts(
            "TargetC", "10.0.0.2", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            new ProbeMethodology(0, 500, 1500), Array.Empty<TargetProbeAttempt>());

        Assert.Equal(0, stats.ExecutedCount);
        Assert.Equal(0, stats.EligibleCount);
        Assert.Null(stats.NoReplyRatio);
        Assert.Null(stats.Rtt);
    }

    [Fact]
    public void Timeout_has_no_synthetic_RTT_and_RTT_statistics_use_only_received_replies()
    {
        // Invariant 35: TIMEOUT_IS_NEVER_SYNTHESIZED_AS_RTT
        var now = DateTimeOffset.UtcNow;
        var attempts = new List<TargetProbeAttempt>
        {
            new("att-1", "T", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, 1, 32, now, 1500, ProbeOutcomeType.ReplyReceived, "1.1.1.1", 10.0),
            new("att-2", "T", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, 2, 32, now.AddSeconds(1), 1500, ProbeOutcomeType.NoReplyBeforeTimeout),
            new("att-3", "T", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, 3, 32, now.AddSeconds(2), 1500, ProbeOutcomeType.ReplyReceived, "1.1.1.1", 30.0),
        };

        var stats = TargetProbeStatistics.CreateFromAttempts(
            "T", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            new ProbeMethodology(3, 1000, 1500), attempts);

        Assert.NotNull(stats.Rtt);
        Assert.Equal(2, stats.Rtt.SampleCount);
        Assert.Equal(10.0, stats.Rtt.MinMs);
        Assert.Equal(30.0, stats.Rtt.MaxMs);
        Assert.Equal(10.0, stats.Rtt.MedianMs); // Nearest rank median: rank = ceil(0.5 * 2) = 1 -> sorted[0] = 10.0
    }


    [Fact]
    public void Nearest_rank_percentile_is_exact_and_deterministic()
    {
        // Test with sample of 10 values: 10, 20, 30, 40, 50, 60, 70, 80, 90, 100
        var values = Enumerable.Range(1, 10).Select(i => (double)i * 10).ToList();

        // P50: ceil(0.5 * 10) = 5 -> index 4 = 50.0
        var median = ProbePercentileCalculator.ComputePercentile(values, 50);
        Assert.Equal(50.0, median);

        // P95: ceil(0.95 * 10) = 10 -> index 9 = 100.0
        var p95 = ProbePercentileCalculator.ComputePercentile(values, 95);
        Assert.Equal(100.0, p95);

        // P90: ceil(0.9 * 10) = 9 -> index 8 = 90.0
        var p90 = ProbePercentileCalculator.ComputePercentile(values, 90);
        Assert.Equal(90.0, p90);
    }

    [Fact]
    public void Delay_variation_algorithm_is_deterministic_and_names_its_method()
    {
        // Invariant 36: DELAY_VARIATION_ALWAYS_NAMES_ITS_METHOD
        var rtts = new List<double> { 10.0, 15.0, 12.0, 20.0 };
        // Variations: |15-10|=5, |12-15|=3, |20-12|=8 -> sorted: 3, 5, 8 (N=3)
        // Median (P50): ceil(0.5 * 3) = 2 -> 5.0
        // P95: ceil(0.95 * 3) = 3 -> 8.0

        var result = RoundTripDelayVariationCalculator.Compute(rtts);

        Assert.Equal("ConsecutiveReplyAbsoluteDifference", result.Method);
        Assert.Equal(3, result.SampleCount);
        Assert.Equal(5.0, result.MedianMs);
        Assert.Equal(8.0, result.P95Ms);
    }

    [Fact]
    public void Targets_and_address_families_are_kept_separate_Invariants_34_and_37()
    {
        var now = DateTimeOffset.UtcNow;
        var attemptsV4 = Enumerable.Range(1, 10).Select(i => new TargetProbeAttempt(
            $"v4-{i}", "CloudflareDNS", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            i, 32, now.AddMilliseconds(i * 100), 1000, ProbeOutcomeType.ReplyReceived, "1.1.1.1", 12.0)).ToList();

        var attemptsV6 = Enumerable.Range(1, 10).Select(i => new TargetProbeAttempt(
            $"v6-{i}", "CloudflareDNS", "2606:4700:4700::1111", TargetAddressFamily.IPv6, TargetProbeType.IcmpEcho,
            i, 32, now.AddMilliseconds(i * 100), 1000, ProbeOutcomeType.NoReplyBeforeTimeout)).ToList();

        var statsV4 = TargetProbeStatistics.CreateFromAttempts("CloudflareDNS", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, DefaultMethodology, attemptsV4);
        var statsV6 = TargetProbeStatistics.CreateFromAttempts("CloudflareDNS", "2606:4700:4700::1111", TargetAddressFamily.IPv6, TargetProbeType.IcmpEcho, DefaultMethodology, attemptsV6);

        // IPv4 is 0% loss, IPv6 is 100% loss - never merged into 50%
        Assert.Equal(0.0, statsV4.NoReplyRatio);
        Assert.Equal(1.0, statsV6.NoReplyRatio);
        Assert.Equal(TargetAddressFamily.IPv4, statsV4.AddressFamily);
        Assert.Equal(TargetAddressFamily.IPv6, statsV6.AddressFamily);
    }

    [Fact]
    public void Golden_forensic_packet_loss_fixtures_verification()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "PacketLoss");
        if (!Directory.Exists(fixtureDir))
        {
            fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "PacketLoss"));
        }
        Directory.CreateDirectory(fixtureDir);

        var now = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

        // 1. all-replies
        var allRepliesAttempts = Enumerable.Range(1, 20).Select(i => new TargetProbeAttempt(
            $"all-{i}", "TargetA", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            i, 32, now.AddSeconds(i), 1500, ProbeOutcomeType.ReplyReceived, "1.1.1.1", 10.0 + (i % 5))).ToList();
        var allRepliesStats = TargetProbeStatistics.CreateFromAttempts("TargetA", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, DefaultMethodology, allRepliesAttempts);

        var allRepliesFile = Path.Combine(fixtureDir, "all-replies.json");
        File.WriteAllText(allRepliesFile, JsonSerializer.Serialize(allRepliesStats, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(0.0, allRepliesStats.NoReplyRatio);
        Assert.Equal(20, allRepliesStats.ReplyCount);

        // 2. one-timeout
        var oneTimeoutAttempts = allRepliesAttempts.Take(19).ToList();
        oneTimeoutAttempts.Add(new TargetProbeAttempt(
            "timeout-20", "TargetA", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho,
            20, 32, now.AddSeconds(20), 1500, ProbeOutcomeType.NoReplyBeforeTimeout));
        var oneTimeoutStats = TargetProbeStatistics.CreateFromAttempts("TargetA", "1.1.1.1", TargetAddressFamily.IPv4, TargetProbeType.IcmpEcho, DefaultMethodology, oneTimeoutAttempts);

        var oneTimeoutFile = Path.Combine(fixtureDir, "one-timeout.json");
        File.WriteAllText(oneTimeoutFile, JsonSerializer.Serialize(oneTimeoutStats, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(0.05, oneTimeoutStats.NoReplyRatio);
        Assert.Equal(19, oneTimeoutStats.ReplyCount);
        Assert.Equal(1, oneTimeoutStats.NoReplyCount);
    }
}
