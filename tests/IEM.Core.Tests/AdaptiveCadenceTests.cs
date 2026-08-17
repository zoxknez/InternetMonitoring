using IEM.Core.Classification;
using IEM.Core.Model;
using IEM.Core.Scheduling;

namespace IEM.Core.Tests;

public sealed class AdaptiveCadenceTests
{
    private static readonly SampleVerdict Healthy = new(NetworkState.Ok, "ok");
    private static readonly SampleVerdict Degraded = new(NetworkState.PacketLoss, "loss");
    private static readonly SampleVerdict Down = new(NetworkState.InternetDown, "down");
    private static readonly SampleVerdict Roaming = new(NetworkState.WifiRoaming, "roaming");

    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Starts_stable_and_samples_once_a_second()
    {
        var cadence = new AdaptiveCadenceController();

        Assert.Equal(CadencePhase.Stable, cadence.Phase);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), cadence.Interval);
    }

    [Fact]
    public void A_single_degraded_sample_moves_to_suspect()
    {
        var cadence = new AdaptiveCadenceController();

        var decision = cadence.Observe(Degraded, Sec(1));

        Assert.Equal(CadencePhase.Suspect, decision.Phase);
        Assert.Equal(TimeSpan.FromMilliseconds(250), decision.Interval);
        Assert.True(decision.PhaseChanged);
    }

    [Fact]
    public void A_second_consecutive_failure_escalates_to_burst()
    {
        var cadence = new AdaptiveCadenceController();
        cadence.Observe(Degraded, Sec(1));

        var decision = cadence.Observe(Degraded, Sec(1.25));

        Assert.Equal(CadencePhase.Burst, decision.Phase);
        Assert.Equal(TimeSpan.FromMilliseconds(100), decision.Interval);
    }

    [Fact]
    public void A_confirmed_outage_goes_straight_to_incident_cadence()
    {
        // No point ramping up gradually once the link is definitely down: the outage
        // boundaries are precisely what needs sub-second resolution.
        var cadence = new AdaptiveCadenceController();

        var decision = cadence.Observe(Down, Sec(1));

        Assert.Equal(CadencePhase.Incident, decision.Phase);
        Assert.Equal(TimeSpan.FromMilliseconds(100), decision.Interval);
    }

    [Fact]
    public void Recovery_holds_fast_sampling_then_returns_to_stable()
    {
        // A link that flaps back and forth would otherwise be sampled slowly again the
        // instant it recovered, and the second drop would be smeared.
        var cadence = new AdaptiveCadenceController();
        cadence.Observe(Down, Sec(1));

        Assert.Equal(CadencePhase.Recovery, cadence.Observe(Healthy, Sec(2)).Phase);
        Assert.Equal(CadencePhase.Recovery, cadence.Observe(Healthy, Sec(20)).Phase);
        Assert.Equal(CadencePhase.Stable, cadence.Observe(Healthy, Sec(32)).Phase);
    }

    [Fact]
    public void An_outage_during_recovery_returns_to_incident_cadence()
    {
        var cadence = new AdaptiveCadenceController();
        cadence.Observe(Down, Sec(1));
        cadence.Observe(Healthy, Sec(2));

        Assert.Equal(CadencePhase.Incident, cadence.Observe(Down, Sec(3)).Phase);
    }

    [Fact]
    public void Info_level_states_do_not_trigger_escalation()
    {
        // Roaming, monitoring gaps and our own speed test are recorded elsewhere. Letting
        // them drive the cadence would burn the fast sampling budget on non-faults.
        var cadence = new AdaptiveCadenceController();

        var decision = cadence.Observe(Roaming, Sec(1));

        Assert.Equal(CadencePhase.Stable, decision.Phase);
        Assert.False(decision.PhaseChanged);
    }

    [Fact]
    public void A_healthy_sample_resets_the_failure_streak()
    {
        var cadence = new AdaptiveCadenceController();
        cadence.Observe(Degraded, Sec(1));
        cadence.Observe(Healthy, Sec(2));
        cadence.Observe(Healthy, Sec(40));

        // Streak reset, so the next single failure is Suspect again rather than Burst.
        Assert.Equal(CadencePhase.Suspect, cadence.Observe(Degraded, Sec(41)).Phase);
    }

    [Fact]
    public void Phase_change_flag_reports_only_actual_transitions()
    {
        var cadence = new AdaptiveCadenceController();
        cadence.Observe(Down, Sec(1));

        Assert.False(cadence.Observe(Down, Sec(1.1)).PhaseChanged);
    }
}
