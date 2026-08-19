using System.Net;
using System.Net.Sockets;
using IEM.Core.Model;
using IEM.Core.Scheduling;
using IEM.Core.Time;

namespace IEM.Core.Probes;

/// <summary>
/// Runs every family of probes on its own loop, writing answers into a shared store.
/// <para>
/// This is the change that makes sub-second outage boundaries possible. Before it, one
/// sampling cycle awaited every probe together, so a TCP connect timing out after two
/// seconds set the pace for everything - and the tool was at its coarsest precisely during
/// an outage, which is the only time the precision matters. Now a slow probe slows nothing
/// but itself.
/// </para>
/// <para>
/// Each loop is sequential: send, file the answer, wait out the remainder of its interval,
/// repeat. A probe that takes longer than its interval simply runs back to back. There is
/// no queue to build up and no round that can overlap itself.
/// </para>
/// </summary>
public sealed class ProbeScheduler : IAsyncDisposable
{
    private readonly ObservationStore _store;
    private readonly ProbeOptions _options;
    private readonly IClock _clock;
    private readonly Func<LinkSnapshot> _link;
    private readonly IRouteResolver _routes;
    private readonly IBoundIcmp? _boundIcmp;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _deepWake = new(0, 1);
    private readonly List<Task> _loops = [];

    /// <summary>Interval the fast loops aim for. Follows the cadence phase.</summary>
    private volatile int _fastIntervalMs = 1000;

