using System.Net;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// A speed figure labelled with one adapter and measured over another is worse than no
/// figure: it looks exactly like evidence. The monitoring probes have been pinned to their
/// link since v2.2; the measurement was not, and recorded "one path, no VPN" without anybody
/// checking. These are the rules for what the route table's answer means.
/// </summary>
public sealed class SpeedPathTests
{
    private const string Monitored = "{AAAA0000-0000-0000-0000-000000000001}";
    private const string Vpn = "{BBBB0000-0000-0000-0000-000000000002}";

    private static readonly IPAddress V4 = IPAddress.Parse("104.16.0.1");
    private static readonly IPAddress V6 = IPAddress.Parse("2606:4700::1");

    [Fact]
    public void Traffic_leaving_through_the_inspected_adapter_is_a_single_path()
    {
        var resolver = new StubResolver { [V4] = Monitored };

        Assert.True(SpeedPath.LeavesThroughAdapter(resolver, [V4], Monitored));
    }

    /// <summary>
    /// The case this exists for: a VPN or a docking station quietly carrying the transfer
    /// while the figure is filed against the link the user believes they measured.
    /// </summary>
    [Fact]
    public void Traffic_leaving_through_another_adapter_is_not()
    {
        var resolver = new StubResolver { [V4] = Vpn };

        Assert.False(SpeedPath.LeavesThroughAdapter(resolver, [V4], Monitored));
    }

    /// <summary>
    /// A host with both an IPv4 and an IPv6 address may have one of them routed through a
    /// tunnel adapter this machine never actually uses. One matching route is enough;
    /// treating the other as a defect would refuse perfectly good measurements.
    /// </summary>
    [Fact]
    public void One_matching_route_is_enough_when_the_host_has_several_addresses()
    {
        var resolver = new StubResolver { [V4] = Monitored, [V6] = Vpn };

        Assert.True(SpeedPath.LeavesThroughAdapter(resolver, [V4, V6], Monitored));
        Assert.True(SpeedPath.LeavesThroughAdapter(resolver, [V6, V4], Monitored));
    }

    /// <summary>
    /// "We could not check" must never become "we checked and it was fine" - that is the
    /// substitution this whole tool exists to refuse.
    /// </summary>
    [Fact]
    public void An_unresolvable_route_is_unknown_rather_than_either_answer()
    {
        Assert.Null(SpeedPath.LeavesThroughAdapter(new StubResolver(), [V4], Monitored));
        Assert.Null(SpeedPath.LeavesThroughAdapter(NullRouteResolver.Instance, [V4], Monitored));
    }

    [Fact]
    public void With_nothing_to_check_against_the_answer_is_unknown()
    {
        var resolver = new StubResolver { [V4] = Monitored };

        Assert.Null(SpeedPath.LeavesThroughAdapter(resolver, [V4], interfaceId: null));
        Assert.Null(SpeedPath.LeavesThroughAdapter(resolver, [V4], interfaceId: "  "));
        Assert.Null(SpeedPath.LeavesThroughAdapter(resolver, [], Monitored));
    }

    /// <summary>Adapter identifiers arrive in different casings from different APIs.</summary>
    [Fact]
    public void The_comparison_does_not_turn_on_the_casing_of_a_guid()
    {
        var resolver = new StubResolver { [V4] = Monitored.ToLowerInvariant() };

        Assert.True(SpeedPath.LeavesThroughAdapter(resolver, [V4], Monitored.ToUpperInvariant()));
    }

    /// <summary>And the finding reaches the verdict as an ambiguous path, not as silence.</summary>
    [Fact]
    public void A_measurement_over_another_adapter_cannot_support_a_complaint()
    {
        var conditions = new SpeedMeasurementConditions(LinkMedium.Ethernet, 1_000_000_000, 100, 94)
        {
            SinglePath = false,
        };

        var validity = SpeedMeasurementValidity.Of(conditions);

        Assert.False(validity.IsValidForComplaint);
        Assert.Contains(SpeedMeasurementDefect.PathAmbiguous, validity.Defects);
    }

    private sealed class StubResolver : IRouteResolver
    {
        private readonly Dictionary<IPAddress, string> _routes = [];

        public string this[IPAddress destination]
        {
            set => _routes[destination] = value;
        }

        public ProbePath Resolve(IPAddress destination) =>
            _routes.TryGetValue(destination, out var id)
                ? new ProbePath(id, "10.0.0.2", Resolved: true)
                : ProbePath.Unresolved;
    }
}
