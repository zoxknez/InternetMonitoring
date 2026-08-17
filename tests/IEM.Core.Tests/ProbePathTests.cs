using System.Net;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// P0-3. Windows picks the outgoing adapter per destination, not once per machine, so on a
/// laptop with Wi-Fi, a docking station and a VPN all up at once, three probes can leave by
/// three different routes. An outage attributed to the wrong link is not merely useless -
/// it looks exactly like evidence, right up until the operator checks it.
/// </summary>
public sealed class ProbePathTests
{
    [Fact]
    public void A_probe_with_no_resolved_route_cannot_support_an_attribution()
    {
        Assert.False(ProbePath.Unresolved.Resolved);
        Assert.False(ProbePath.Unresolved.ProvesLink);
        Assert.False(ProbePath.Unresolved.Bound);
    }

    /// <summary>
    /// Knowing the route and forcing it are different claims. The resolver reports what
    /// Windows would choose; binding makes it a fact. Both are recorded, separately.
    /// </summary>
    [Fact]
    public void Resolved_and_bound_are_recorded_separately()
    {
        var predicted = new ProbePath("{ETH}", "192.168.1.10", Resolved: true);
        var enforced = predicted with { Bound = true };

        Assert.True(predicted.ProvesLink);
        Assert.False(predicted.Bound);
        Assert.True(enforced.Bound);
    }

    /// <summary>
    /// A source address without an adapter behind it says where packets came from but not
    /// which link carried them, which is the only question attribution turns on.
    /// </summary>
    [Fact]
    public void A_source_address_alone_does_not_identify_the_link()
    {
        var path = new ProbePath(null, "192.168.1.10", Resolved: true);

        Assert.True(path.Resolved);
        Assert.False(path.ProvesLink);
    }

    // ---- What the cycle concludes from the paths ------------------------------

    [Fact]
    public void A_cycle_whose_probes_all_took_one_path_names_that_adapter()
    {
        var cycle = CycleBuilder.Wired().Build();

        Assert.NotNull(cycle.AgreedInterfaceId);
        Assert.NotNull(cycle.AgreedSourceAddress);
        Assert.False(cycle.MultiplePathsInUse);
    }

    /// <summary>
    /// The state this exists to catch. Traffic is leaving by more than one adapter, so no
    /// outage measured now can be pinned to any one of them.
    /// </summary>
    [Fact]
    public void A_cycle_whose_probes_took_different_paths_names_no_adapter()
    {
        var cycle = CycleBuilder.Wired().ProbesTookDifferentPaths().Build();

        Assert.True(cycle.MultiplePathsInUse);
        Assert.Null(cycle.AgreedInterfaceId);
    }

    [Fact]
    public void Unresolved_paths_do_not_count_as_disagreement()
    {
        // Nothing could be resolved, which is ignorance rather than a split path. Reporting
        // it as multi-path would put a warning on every machine where routing cannot be read.
        var cycle = CycleBuilder.Wired().PathsUnresolved().Build();

        Assert.False(cycle.MultiplePathsInUse);
        Assert.Null(cycle.AgreedInterfaceId);
    }

    // ---- The resolver on this machine -----------------------------------------

    /// <summary>
    /// The fallback used where routing cannot be inspected. Every probe reports an
    /// unresolved path, which costs attribution and leaves the measurements untouched.
    /// </summary>
    [Fact]
    public void The_null_resolver_answers_nothing_rather_than_guessing()
    {
        var path = NullRouteResolver.Instance.Resolve(IPAddress.Parse("1.1.1.1"));

        Assert.Equal(ProbePath.Unresolved, path);
    }
}
