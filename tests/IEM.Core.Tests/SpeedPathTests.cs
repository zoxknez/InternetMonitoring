using System.Net;
using System.Net.Sockets;
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

    private static MeasurementRouteState StateOf(IRouteResolver resolver, IReadOnlyList<IPAddress> destinations, string? id) =>
        SpeedPath.ResolveRoutes(resolver, destinations, id).State;

    [Fact]
    public void Traffic_leaving_through_the_inspected_adapter_agrees_with_the_route_table()
    {
        var resolver = new StubResolver { [V4] = Monitored };

        Assert.Equal(MeasurementRouteState.AllResolvedRoutesMatch, StateOf(resolver, [V4], Monitored));
    }

    /// <summary>
    /// The case this exists for: a VPN or a docking station quietly carrying the transfer
    /// while the figure is filed against the link the user believes they measured.
    /// </summary>
    [Fact]
    public void Traffic_leaving_through_another_adapter_does_not()
    {
        var resolver = new StubResolver { [V4] = Vpn };

        Assert.Equal(MeasurementRouteState.OtherRouteOnly, StateOf(resolver, [V4], Monitored));
    }

    /// <summary>
    /// The bug this release removes. Until 2.7 one matching route ended the search and the
    /// answer was "single path", on the reasoning that a host's second address family may be
    /// routed through a tunnel this machine never uses. But nothing stops the transfer from
    /// choosing that family - so a measurement carried by the VPN could be filed against the
    /// Ethernet link with a clean bill of health.
    /// </summary>
    [Fact]
    public void A_host_routed_through_two_adapters_is_mixed_rather_than_confirmed()
    {
        var resolver = new StubResolver { [V4] = Monitored, [V6] = Vpn };

        Assert.Equal(MeasurementRouteState.MixedRoutes, StateOf(resolver, [V4, V6], Monitored));

        // And the order the addresses arrive in cannot change the answer, which is exactly
        // what the early exit made it do.
        Assert.Equal(MeasurementRouteState.MixedRoutes, StateOf(resolver, [V6, V4], Monitored));
    }

    /// <summary>
    /// "Putanja je dvosmislena" tells nobody what to change; naming the family does.
    /// </summary>
    [Fact]
    public void A_mixed_result_says_which_address_family_went_the_other_way()
    {
        var resolver = new StubResolver { [V4] = Monitored, [V6] = Vpn };

        var route = SpeedPath.ResolveRoutes(resolver, [V4, V6], Monitored);

        Assert.Equal(V6, Assert.Single(route.Elsewhere).Destination);
        Assert.Contains("IPv6", route.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("IPv4", route.Describe(), StringComparison.Ordinal);
    }

    /// <summary>
    /// "We could not check" must never become "we checked and it was fine" - that is the
    /// substitution this whole tool exists to refuse.
    /// </summary>
    [Fact]
    public void An_unresolvable_route_is_unknown_rather_than_either_answer()
    {
        Assert.Equal(MeasurementRouteState.Unknown, StateOf(new StubResolver(), [V4], Monitored));
        Assert.Equal(MeasurementRouteState.Unknown, StateOf(NullRouteResolver.Instance, [V4], Monitored));
    }

    [Fact]
    public void With_nothing_to_check_against_the_answer_is_unknown()
    {
        var resolver = new StubResolver { [V4] = Monitored };

        Assert.Equal(MeasurementRouteState.Unknown, StateOf(resolver, [V4], id: null));
        Assert.Equal(MeasurementRouteState.Unknown, StateOf(resolver, [V4], id: "  "));
        Assert.Equal(MeasurementRouteState.Unknown, StateOf(resolver, [], Monitored));
    }

    /// <summary>
    /// A candidate the route table had no answer for is counted and said, rather than
    /// dropped. The verdict is about the routes that did resolve, and the report can show how
    /// many did not.
    /// </summary>
    [Fact]
    public void An_address_that_could_not_be_resolved_is_recorded_rather_than_dropped()
    {
        var resolver = new StubResolver { [V4] = Monitored };

        var route = SpeedPath.ResolveRoutes(resolver, [V4, V6], Monitored);

        Assert.Equal(MeasurementRouteState.AllResolvedRoutesMatch, route.State);
        Assert.Equal(1, route.UnresolvedCount);
        Assert.Contains("nije mogla razrešiti", route.Describe(), StringComparison.Ordinal);
    }

    /// <summary>Adapter identifiers arrive in different casings from different APIs.</summary>
    [Fact]
    public void The_comparison_does_not_turn_on_the_casing_of_a_guid()
    {
        var resolver = new StubResolver { [V4] = Monitored.ToLowerInvariant() };

        Assert.Equal(
            MeasurementRouteState.AllResolvedRoutesMatch,
            StateOf(resolver, [V4], Monitored.ToUpperInvariant()));
    }

    /// <summary>
    /// And the finding reaches the verdict. Three of the four states stop a measurement from
    /// standing behind a complaint, each with its own defect, because "some of it went out of
    /// the VPN", "all of it did" and "nobody could tell" need different answers.
    /// </summary>
    [Theory]
    [InlineData(MeasurementRouteState.MixedRoutes, SpeedMeasurementDefect.PathAmbiguous)]
    [InlineData(MeasurementRouteState.OtherRouteOnly, SpeedMeasurementDefect.PathElsewhere)]
    [InlineData(MeasurementRouteState.Unknown, SpeedMeasurementDefect.PathUnverified)]
    public void Anything_short_of_agreement_costs_the_measurement_its_standing(
        MeasurementRouteState state,
        SpeedMeasurementDefect expected)
    {
        var conditions = new SpeedMeasurementConditions(LinkMedium.Ethernet, 1_000_000_000, 100, 94)
        {
            RouteState = state,
        };

        var validity = SpeedMeasurementValidity.Of(conditions);

        Assert.False(validity.IsValidForComplaint);
        Assert.Contains(expected, validity.Defects);
        Assert.NotEmpty(expected.Explain());
    }

    /// <summary>
    /// Even the best state stops short of claiming the path was confirmed. The route table
    /// describes the choice the operating system would make; the socket that carried the
    /// transfer was never inspected, and that distinction is the whole of this release.
    /// </summary>
    [Fact]
    public void The_best_answer_is_still_not_called_a_confirmed_path()
    {
        var text = MeasurementRouteState.AllResolvedRoutesMatch.Label();

        Assert.Contains("tabela ruta", text, StringComparison.Ordinal);
        Assert.DoesNotContain("potvrđena putanja", text, StringComparison.OrdinalIgnoreCase);
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
                ? new ProbePath(id, destination.AddressFamily == AddressFamily.InterNetworkV6 ? "fd00::2" : "10.0.0.2", Resolved: true)
                : ProbePath.Unresolved;
    }
}
