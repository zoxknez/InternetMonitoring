using System.Net.Sockets;
using IEM.Core.Model;
using IEM.Core.Probes;

namespace IEM.Core.Tests;

/// <summary>
/// Builds <see cref="ProbeCycle"/> fixtures for classifier tests.
/// <para>
/// Starts from a fully healthy cycle and lets each test break exactly the one thing it
/// is about, so a test reads as the fault it describes rather than as a wall of setup.
/// </para>
/// <para>
/// Results are stamped <see cref="Freshness.Fresh"/> by default. That is not a convenience:
/// an unstamped result defaults to <see cref="Freshness.Unknown"/> and proves nothing, which
/// is exactly the behaviour that stops a measurement taken before the trouble from claiming
/// the connection is fine. Tests that need that behaviour ask for it explicitly.
/// </para>
/// </summary>
internal sealed class CycleBuilder
{
    private const string GatewayIp = "192.168.1.1";
    private const string MonitoredInterface = "{TEST}";

    private readonly List<ProbeResult> _results = [];
    private LinkSnapshot _link;
    private bool _selfTest;
    private Freshness _freshness = Freshness.Fresh;

    /// <summary>
    /// What the machine itself was pushing through the link. A quiet line by default, because
    /// that is the case every existing test means; null would say "not measured", which is a
    /// third state and one that has to be asked for.
    /// </summary>
    private long? _localTraffic = 4_000;

    private CycleBuilder(LinkMedium medium)
    {
        _link = new LinkSnapshot("Test Adapter", MonitoredInterface, LinkStatus.Up, medium)
        {
            GatewayAddress = GatewayIp,
            LinkSpeedBitsPerSecond = 1_000_000_000,
            Wireless = medium == LinkMedium.Wireless
                ? new WirelessSnapshot("TestNet", "AA:BB:CC:DD:EE:FF", 80, 36)
                {
                    SsidVisibleInScan = true,
                    MeasuredRssiDbm = -55,
                    RadioOn = true,
                }
                : null,
        };

        GatewayReachable();
        ExternalIcmpAllSucceed();
        ExternalTcpAllSucceed();
        TlsSucceeds();
        DnsAllSucceed();
        HttpSucceeds();
    }

    public static CycleBuilder Wired() => new(LinkMedium.Ethernet);

    public static CycleBuilder Wireless() => new(LinkMedium.Wireless);

    // ---- Freshness ---------------------------------------------------------

    /// <summary>
    /// Marks every result added from here on as measured before the current trouble began.
    /// <para>
    /// This is the shape of the bug that hid short outages: a handshake that succeeded five
    /// seconds ago, still well inside its lifetime, insisting the line is fine while every
    /// live probe fails.
    /// </para>
    /// </summary>
    public CycleBuilder MeasuredBeforeTrouble()
    {
        _freshness = Freshness.Stale;
        return this;
    }

    /// <summary>Marks subsequent results as too old to describe the present at all.</summary>
    public CycleBuilder MeasuredTooLongAgo()
    {
        _freshness = Freshness.Expired;
        return this;
    }

    public CycleBuilder MeasuredNow()
    {
        _freshness = Freshness.Fresh;
        return this;
    }

    // ---- Link -------------------------------------------------------------

    public CycleBuilder AdapterDown()
    {
        _link = _link with { Status = LinkStatus.Down };
        return this;
    }

    public CycleBuilder SsidNotVisible()
    {
        _link = _link with { Wireless = _link.Wireless! with { SsidVisibleInScan = false } };
        return this;
    }

    public CycleBuilder SsidScanUnavailable()
    {
        _link = _link with { Wireless = _link.Wireless! with { SsidVisibleInScan = null } };
        return this;
    }

    /// <summary>The user switched Wi-Fi off, which makes every network vanish from the scan.</summary>
    public CycleBuilder RadioOff()
    {
        _link = _link with { Wireless = _link.Wireless! with { RadioOn = false, SsidVisibleInScan = false } };
        return this;
    }

    public CycleBuilder WeakSignal()
    {
        _link = _link with { Wireless = _link.Wireless! with { MeasuredRssiDbm = -82, SignalQualityPercent = 22 } };
        return this;
    }

    /// <summary>The router reports its WAN connection as down.</summary>
    public CycleBuilder RouterReportsWanDown()
    {
        _link = _link with
        {
            Router = new WanStatus("Disconnected", "ERROR_NO_CARRIER", TimeSpan.Zero, null),
        };
        return this;
    }

    /// <summary>The router's WAN uptime went backwards, so it reconnected.</summary>
    public CycleBuilder RouterReconnected()
    {
        _link = _link with
        {
            Router = new WanStatus("Connected", "ERROR_NONE", TimeSpan.FromSeconds(4), "203.0.113.7"),
            RouterReconnected = true,
        };
        return this;
    }

