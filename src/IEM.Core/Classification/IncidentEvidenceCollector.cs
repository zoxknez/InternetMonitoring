using IEM.Core.Model;

namespace IEM.Core.Classification;

/// <summary>
/// Gathers the evidence for one outage while it is happening.
/// <para>
/// It has to be collected live. Almost every signal is of the form "did this hold for the
/// whole outage" - the adapter never dropped, the SSID stayed visible, the router never
/// restarted - and none of that can be reconstructed afterwards from a duration and a state
/// name. Waiting until the incident closed and then asking the network would answer a
/// question about the present, not about the outage.
/// </para>
/// <para>
/// Every field starts unknown and stays unknown unless something is actually observed.
/// "Could not check" and "checked and it held" are different claims, and the scorer needs
/// to be able to tell them apart.
/// </para>
/// </summary>
public sealed class IncidentEvidenceCollector
{
    private bool _active;

    // Each of these is null until the first sample says something about it, then narrows.
    private bool? _linkStayedUp;
    private bool? _wired;
    private bool? _noAdapterReset;
    private bool? _noClockJump;
    private bool? _noSaturation;
    private bool? _ssidVisible;
    private bool? _signalHealthy;
    private bool? _noRoaming;
    private bool? _gatewayReachable;
    private bool? _routerReportsWanDown;
    private bool? _noCpeReboot;
    private bool? _allIcmpFailed;
    private bool? _allTcpFailed;
    private bool? _tlsFailed;
    private bool? _publicDnsFailed;
    private bool? _httpFailed;

    /// <summary>Busiest second of our own traffic during the outage, when it was measured.</summary>
    private long? _peakLocalTraffic;

    private bool _sleptDuring;
    private bool _sleepKnown;
    private string? _addressBefore;
    private string? _addressAfter;

    /// <summary>Remembers the public address while the connection still works.</summary>
    public void ObserveHealthy(ProbeCycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        if (!_active && cycle.Link.PublicAddress is { Length: > 0 } address)
        {
            // The address as it stood before any trouble. It cannot be read during an
            // outage, so the comparison has to be set up in advance.
            _addressBefore = address;
        }
    }

    /// <summary>Starts gathering for a new outage.</summary>
    public void Begin()
    {
        _active = true;

        _linkStayedUp = null;
        _wired = null;
        _noAdapterReset = null;
        _noClockJump = null;
        _noSaturation = null;
        _peakLocalTraffic = null;
        _ssidVisible = null;
        _signalHealthy = null;
        _noRoaming = null;
        _gatewayReachable = null;
        _routerReportsWanDown = null;
        _noCpeReboot = null;
        _allIcmpFailed = null;
        _allTcpFailed = null;
        _tlsFailed = null;
        _publicDnsFailed = null;
        _httpFailed = null;

        _sleptDuring = false;
        _sleepKnown = true;
        _addressAfter = null;
    }

    public bool IsCollecting => _active;

    /// <summary>Folds one failing sample into the picture.</summary>
    public void Observe(ProbeCycle cycle, ClassificationContext context, bool clockAnomalous)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(context);

        if (!_active)
        {
            return;
        }

        var link = cycle.Link;
        var wireless = link.Wireless;

        // "Held throughout" signals: once one sample breaks them, they stay broken.
        Narrow(ref _linkStayedUp, link.IsUp);
        Narrow(ref _wired, link.Medium == LinkMedium.Ethernet);
        Narrow(ref _noAdapterReset, link.Status != LinkStatus.Missing);
        Narrow(ref _noClockJump, !clockAnomalous);
        // Our own measurement is known saturation; anything else is only known when the
        // adapter counters could be read. Without a reading this stays null - "not checked"
        // - because it used to narrow to true on every cycle and so reported every incident
        // as having ruled out the machine's own traffic, which nothing had ever looked at.
        if (cycle.SelfTestRunning)
        {
            Narrow(ref _noSaturation, false);
        }
        else if (cycle.LocalTrafficBytesPerSecond is { } localTraffic)
        {
            Narrow(ref _noSaturation, !cycle.LocalTrafficHeavy);

            // The worst moment, not an average: the question is whether our own traffic
            // could explain the outage at all, and the busiest second is what answers it.
            _peakLocalTraffic = Math.Max(_peakLocalTraffic ?? 0, localTraffic);
        }

