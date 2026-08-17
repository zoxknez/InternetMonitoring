using System.Diagnostics;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// Regression tests for P0-1 and P0-2.
/// <para>
/// These two are the reason a short outage could vanish from the record entirely. The
/// sampling cycle used to wait for every probe, so a two-second TCP timeout set the pace
/// during an outage - and a cached handshake from before the trouble kept insisting the
/// line was fine, turning a real outage into harmless filtering.
/// </para>
/// </summary>
public sealed class ObservationStoreTests
{
    private static ProbeResult Result(
        ProbeKind kind,
        ProbeScope scope,
        string target,
        bool succeeded,
        long startedAt,
        long completedAt,
        DnsResolverRole? dnsRole = null) =>
        new(kind, scope, target,
            succeeded ? ProbeOutcome.Success : ProbeOutcome.TimedOut,
            succeeded ? TimeSpan.FromMilliseconds(20) : null)
        {
            StartedAtTicks = startedAt,
            CompletedAtTicks = completedAt,
            DnsRole = dnsRole,
        };

    private static long Seconds(double value) => (long)(value * ManualClock.TicksPerSecondValue);

    // ---- P0-1: the tick never waits ----------------------------------------

    /// <summary>
    /// The aggregator reads a shelf. It must return at once even while a probe that was
    /// sent two seconds ago has still not answered - which is precisely the state a real
    /// outage puts every TCP connect into.
    /// </summary>
    [Fact]
    public async Task Reading_the_store_never_waits_for_a_probe_still_in_flight()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        using var probeStarted = new SemaphoreSlim(0, 1);
        using var releaseProbe = new SemaphoreSlim(0, 1);

        var pendingProbe = Task.Run(async () =>
        {
            probeStarted.Release();
            await releaseProbe.WaitAsync();
            store.Record(Result(ProbeKind.TcpConnect, ProbeScope.External, "1.1.1.1:443", false, 0, Seconds(2)));
        });

        await probeStarted.WaitAsync();

        // Twenty ticks while that probe hangs. Before the split, the cycle awaited the
        // probe and none of these would have happened.
        var elapsed = Stopwatch.StartNew();

        for (var i = 0; i < 20; i++)
        {
            store.Snapshot();
        }

        elapsed.Stop();

        Assert.True(
            elapsed.ElapsedMilliseconds < 200,
            $"twenty reads took {elapsed.ElapsedMilliseconds} ms while a probe was pending");