    /// <summary>The connection is now behind a different router.</summary>
    public CycleBuilder DifferentGateway(string address)
    {
        _link = _link with { GatewayAddress = address };
        _results.RemoveAll(r => r.Scope == ProbeScope.Gateway);
        _results.Add(Measured(new ProbeResult(
            ProbeKind.Icmp, ProbeScope.Gateway, address, ProbeOutcome.Success, TimeSpan.FromMilliseconds(2))));

        return this;
    }

    public CycleBuilder NoGateway()
    {
        _link = _link with { GatewayAddress = null };
        _results.RemoveAll(r => r.Scope == ProbeScope.Gateway);
        _results.Add(ProbeResult.Skip(ProbeKind.Icmp, ProbeScope.Gateway, string.Empty, "No gateway configured"));
        return this;
    }

    /// <summary>
    /// Routing could not be read at all - no adapter, no source address, on any probe.
    /// Ignorance, which must not be mistaken for probes disagreeing about the path.
    /// </summary>
    public CycleBuilder PathsUnresolved()
    {
        for (var i = 0; i < _results.Count; i++)
        {
            _results[i] = _results[i] with { Path = ProbePath.Unresolved };
        }

        return this;
    }

    /// <summary>Probes left the machine through more than one adapter.</summary>
    public CycleBuilder ProbesTookDifferentPaths()
    {
        for (var i = 0; i < _results.Count; i++)
        {
            var viaOtherAdapter = i % 2 == 1;

            _results[i] = _results[i] with
            {
                Path = new ProbePath(viaOtherAdapter ? "{OTHER}" : MonitoredInterface, "192.168.1.50", Resolved: true),
            };
        }

        return this;
    }

    // ---- Gateway ----------------------------------------------------------

    public CycleBuilder GatewayReachable() => ReplaceGateway(ProbeOutcome.Success, TimeSpan.FromMilliseconds(2));

    public CycleBuilder GatewayUnreachable() => ReplaceGateway(ProbeOutcome.TimedOut, null);

    // ---- External ---------------------------------------------------------

    public CycleBuilder ExternalIcmpAllSucceed() => ReplaceExternalIcmp(3, 3);

    public CycleBuilder ExternalIcmpAllFail() => ReplaceExternalIcmp(3, 0);

    public CycleBuilder ExternalIcmpPartialLoss() => ReplaceExternalIcmp(3, 2);

    public CycleBuilder ExternalIcmpLatency(TimeSpan rtt) => ReplaceExternalIcmp(3, 3, rtt);

    public CycleBuilder ExternalTcpAllSucceed() => ReplaceExternalTcp(2, 2);

    public CycleBuilder ExternalTcpAllFail() => ReplaceExternalTcp(2, 0);

    public CycleBuilder TlsSucceeds() => ReplaceTls(succeeds: true);

    public CycleBuilder TlsFails() => ReplaceTls(succeeds: false);

    public CycleBuilder HttpSucceeds() => ReplaceHttp(ProbeOutcome.Success, captivePortal: false);

    public CycleBuilder HttpFails() => ReplaceHttp(ProbeOutcome.Failed, captivePortal: false);

    public CycleBuilder CaptivePortal() => ReplaceHttp(ProbeOutcome.Failed, captivePortal: true);

    /// <summary>Every path out of the house is dead. The shape of a real outage.</summary>
    public CycleBuilder AllExternalFail() =>
        ExternalIcmpAllFail().ExternalTcpAllFail().TlsFails().HttpFails().DnsAllFail();

    // ---- DNS --------------------------------------------------------------

    public CycleBuilder DnsAllSucceed() => ReplaceDns(isp: true, pub: true, system: true);

    public CycleBuilder DnsAllFail() => ReplaceDns(isp: false, pub: false, system: false);

    /// <summary>The operator's resolver is broken but the rest of the internet is fine.</summary>
    public CycleBuilder DnsIspFailsOnly() => ReplaceDns(isp: false, pub: true, system: true);

    /// <summary>Only the Windows resolver answers. It cannot prove which link carried the query.</summary>
    public CycleBuilder OnlySystemDnsAnswers() => ReplaceDns(isp: false, pub: false, system: true);

    // ---- Misc -------------------------------------------------------------

    public CycleBuilder DuringSelfTest()
    {
        _selfTest = true;
        return this;
    }

    /// <summary>
    /// The machine itself was pulling something substantial through the link at this moment.
    /// Default is well past the threshold; a value can be given to sit either side of it.
    /// </summary>
    public CycleBuilder WithLocalTraffic(long bytesPerSecond = 25_000_000)
    {
        _localTraffic = bytesPerSecond;
        return this;
    }