        Narrow(ref _noRoaming, !context.BssidChanged);
        // Only a live reading may testify here. An empty reading - router not answering,
        // reading too old - used to narrow this as though it had checked and found no
        // reboot, recording the opposite of "could not check" into the evidence.
        if (link.RouterChecked)
        {
            Narrow(ref _noCpeReboot, !link.RouterReconnected);
        }

        // Wi-Fi detail only exists on a wireless link, and only when a scan succeeded.
        // Absent scan data must not read as "the network was gone".
        if (wireless?.SsidVisibleInScan is { } visible)
        {
            Narrow(ref _ssidVisible, visible);
        }

        if (wireless?.IsSignalWeak is { } weak)
        {
            Narrow(ref _signalHealthy, !weak);
        }

        // The gateway is only evidence when there is one to test.
        if (link.HasGateway)
        {
            Narrow(ref _gatewayReachable, cycle.Gateway.AnyFreshSuccess);
        }

        if (link.Router is { } router)
        {
            // Widen rather than narrow: the router saying its WAN is down even once during
            // the outage is what counts, not it saying so every time.
            Widen(ref _routerReportsWanDown, router.IsDisconnected);
        }

        // Failure signals, only counted where the family was actually attempted.
        NarrowIfAttempted(ref _allIcmpFailed, cycle.ExternalIcmp);
        NarrowIfAttempted(ref _allTcpFailed, cycle.ExternalTcp);
        NarrowIfAttempted(ref _tlsFailed, cycle.ExternalTls);
        NarrowIfAttempted(ref _publicDnsFailed, cycle.DnsPublic);
        NarrowIfAttempted(ref _httpFailed, cycle.Http);
    }

    /// <summary>
    /// Monitoring paused during this outage, which withdraws whichever claims the pause
    /// actually undermines.
    /// <para>
    /// Only those. Marking the clock as having jumped on every pause would print "the system
    /// clock moved" against outages where it demonstrably did not - a false statement in a
    /// document meant to be handed to an operator, and one they could disprove from their
    /// own logs.
    /// </para>
    /// </summary>
    /// <param name="sleep">The operating system reported a suspend.</param>
    /// <param name="clockAdjusted">The pause was explained by the wall clock being corrected.</param>
    public void NoteMonitoringPaused(bool sleep, bool clockAdjusted)
    {
        if (!_active)
        {
            return;
        }

        _sleepKnown = true;
        _sleptDuring = _sleptDuring || sleep;

        if (clockAdjusted)
        {
            Narrow(ref _noClockJump, false);
        }
    }

    /// <summary>Finishes gathering and returns what was established.</summary>
    public IncidentEvidence Build(ProbeCycle? recovery = null)
    {
        _active = false;

        if (recovery?.Link.PublicAddress is { Length: > 0 } address)
        {
            _addressAfter = address;
        }

        return new IncidentEvidence
        {
            LinkStayedUp = _linkStayedUp,
            WiredConnection = _wired,
            NoAdapterReset = _noAdapterReset,
            NoSystemSleep = _sleepKnown ? !_sleptDuring : null,
            NoClockJump = _noClockJump,
            NoLocalSaturation = _noSaturation,
            PeakLocalTrafficBytesPerSecond = _peakLocalTraffic,

            SsidRemainedVisible = _ssidVisible,
            SignalHealthy = _signalHealthy,
            NoRoaming = _noRoaming,

            GatewayRemainedReachable = _gatewayReachable,
            RouterReportsWanDown = _routerReportsWanDown,
            NoCpeReboot = _noCpeReboot,

            AllExternalIcmpFailed = _allIcmpFailed,
            AllExternalTcpFailed = _allTcpFailed,
            TlsFailed = _tlsFailed,
            PublicDnsFailed = _publicDnsFailed,
            HttpFailed = _httpFailed,

            // Only claimable when both readings exist. A missing one means the router could
            // not be asked, not that the address held.
            PublicAddressChanged = _addressBefore is not null && _addressAfter is not null
                ? !string.Equals(_addressBefore, _addressAfter, StringComparison.Ordinal)
                : null,
        };
    }

    /// <summary>True only if it was true on every sample.</summary>
    private static void Narrow(ref bool? held, bool observation) => held = (held ?? true) && observation;

    /// <summary>True if it was true on any sample.</summary>
    private static void Widen(ref bool? seen, bool observation) => seen = (seen ?? false) || observation;

    private static void NarrowIfAttempted(ref bool? allFailed, ProbeTally tally)
    {
        if (!tally.IsSilent)
        {
            Narrow(ref allFailed, tally.AllFailed);
        }
    }
}