        releaseProbe.Release();
        await pendingProbe;
    }

    [Fact]
    public void An_empty_store_reads_as_nothing_known()
    {
        var store = new ObservationStore(new ManualClock());

        Assert.Empty(store.Snapshot());
        Assert.Null(store.SuspicionStartedAtTicks);
    }

    [Fact]
    public void A_later_result_replaces_its_predecessor_for_the_same_probe()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", true, 0, 0));
        clock.Advance(TimeSpan.FromMilliseconds(200));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, Seconds(0.2), Seconds(0.2)));

        var snapshot = store.Snapshot();

        Assert.Single(snapshot);
        Assert.False(snapshot[0].Succeeded);
    }

    // ---- P0-2: a measurement cannot testify about trouble it predates ------

    /// <summary>
    /// The headline rule. A handshake that succeeded before the trouble started is not
    /// evidence that the trouble is not happening.
    /// </summary>
    [Fact]
    public void A_success_measured_before_the_trouble_stops_counting()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        // TLS succeeded at t=0, well inside its lifetime.
        store.Record(Result(ProbeKind.TlsHandshake, ProbeScope.External, "one.one.one.one:443", true, 0, 0));

        // Five seconds later the fast probes start failing.
        clock.Advance(TimeSpan.FromSeconds(5));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, Seconds(5), Seconds(5)));

        var snapshot = store.Snapshot();
        var tls = snapshot.Single(r => r.Kind == ProbeKind.TlsHandshake);

        Assert.Equal(Freshness.Stale, tls.Freshness);
        Assert.False(tls.ProvesReachability);
        Assert.True(tls.Succeeded, "the success itself is still recorded, only its authority is withdrawn");
    }

    [Fact]
    public void A_success_measured_after_the_trouble_began_still_counts()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, 0, 0));

        clock.Advance(TimeSpan.FromSeconds(1));
        store.Record(Result(ProbeKind.TcpConnect, ProbeScope.External, "8.8.8.8:443", true, Seconds(1), Seconds(1)));

        var tcp = store.Snapshot().Single(r => r.Kind == ProbeKind.TcpConnect);

        Assert.Equal(Freshness.Fresh, tcp.Freshness);
        Assert.True(tcp.ProvesReachability);
    }

    [Fact]
    public void Suspicion_begins_when_the_failing_probe_finished_not_when_it_was_noticed()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        clock.Advance(TimeSpan.FromSeconds(3));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, Seconds(1), Seconds(3)));

        // The connection was already in trouble while that probe was timing out.
        Assert.Equal(Seconds(3), store.SuspicionStartedAtTicks);
    }

    [Fact]
    public void Suspicion_clears_only_once_every_fast_probe_is_succeeding_again()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, 0, 0));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "8.8.8.8", false, 0, 0));

        clock.Advance(TimeSpan.FromSeconds(1));

        // One target recovers. That is not enough - a single lucky ping must not restore
        // trust in a shelf full of stale results while the rest of the link is still down.
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", true, Seconds(1), Seconds(1)));
        Assert.NotNull(store.SuspicionStartedAtTicks);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "8.8.8.8", true, Seconds(1), Seconds(1)));
        Assert.Null(store.SuspicionStartedAtTicks);
    }

    /// <summary>
    /// A target that stops being probed must not hold suspicion open forever.
    /// <para>
    /// The IPv6 addresses are dropped from the round the moment the machine loses its global
    /// IPv6 address - which is what a router restarting mid-outage does. Their last answer
    /// was a failure and nothing ever overwrites it. Left counting, that single dead entry
    /// marks every later handshake stale, nothing can prove reachability again, and the tool
    /// reports a permanent outage on a connection that came back minutes ago.
    /// </para>
    /// </summary>
    [Fact]
    public void A_target_that_stopped_being_probed_does_not_hold_suspicion_open()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        // Both families failing: the outage.
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, 0, 0));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "2606:4700:4700::1111", false, 0, 0));

        Assert.NotNull(store.SuspicionStartedAtTicks);

        // IPv6 is gone from the machine, so that target is never probed again. IPv4 recovers.
        clock.Advance(TimeSpan.FromSeconds(10));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", true, Seconds(10), Seconds(10)));

        Assert.Null(store.SuspicionStartedAtTicks);
    }

    /// <summary>
    /// The mirror image, which must keep working: a target still being probed and still
    /// failing does hold suspicion open, however long the outage runs.
    /// </summary>
    [Fact]
    public void A_target_that_is_still_failing_keeps_suspicion_open()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, 0, 0));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "8.8.8.8", false, 0, 0));

        clock.Advance(TimeSpan.FromSeconds(10));

        // Both still being probed. One recovers, the other is refreshed and still failing.
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "8.8.8.8", false, Seconds(10), Seconds(10)));
        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", true, Seconds(10), Seconds(10)));

        Assert.NotNull(store.SuspicionStartedAtTicks);
    }

    /// <summary>
    /// Slow probes run too rarely to mark the start of anything precisely, so their failure
    /// must not backdate the moment trouble began.
    /// </summary>
    [Fact]
    public void A_failing_slow_probe_does_not_by_itself_raise_suspicion()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Http, ProbeScope.External, "http://example/connecttest", false, 0, 0));

        Assert.Null(store.SuspicionStartedAtTicks);
    }

    // ---- Lifetimes ---------------------------------------------------------

    [Fact]
    public void A_result_older_than_its_family_lifetime_expires()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", true, 0, 0));

        // External ICMP lives three seconds.
        clock.Advance(TimeSpan.FromSeconds(4));

        var icmp = store.Snapshot().Single();

        Assert.Equal(Freshness.Expired, icmp.Freshness);
        Assert.False(icmp.ProvesReachability);
    }

    /// <summary>
    /// Each family gets its own lifetime. A single universal maximum age is wrong: half a
    /// second is ancient for a gateway ping and current for an HTTP fetch.
    /// </summary>
    [Fact]
    public void Families_expire_on_their_own_schedules()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", true, 0, 0));
        store.Record(Result(ProbeKind.Http, ProbeScope.External, "http://example/connecttest", true, 0, 0));

        clock.Advance(TimeSpan.FromSeconds(5));

        var snapshot = store.Snapshot();

        Assert.Equal(Freshness.Expired, snapshot.Single(r => r.Kind == ProbeKind.Icmp).Freshness);
        Assert.Equal(Freshness.Fresh, snapshot.Single(r => r.Kind == ProbeKind.Http).Freshness);
    }

    [Fact]
    public void A_skipped_probe_is_never_treated_as_evidence()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.Gateway, "192.168.1.1", "Adapter is not up"));

        var skipped = store.Snapshot().Single();

        Assert.Equal(Freshness.Unknown, skipped.Freshness);
        Assert.False(skipped.ProvesReachability);
        Assert.False(skipped.WasAttempted);
    }

    [Fact]
    public void Clearing_forgets_both_results_and_suspicion()
    {
        var clock = new ManualClock();
        var store = new ObservationStore(clock);

        store.Record(Result(ProbeKind.Icmp, ProbeScope.External, "1.1.1.1", false, 0, 0));
        Assert.NotNull(store.SuspicionStartedAtTicks);

        store.Clear();

        Assert.Empty(store.Snapshot());
        Assert.Null(store.SuspicionStartedAtTicks);
    }
}
