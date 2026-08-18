using System.Net;
using System.Net.Sockets;
using IEM.Core.Model;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// What the measurement's own sockets did, and the line between having watched them and not.
/// <para>
/// The route table answers "where would the system send this"; these answer "where did it
/// actually go". They are recorded separately and judged separately, and the one thing neither
/// of them may do is let "nobody looked" read as "it was fine" - the same invariant the route
/// states were rebuilt around in 2.7.0, now applied one layer closer to the wire.
/// </para>
/// </summary>
public sealed class ActualPathTests
{
    private const string Ethernet = "{ETH-0}";
    private const string Tunnel = "{VPN-9}";

    private static ConnectionAttempt Attempt(string? interfaceId) =>
        new(IPAddress.Parse("192.168.1.10"), 51_000, IPAddress.Parse("93.184.216.34"), 443, DateTimeOffset.UnixEpoch)
        {
            Observed = interfaceId is null ? null : new NetworkInterfaceIdentity(interfaceId, interfaceId),
        };

    [Fact]
    public void No_connection_was_observed_so_nothing_is_concluded()
    {
        var agreement = PathAgreement.Of(Ethernet, []);

        Assert.Equal(PathAgreementState.Unknown, agreement.State);
        Assert.Contains("nisu posmatrane", agreement.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Connections_nobody_could_place_are_not_agreement()
    {
        // The sockets connected; their local addresses matched no adapter on this machine.
        // That is the absence of a finding, and it must not round up to one.
        var agreement = PathAgreement.Of(Ethernet, [Attempt(null), Attempt(null)]);

        Assert.Equal(PathAgreementState.Unknown, agreement.State);
        Assert.Equal(2, agreement.UnresolvedCount);
    }

    [Fact]
    public void An_unnamed_adapter_leaves_nothing_to_agree_with()
    {
        var agreement = PathAgreement.Of(null, [Attempt(Ethernet)]);

        Assert.Equal(PathAgreementState.Unknown, agreement.State);
        Assert.Single(agreement.Attempts);
    }

    [Fact]
    public void Every_connection_through_the_named_adapter_is_agreement()
    {
        var agreement = PathAgreement.Of(Ethernet, [Attempt(Ethernet), Attempt(Ethernet), Attempt(Ethernet)]);

        Assert.Equal(PathAgreementState.Match, agreement.State);
        Assert.Empty(agreement.Elsewhere);
    }

    [Fact]
    public void One_connection_elsewhere_is_disagreement_and_it_is_named()
    {
        var agreement = PathAgreement.Of(Ethernet, [Attempt(Ethernet), Attempt(Tunnel), Attempt(Ethernet)]);

        Assert.Equal(PathAgreementState.Mismatch, agreement.State);
        Assert.Equal(Tunnel, Assert.Single(agreement.Elsewhere).Observed!.Id);
        Assert.Contains($"1 od 3 izašlo kroz: {Tunnel}", agreement.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void When_every_connection_went_elsewhere_the_sentence_says_every_one()
    {
        // Measured while labelled with the Wi-Fi adapter, with the cable carrying all of it.
        // The state is the same as one stray connection; the sentence must not be.
        var agreement = PathAgreement.Of(Ethernet, [Attempt(Tunnel), Attempt(Tunnel), Attempt(Tunnel)]);

        Assert.Equal(PathAgreementState.Mismatch, agreement.State);
        Assert.Contains("3 od 3 izašlo kroz", agreement.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("deo veza", agreement.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Agreement_never_claims_the_connections_it_could_not_place()
    {
        // Two through the Ethernet, one nowhere. The state rests on what resolved, so the
        // remainder has to be stated rather than dropped - otherwise the sentence reads as if
        // all three had been accounted for.
        var agreement = PathAgreement.Of(Ethernet, [Attempt(Ethernet), Attempt(Ethernet), Attempt(null)]);

        Assert.Equal(PathAgreementState.Match, agreement.State);
        Assert.Contains("za 1 veza adapter nije utvrđen", agreement.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("sve veze", agreement.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_sockets_did_outranks_what_the_route_table_predicted()
    {
        // The strongest case for recording both: the route table agreed beforehand, and the
        // transfer went out of the tunnel anyway. Before this, the figure would have been
        // filed as valid on the strength of the prediction alone.
        var conditions = Wired() with
        {
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
            ActualPath = PathAgreement.Of(Ethernet, [Attempt(Ethernet), Attempt(Tunnel)]),
        };

        var validity = SpeedMeasurementValidity.Of(conditions);

        Assert.False(validity.IsValidForComplaint);
        Assert.Contains(SpeedMeasurementDefect.ActualPathMismatch, validity.Defects);
    }

    [Fact]
    public void An_unobserved_path_is_not_a_defect_of_its_own()
    {
        // On some machines the sockets cannot be tied to an adapter at all. Not having watched
        // is already covered by the route-table check; charging it twice would make every
        // measurement on such a machine invalid for a reason that is not about the connection.
        var conditions = Wired() with
        {
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
            ActualPath = PathAgreement.NotObserved,
        };

        var validity = SpeedMeasurementValidity.Of(conditions);

        Assert.True(validity.IsValidForComplaint);
    }

    [Fact]
    public void The_note_records_the_observation_beside_the_prediction()
    {
        var conditions = Wired() with
        {
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
            ActualPath = PathAgreement.Of(Ethernet, [Attempt(Ethernet), Attempt(Ethernet), Attempt(null)]),
        };

        var note = SpeedMeasurementNote.From(
            DateTimeOffset.UnixEpoch,
            LinkMedium.Ethernet,
            1_000,
            conditions,
            new ThroughputResult(95, 100_000_000, TimeSpan.FromSeconds(8), ThroughputRefusal.None));

        Assert.Equal(PathAgreementState.Match, note.ActualPathState);
        Assert.Equal(3, note.ObservedConnections);
        Assert.Equal(1, note.UnresolvedConnections);
        Assert.Equal([Ethernet], note.ObservedInterfaces);
        Assert.Contains("za 1 veza adapter nije utvrđen", note.DescribeObservedPath(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_from_before_this_release_says_nothing_was_watching()
    {
        // Every measurement written up to 2.7.2 lacks these fields. Read back, it has to say
        // that the sockets were not observed - not borrow the agreement of whatever session
        // happens to be rendering the report.
        var note = new SpeedMeasurementNote(
            DateTimeOffset.UnixEpoch, LinkMedium.Ethernet, 1_000, 100, 95, 100_000_000,
            TimeSpan.FromSeconds(8), true, null, []);

        Assert.Equal(PathAgreementState.Unknown, note.ActualPathState);
        Assert.Equal(0, note.ObservedConnections);
        Assert.Equal("veze merenja nisu posmatrane", note.DescribeObservedPath());
    }

    [Fact]
    public void The_observer_records_what_the_socket_reported()
    {
        var observer = new ConnectionObserver(new StubAddressMap(Ethernet));

        observer.Record(
            new IPEndPoint(IPAddress.Parse("192.168.1.10"), 51_000),
            new IPEndPoint(IPAddress.Parse("93.184.216.34"), 443));

        var attempt = Assert.Single(observer.Attempts);

        Assert.Equal(Ethernet, attempt.Observed!.Id);
        Assert.Equal(443, attempt.RemotePort);
        Assert.Equal(AddressFamily.InterNetwork, attempt.Family);
    }

    [Fact]
    public void An_ipv4_connection_is_recorded_as_ipv4_however_the_socket_wrote_it()
    {
        // The measurement's sockets are dual-stack, so Windows hands back ::ffff:192.168.1.102
        // for a connection that is plainly IPv4. The first live run of this feature observed
        // six connections and placed none of them, because every adapter on the machine was
        // compared against a form no adapter uses - and the address family, which exists here
        // to catch a measurement leaking onto the other stack, said IPv6 for all six.
        var observer = new ConnectionObserver(new PlainIpv4Map("192.168.1.102", Ethernet));

        observer.Record(
            new IPEndPoint(IPAddress.Parse("::ffff:192.168.1.102"), 1_998),
            new IPEndPoint(IPAddress.Parse("::ffff:172.66.0.218"), 443));

        var attempt = Assert.Single(observer.Attempts);

        Assert.Equal(AddressFamily.InterNetwork, attempt.Family);
        Assert.Equal(IPAddress.Parse("192.168.1.102"), attempt.LocalAddress);
        Assert.Equal(IPAddress.Parse("172.66.0.218"), attempt.RemoteAddress);
        Assert.Equal(Ethernet, attempt.Observed!.Id);
    }

    [Fact]
    public void A_socket_without_endpoints_is_not_recorded_as_anything()
    {
        var observer = new ConnectionObserver(new StubAddressMap(Ethernet));

        observer.Record(null, new IPEndPoint(IPAddress.Loopback, 443));
        observer.Record(new IPEndPoint(IPAddress.Loopback, 51_000), null);

        Assert.Empty(observer.Attempts);
    }

    private static SpeedMeasurementConditions Wired() =>
        new(LinkMedium.Ethernet, 1_000_000_000, 100, 95) { LinkWasIdle = true, ConnectionHealthy = true };

    /// <summary>An adapter that holds one plain IPv4 address, the way a real one does.</summary>
    private sealed class PlainIpv4Map(string address, string interfaceId) : ILocalAddressMap
    {
        public NetworkInterfaceIdentity? For(IPAddress localAddress) =>
            IPAddress.Parse(address).Equals(localAddress)
                ? new NetworkInterfaceIdentity(interfaceId, interfaceId)
                : null;
    }

    private sealed class StubAddressMap(string? interfaceId) : ILocalAddressMap
    {
        public NetworkInterfaceIdentity? For(IPAddress localAddress) =>
            interfaceId is null ? null : new NetworkInterfaceIdentity(interfaceId, interfaceId);
    }
}