    /// <param name="routes">
    /// Resolves the adapter and source address per destination. The default answers nothing,
    /// which costs attribution and nothing else.
    /// </param>
    /// <param name="boundIcmp">Platform sender that can pin ICMP to a source address.</param>
    public ProbeScheduler(
        ObservationStore store,
        ProbeOptions options,
        IClock clock,
        Func<LinkSnapshot> link,
        HttpClient? httpClient = null,
        IRouteResolver? routes = null,
        IBoundIcmp? boundIcmp = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _routes = routes ?? NullRouteResolver.Instance;
        _boundIcmp = boundIcmp;

        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            // The connectivity endpoint must be reached afresh each time. A pooled or
            // cached answer would report connectivity that no longer exists.
            PooledConnectionLifetime = TimeSpan.FromSeconds(10),
            AllowAutoRedirect = false,
        });

        _httpClient.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
        };
    }

    public void Start()
    {
        if (_loops.Count > 0)
        {
            return;
        }

        var token = _shutdown.Token;

        _loops.Add(Task.Run(() => FastLoopAsync(ProbeGatewayAsync, token), CancellationToken.None));
        _loops.Add(Task.Run(() => FastLoopAsync(ProbeExternalIcmpAsync, token), CancellationToken.None));
        _loops.Add(Task.Run(() => FastLoopAsync(ProbeTcpAsync, token), CancellationToken.None));
        _loops.Add(Task.Run(() => DeepLoopAsync(token), CancellationToken.None));
    }

    /// <summary>
    /// Tells the fast loops how often to run. Called when the cadence phase changes, so
    /// probing tightens during an incident and relaxes once service returns.
    /// </summary>
    public void SetPhase(CadencePhase phase, TimeSpan interval) =>
        _fastIntervalMs = (int)Math.Clamp(interval.TotalMilliseconds, 50, 5000);

    /// <summary>
    /// Asks the slow probes to run now rather than at their next scheduled turn.
    /// <para>
    /// Called the moment the fast probes see trouble. Their answers are what decide whether
    /// this is a real outage or merely filtered ping, and a result from before the trouble
    /// began is worthless for that - the store will mark it stale and refuse to count it.
    /// </para>
    /// </summary>
    public void RequestDeepRound()
    {
        if (_deepWake.CurrentCount != 0)
        {
            return;
        }

        try
        {
            _deepWake.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending request is all that is wanted.
        }
    }

    // ---- Loops --------------------------------------------------------------

    private async Task FastLoopAsync(Func<CancellationToken, Task> probe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = _clock.MonotonicTicks;

            await RunGuardedAsync(probe, cancellationToken).ConfigureAwait(false);

            var elapsed = _clock.MonotonicElapsed(started, _clock.MonotonicTicks);
            var remaining = TimeSpan.FromMilliseconds(_fastIntervalMs) - elapsed;

            if (remaining <= TimeSpan.Zero)
            {
                // The probe took longer than its interval. Run again straight away rather
                // than trying to catch up, which would only queue work on a link already
                // struggling.
                continue;
            }

            if (!await DelayAsync(remaining, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task DeepLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RunGuardedAsync(ProbeDeepAsync, cancellationToken).ConfigureAwait(false);

            try
            {
                await _deepWake.WaitAsync(_options.DeepProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task RunGuardedAsync(Func<CancellationToken, Task> probe, CancellationToken cancellationToken)
    {
        try
        {
            await probe(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
#pragma warning disable CA1031 // A failing probe must never take the monitor down with it.
        catch (Exception)
        {
            // Swallowed deliberately: this tool exists to keep recording while the network
            // misbehaves, so an unexpected probe failure has to be survivable. The result
            // simply is not filed, and the store ages the previous one out.
        }
#pragma warning restore CA1031
    }

    private async Task<bool> DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---- Probes -------------------------------------------------------------

    internal Task ProbeGatewayForTestAsync(CancellationToken token = default) => ProbeGatewayAsync(token);

    private async Task ProbeGatewayAsync(CancellationToken cancellationToken)
    {
        var link = _link();

        if (!link.HasGateway || !link.IsUp)
        {
            _store.Record(Stamp(
                ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.Gateway, link.GatewayAddress ?? string.Empty,
                    link.IsUp ? "No gateway configured" : "Adapter is not up"),
                _clock.MonotonicTicks,
                _clock.MonotonicTicks));

            return;
        }

        var path = PathTo(link.GatewayAddress!);

        // Gateway coherence checks:
        // 1. Gateway route must be resolved to an interface
        if (!path.Resolved || string.IsNullOrWhiteSpace(path.InterfaceId))
        {
            _store.Record(Stamp(
                ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.Gateway, link.GatewayAddress!, "Gateway route unresolved"),
                _clock.MonotonicTicks,
                _clock.MonotonicTicks));
            return;
        }

        // 2. FIB Interface must match the monitored interface
        if (link.InterfaceId is not null && !string.Equals(path.InterfaceId, link.InterfaceId, StringComparison.OrdinalIgnoreCase))
        {
            _store.Record(Stamp(
                ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.Gateway, link.GatewayAddress!, $"Gateway route interface mismatch: {path.InterfaceId} != {link.InterfaceId}"),
                _clock.MonotonicTicks,
                _clock.MonotonicTicks));
            return;
        }

        // 3. Source address family must match gateway IP family
        var source = SourceOf(path);
        if (IPAddress.TryParse(link.GatewayAddress!, out var gwIp))
        {
            if (source is not null && source.AddressFamily != gwIp.AddressFamily)
            {
                _store.Record(Stamp(
                    ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.Gateway, link.GatewayAddress!, "Gateway source address family mismatch"),
                    _clock.MonotonicTicks,
                    _clock.MonotonicTicks));
                return;
            }
        }
        else
        {
            _store.Record(Stamp(
                ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.Gateway, link.GatewayAddress!, "Unparseable gateway address"),
                _clock.MonotonicTicks,
                _clock.MonotonicTicks));
            return;
        }

        var started = _clock.MonotonicTicks;

        var result = await FastProbes
            .IcmpAsync(
                link.GatewayAddress!, ProbeScope.Gateway, _options.IcmpTimeout, cancellationToken,
                source, _boundIcmp)
            .ConfigureAwait(false);

        _store.Record(Stamp(result, started, _clock.MonotonicTicks, path, BoundIfSourced(path, result)));
    }

    private async Task ProbeExternalIcmpAsync(CancellationToken cancellationToken)
    {
        if (!_link().IsUp)
        {
            SkipExternal(ProbeKind.Icmp, _options.IcmpV4Targets);
            return;
        }

        var targets = _options.IcmpV4Targets.ToList();

        if (Ipv6Availability.IsAvailable(_clock))
        {
            targets.AddRange(_options.IcmpV6Targets);
        }

        await ProbeAllAsync(
            targets,
            (target, path, token) => FastProbes.IcmpAsync(
                target, ProbeScope.External, _options.IcmpTimeout, token, SourceOf(path), _boundIcmp),
            bound: (path, target) => _boundIcmp is not null && SourceOf(path) is not null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeTcpAsync(CancellationToken cancellationToken)
    {
        if (!_link().IsUp)
        {
            SkipExternal(ProbeKind.TcpConnect, _options.TcpTargets);
            return;
        }

        await ProbeAllAsync(
            _options.TcpTargets,
            (target, path, token) => FastProbes.TcpConnectAsync(target, _options.TcpTimeout, token, SourceOf(path)),
            bound: (path, target) => SourceOf(path) is not null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProbeDeepAsync(CancellationToken cancellationToken)
    {
        var link = _link();

        var work = new List<Task<ProbeResult>>(5);
        var started = _clock.MonotonicTicks;

        // Each resolver is queried over its own resolved path, and both are bound to the
        // same source. Only then does "the operator's resolver is down while a public one
        // works" mean anything: unbound, the two queries could have left the machine by
        // different routes and the comparison would be between two different networks.
        var publicPath = PathTo(_options.PublicResolver);
        var tlsPath = PathTo(_options.TlsTarget);

        work.Add(Tag(DeepProbes.SystemDnsAsync(_options.DnsQueryName, _options.DnsTimeout, cancellationToken),
            // The Windows resolver picks its own path and cannot be bound, so it is filed
            // with no path at all rather than with a guess.
            ProbePath.Unresolved));

        work.Add(Tag(DeepProbes.DnsAsync(
            _options.PublicResolver, DnsResolverRole.Public, _options.DnsQueryName, _options.DnsTimeout,
            cancellationToken, SourceOf(publicPath)), publicPath, bound: SourceOf(publicPath) is not null));

        work.Add(Tag(DeepProbes.TlsAsync(_options.TlsTarget, _options.TlsTimeout, cancellationToken), tlsPath));

        work.Add(Tag(DeepProbes.HttpAsync(
            _httpClient, _options.HttpProbeUrl, _options.ExpectedHttpBody, _options.HttpTimeout, cancellationToken),
            ProbePath.Unresolved));

        // Every resolver the machine was given, not only the first.
        //
        // The first entry used to stand for all of them, and on an IPv6 network that entry is
        // the router's own link-local address. A tester's connection worked perfectly - system
        // DNS, HTTP, TCP, ping all fine - while the program reported his operator's DNS as
        // broken for the whole session, because one address we could not get an answer from
        // spoke for every resolver he had.
        var assigned = link.DnsServers
            .Where(server => IPAddress.TryParse(server, out _))
            .Take(Math.Max(1, _options.MaxAssignedResolvers))
            .ToArray();

        foreach (var resolver in assigned)
        {
            var assignedPath = PathTo(resolver);

            work.Add(Tag(DeepProbes.DnsAsync(
                resolver, DnsResolverRole.IspAssigned, _options.DnsQueryName, _options.DnsTimeout,
                cancellationToken, SourceOf(assignedPath)), assignedPath, bound: SourceOf(assignedPath) is not null));
        }

        // A public resolver on the other stack too, whenever an assigned one lives there.
        // Without it the comparison crosses address families, and the verdict describes the
        // difference between two stacks rather than the health of one resolver.
        if (assigned.Any(r => IPAddress.Parse(r).AddressFamily == AddressFamily.InterNetworkV6))
        {
            var publicV6Path = PathTo(_options.PublicResolverV6);

            work.Add(Tag(DeepProbes.DnsAsync(
                _options.PublicResolverV6, DnsResolverRole.Public, _options.DnsQueryName, _options.DnsTimeout,
                cancellationToken, SourceOf(publicV6Path)), publicV6Path, bound: SourceOf(publicV6Path) is not null));
        }

        var results = await Task.WhenAll(work).ConfigureAwait(false);
        var completed = _clock.MonotonicTicks;

        _store.RecordAll(results.Select(r => r with { StartedAtTicks = started, CompletedAtTicks = completed }));

        static async Task<ProbeResult> Tag(Task<ProbeResult> probe, ProbePath path, bool bound = false)
        {
            var result = await probe.ConfigureAwait(false);
            return result with { Path = path with { Bound = bound && result.Outcome != ProbeOutcome.Skipped } };
        }
    }

    /// <summary>
    /// Probes several targets of one family at once, then files them together.
    /// <para>
    /// Concurrent within the family so one unreachable address does not delay the others,
    /// but the family as a whole still keeps its own pace, independent of every other loop.
    /// </para>
    /// </summary>
    private async Task ProbeAllAsync(
        IEnumerable<string> targets,
        Func<string, ProbePath, CancellationToken, Task<ProbeResult>> probe,
        Func<ProbePath, string, bool> bound,
        CancellationToken cancellationToken)
    {
        var started = _clock.MonotonicTicks;

        // Routes are resolved before the round, so every probe in it is measured against the
        // path the stack reported at the same moment.
        var planned = targets.Select(target => (Target: target, Path: PathTo(target))).ToList();

        var results = await Task
            .WhenAll(planned.Select(p => probe(p.Target, p.Path, cancellationToken)))
            .ConfigureAwait(false);

        var completed = _clock.MonotonicTicks;

        _store.RecordAll(results.Select((result, i) =>
            Stamp(result, started, completed, planned[i].Path,
                bound(planned[i].Path, planned[i].Target) && result.Outcome != ProbeOutcome.Skipped)));
    }

    private void SkipExternal(ProbeKind kind, IEnumerable<string> targets)
    {
        var now = _clock.MonotonicTicks;

        // A down adapter cannot send anything. Filing the attempt as skipped rather than
        // failed keeps a local link problem from being counted as external packet loss.
        _store.RecordAll(targets.Select(target =>
            Stamp(ProbeResult.Skip(kind, ProbeScope.External, target, "Adapter is not up"), now, now)));
    }

    // ---- Paths ---------------------------------------------------------------

    /// <summary>
    /// The route to a target, which may be written either as a bare address or as
    /// <c>address:port</c>.
    /// </summary>
    private ProbePath PathTo(string target)
    {
        var host = target;

        if (FastProbes.TryParseEndpoint(target, out var endpoint))
        {
            return _routes.Resolve(endpoint.Address);
        }

        // A bracketed IPv6 literal with no port still needs unwrapping.
        host = host.Trim('[', ']');

        return IPAddress.TryParse(host, out var address) ? _routes.Resolve(address) : ProbePath.Unresolved;
    }

    private static IPAddress? SourceOf(ProbePath path) =>
        path.SourceAddress is { } source && IPAddress.TryParse(source, out var address) ? address : null;

    /// <summary>
    /// Whether a gateway ping actually went out pinned to the resolved source. Only true when
    /// the platform sender exists and the result came back through it.
    /// </summary>
    private bool BoundIfSourced(ProbePath path, ProbeResult result) =>
        _boundIcmp is not null &&
        SourceOf(path) is not null &&
        result.Outcome != ProbeOutcome.Skipped;

    private static ProbeResult Stamp(ProbeResult result, long startedAtTicks, long completedAtTicks) =>
        result with { StartedAtTicks = startedAtTicks, CompletedAtTicks = completedAtTicks };

    private static ProbeResult Stamp(
        ProbeResult result, long startedAtTicks, long completedAtTicks, ProbePath path, bool bound) =>
        result with
        {
            StartedAtTicks = startedAtTicks,
            CompletedAtTicks = completedAtTicks,
            Path = path with { Bound = bound },
        };

    private bool _disposed;

    /// <summary>
    /// Stops every loop and waits for the probes already in flight.
    /// <para>
    /// Idempotent, because closing a session calls this explicitly - probing has to stop
    /// before the evidence is written, not after the report is built - while the usual
    /// <c>await using</c> still calls it again on the way out.
    /// </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _shutdown.CancelAsync().ConfigureAwait(false);

        foreach (var loop in _loops)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _shutdown.Dispose();
        _deepWake.Dispose();
        _httpClient.Dispose();
    }
}