    /// <summary>The counters could not be read, so nothing is known about our own traffic.</summary>
    public CycleBuilder WithoutLocalTrafficReading()
    {
        _localTraffic = null;
        return this;
    }

    public ProbeCycle Build(long sequence = 1) => new(
        sequence,
        new DateTimeOffset(2026, 8, 13, 16, 22, 33, TimeSpan.Zero),
        MonotonicTicks: sequence * TimeSpan.TicksPerSecond,
        _link,
        [.. _results],
        TimeSpan.FromMilliseconds(12))
    {
        SelfTestRunning = _selfTest,
        LocalTrafficBytesPerSecond = _localTraffic,
    };

    // ---- Internals --------------------------------------------------------

    /// <summary>Stamps a result as measured, with whatever freshness the test asked for.</summary>
    private ProbeResult Measured(ProbeResult result) => result with
    {
        Freshness = _freshness,
        Path = new ProbePath(MonitoredInterface, "192.168.1.50", Resolved: true),
    };

    private CycleBuilder ReplaceGateway(ProbeOutcome outcome, TimeSpan? rtt)
    {
        _results.RemoveAll(r => r.Scope == ProbeScope.Gateway);
        _results.Add(Measured(new ProbeResult(ProbeKind.Icmp, ProbeScope.Gateway, GatewayIp, outcome, rtt)));
        return this;
    }

    private CycleBuilder ReplaceExternalIcmp(int total, int succeeding, TimeSpan? rtt = null)
    {
        string[] targets = ["1.1.1.1", "8.8.8.8", "9.9.9.9"];
        _results.RemoveAll(r => r.Scope == ProbeScope.External && r.Kind == ProbeKind.Icmp);

        for (var i = 0; i < total; i++)
        {
            var ok = i < succeeding;

            _results.Add(Measured(new ProbeResult(
                ProbeKind.Icmp,
                ProbeScope.External,
                targets[i % targets.Length],
                ok ? ProbeOutcome.Success : ProbeOutcome.TimedOut,
                ok ? rtt ?? TimeSpan.FromMilliseconds(20) : null)
            {
                Family = AddressFamily.InterNetwork,
            }));
        }

        return this;
    }

    private CycleBuilder ReplaceExternalTcp(int total, int succeeding)
    {
        string[] targets = ["1.1.1.1:443", "8.8.8.8:443"];
        _results.RemoveAll(r => r.Scope == ProbeScope.External && r.Kind == ProbeKind.TcpConnect);

        for (var i = 0; i < total; i++)
        {
            var ok = i < succeeding;

            _results.Add(Measured(new ProbeResult(
                ProbeKind.TcpConnect,
                ProbeScope.External,
                targets[i % targets.Length],
                ok ? ProbeOutcome.Success : ProbeOutcome.TimedOut,
                ok ? TimeSpan.FromMilliseconds(25) : null)));
        }

        return this;
    }

    private CycleBuilder ReplaceTls(bool succeeds)
    {
        _results.RemoveAll(r => r.Kind == ProbeKind.TlsHandshake);

        _results.Add(Measured(new ProbeResult(
            ProbeKind.TlsHandshake,
            ProbeScope.External,
            "one.one.one.one:443",
            succeeds ? ProbeOutcome.Success : ProbeOutcome.Failed,
            succeeds ? TimeSpan.FromMilliseconds(40) : null)));

        return this;
    }

    private CycleBuilder ReplaceHttp(ProbeOutcome outcome, bool captivePortal)
    {
        _results.RemoveAll(r => r.Kind == ProbeKind.Http);

        _results.Add(Measured(new ProbeResult(
            ProbeKind.Http,
            ProbeScope.External,
            "http://www.msftconnecttest.com/connecttest.txt",
            outcome,
            outcome == ProbeOutcome.Success ? TimeSpan.FromMilliseconds(60) : null)
        {
            CaptivePortalSuspected = captivePortal,
        }));

        return this;
    }

    private CycleBuilder ReplaceDns(bool isp, bool pub, bool system)
    {
        _results.RemoveAll(r => r.Kind == ProbeKind.Dns);

        Add(DnsResolverRole.IspAssigned, "192.168.1.1", isp);
        Add(DnsResolverRole.Public, "1.1.1.1", pub);
        Add(DnsResolverRole.System, "system", system);

        return this;

        void Add(DnsResolverRole role, string target, bool ok) =>
            _results.Add(Measured(new ProbeResult(
                ProbeKind.Dns,
                ProbeScope.External,
                target,
                ok ? ProbeOutcome.Success : ProbeOutcome.Failed,
                ok ? TimeSpan.FromMilliseconds(15) : null)
            {
                DnsRole = role,
            }));
    }
}
