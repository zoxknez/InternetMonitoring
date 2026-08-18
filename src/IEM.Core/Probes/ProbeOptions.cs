namespace IEM.Core.Probes;

/// <summary>
/// What to probe and how long to wait.
/// <para>
/// Targets sit in three different networks on purpose. A single target cannot tell an
/// outage apart from that one host having a bad day, and an operator will say exactly
/// that if the whole case rests on one address not answering.
/// </para>
/// </summary>
public sealed record ProbeOptions
{
    public static readonly ProbeOptions Default = new();

    /// <summary>Cloudflare, Google and Quad9: three operators, three networks.</summary>
    public IReadOnlyList<string> IcmpV4Targets { get; init; } = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];

    public IReadOnlyList<string> IcmpV6Targets { get; init; } = ["2606:4700:4700::1111", "2001:4860:4860::8888"];

    /// <summary>ICMP is widely filtered, so TCP is what proves usable connectivity.</summary>
    public IReadOnlyList<string> TcpTargets { get; init; } = ["1.1.1.1:443", "8.8.8.8:443"];

    public string TlsTarget { get; init; } = "one.one.one.one:443";

    /// <summary>Name resolved during DNS probes. Short TTL and universally reachable.</summary>
    public string DnsQueryName { get; init; } = "www.msftconnecttest.com";

    /// <summary>Queried directly over UDP so a broken assigned resolver can be told apart from broken DNS.</summary>
    public string PublicResolver { get; init; } = "1.1.1.1";

    /// <summary>
    /// The same comparison over IPv6, used when the machine's assigned resolvers include one.
    /// <para>
    /// Without it the only public resolver was IPv4, so on an IPv6 network the program
    /// compared a query to an IPv6 address against a query to an IPv4 address and blamed the
    /// first for the difference. Cloudflare, same operator as the IPv4 one, so the two
    /// comparisons differ in stack and in nothing else.
    /// </para>
    /// </summary>
    public string PublicResolverV6 { get; init; } = "2606:4700:4700::1111";

    /// <summary>
    /// How many assigned resolvers are queried per cycle.
    /// <para>
    /// A machine usually carries two to four. Querying only the first and then reporting
    /// that "the assigned resolver failed" made one unreachable entry speak for all of them.
    /// Capped so a machine with a long list does not turn every cycle into a DNS flood.
    /// </para>
    /// </summary>
    public int MaxAssignedResolvers { get; init; } = 3;

    /// <summary>The endpoint Windows itself uses to decide whether it has internet.</summary>
    public string HttpProbeUrl { get; init; } = "http://www.msftconnecttest.com/connecttest.txt";

    public string ExpectedHttpBody { get; init; } = "Microsoft Connect Test";

    public TimeSpan IcmpTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan TcpTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan TlsTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often deep probes refresh while everything is healthy.</summary>
    public TimeSpan DeepProbeInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Age past which a cached deep result stops counting as evidence. Beyond this it is
    /// reported as not attempted rather than presented as a current observation.
    /// </summary>
    public TimeSpan DeepProbeMaxAge { get; init; } = TimeSpan.FromSeconds(60);
}
