using IEM.Core.Probes;

namespace IEM.Core.Health;

/// <summary>
/// Evaluates and tracks target health across chronological sampling windows.
/// Implements deterministic hysteresis and explicit reason codes.
/// Invariants:
/// 39. ABSENCE_OF_REPLY_NEVER_PROVES_TARGET_CAPABILITY
/// 40. TARGET_HEALTH_NEVER_REWRITES_PRIOR_EVIDENCE
/// 41. TARGET_HEALTH_CHANGE_NEVER_RETROACTIVELY_REWEIGHTS_HISTORY
/// 42. TARGET_EXCLUSION_IS_ALWAYS_VISIBLE_AND_REASONED
/// 43. SHARED_FAILURE_NEVER_BECOMES_TARGET_FAILURE_BY_DEFAULT
/// 44. TARGET_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE
/// 45. TARGET_HEALTH_IS_SCOPED_TO_ENDPOINT_AND_ADDRESS_FAMILY
/// </summary>
public sealed class TargetHealthEvaluator
{
    private readonly TargetIdentity _target;
    private readonly TargetHealthPolicy _policy;
    private readonly List<TargetHealthSnapshot> _history = new();

    private TargetCapabilityState _capability = TargetCapabilityState.Unknown;
    private TargetHealthState _state = TargetHealthState.Unknown;
    private EvidenceContribution _contribution = EvidenceContribution.Full;
    private double _weight = 1.0;

    private int _consecutiveBadWindows;
    private int _consecutiveSilentWindows;
    private int _consecutiveGoodWindows;

    public TargetHealthEvaluator(TargetIdentity target, TargetHealthPolicy? policy = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _policy = policy ?? TargetHealthPolicy.Default;
    }

    public TargetIdentity Target => _target;
    public TargetHealthPolicy Policy => _policy;
    public TargetHealthState CurrentState => _state;
    public TargetCapabilityState CurrentCapability => _capability;
    public EvidenceContribution CurrentContribution => _contribution;
    public double CurrentWeight => _weight;
    public IReadOnlyList<TargetHealthSnapshot> History => _history.AsReadOnly();

    /// <summary>
    /// Processes a new chronological window of probe statistics and returns an immutable snapshot.
    /// </summary>
    public TargetHealthSnapshot EvaluateWindow(TargetProbeStatistics windowStats, PeerContext? peerContext = null)
    {
        ArgumentNullException.ThrowIfNull(windowStats);

        var reasons = new List<string>();
        var sourceEvidenceRefs = new List<string>
        {
            $"window:{windowStats.SampleStartedUtc:O}..{windowStats.SampleEndedUtc:O}",
            $"exec={windowStats.ExecutedCount};replies={windowStats.ReplyCount};no_replies={windowStats.NoReplyCount}",
        };

        // 1. Update capability state
        // Invariant 39: ABSENCE_OF_REPLY_NEVER_PROVES_TARGET_CAPABILITY
        if (windowStats.ReplyCount > 0)
        {
            _capability = TargetCapabilityState.ResponseObserved;
        }
        else if (_capability == TargetCapabilityState.Unknown && windowStats.ExecutedCount > 0)
        {
            _capability = TargetCapabilityState.ResponseNotYetObserved;
        }

        // Check if sample size is too small for statistical evaluation
        if (windowStats.EligibleCount < _policy.MinEligibleSamplesPerWindow)
        {
            reasons.Add($"Nedovoljan broj uzoraka ({windowStats.EligibleCount}/{_policy.MinEligibleSamplesPerWindow}) za promenu stanja zdravlja.");
            return RecordSnapshot(reasons, sourceEvidenceRefs, windowStats.SampleEndedUtc, peerContext);
        }

        var lossRatio = windowStats.NoReplyRatio ?? 0.0;

        // 2. Check for shared failure across peers
        // Invariant 43: SHARED_FAILURE_NEVER_BECOMES_TARGET_FAILURE_BY_DEFAULT
        if (peerContext?.IsSharedNetworkFailure == true && lossRatio > _policy.HealthyLossThreshold)
        {
            reasons.Add($"Detektovan zajednički mrežni prekid kod vršnjaka ({peerContext.FailingPeers}/{peerContext.TotalPeers}); cilj nije izolovan kao nezdrav.");
            // Do not advance target-specific failure counters during a network-wide incident
            return RecordSnapshot(reasons, sourceEvidenceRefs, windowStats.SampleEndedUtc, peerContext);
        }

        // 3. Evaluate target-specific loss with hysteresis
        if (lossRatio <= _policy.HealthyLossThreshold)
        {
            _consecutiveBadWindows = 0;
            _consecutiveSilentWindows = 0;

            if (_state is TargetHealthState.Degraded or TargetHealthState.Unresponsive or TargetHealthState.Recovering)
            {
                _consecutiveGoodWindows++;
                if (_consecutiveGoodWindows >= _policy.RecoveryWindowsRequired)
                {
                    _state = TargetHealthState.Healthy;
                    _contribution = EvidenceContribution.Full;
                    _weight = 1.0;
                    reasons.Add($"Cilj je potpuno oporavljen nakon {_consecutiveGoodWindows} uzastopna zdrava prozora.");
                }
                else
                {
                    _state = TargetHealthState.Recovering;
                    _contribution = EvidenceContribution.Reduced;
                    _weight = 0.5;
                    reasons.Add($"Cilj u fazi oporavka ({_consecutiveGoodWindows}/{_policy.RecoveryWindowsRequired} uspešnih prozora).");
                }
            }
            else
            {
                _state = TargetHealthState.Healthy;
                _contribution = EvidenceContribution.Full;
                _weight = 1.0;
                reasons.Add($"Cilj stabilan i zdrav (gubitak odgovora {lossRatio * 100.0:F1}%).");
            }
        }
        else
        {
            _consecutiveGoodWindows = 0;
            _consecutiveBadWindows++;

            if (lossRatio >= 1.0)
            {
                _consecutiveSilentWindows++;
            }
            else
            {
                _consecutiveSilentWindows = 0;
            }

            if (_consecutiveSilentWindows >= _policy.FailureWindowsToUnresponsive)
            {
                _state = TargetHealthState.Unresponsive;
                _contribution = EvidenceContribution.Suspended;
                _weight = 0.0;
                // Invariant 42: TARGET_EXCLUSION_IS_ALWAYS_VISIBLE_AND_REASONED
                reasons.Add($"Cilj je suspendovan iz dokaza: potpuno bez odgovora {_consecutiveSilentWindows} uzastopnih prozora dok vršnjaci rade.");
            }
            else if (_consecutiveBadWindows >= _policy.FailureWindowsToDegrade)
            {
                _state = TargetHealthState.Degraded;
                _contribution = EvidenceContribution.Reduced;
                _weight = 0.5;
                reasons.Add($"Cilj je degradiran (povišen gubitak {lossRatio * 100.0:F1}% kroz {_consecutiveBadWindows} uzastopna prozora).");
            }
            else
            {
                reasons.Add($"Zabeležen povišen gubitak odgovora ({lossRatio * 100.0:F1}%), čeka se potvrda kroz prozor ({_consecutiveBadWindows}/{_policy.FailureWindowsToDegrade}).");
            }
        }

        return RecordSnapshot(reasons, sourceEvidenceRefs, windowStats.SampleEndedUtc, peerContext);
    }

    private TargetHealthSnapshot RecordSnapshot(
        List<string> reasons,
        List<string> sourceEvidenceRefs,
        DateTimeOffset timestampUtc,
        PeerContext? peerContext)
    {
        var snapshotId = $"ths-{_target.TargetId}-{_history.Count + 1}";
        var interpretationRefId = $"policy:v{_policy.PolicyVersion}:{_policy.PolicyHash}";

        var snapshot = new TargetHealthSnapshot(
            SnapshotId: snapshotId,
            Target: _target,
            State: _state,
            Capability: _capability,
            Contribution: _contribution,
            Weight: _weight,
            ReasonCodes: reasons,
            SourceEvidenceRefs: sourceEvidenceRefs,
            InterpretationRefId: interpretationRefId,
            EvaluatedAtUtc: timestampUtc,
            PeerContext: peerContext);

        _history.Add(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Rebuilds complete deterministic health history from chronological raw probe statistics.
    /// Invariant 44: TARGET_HEALTH_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE.
    /// </summary>
    public static IReadOnlyList<TargetHealthSnapshot> RebuildHealthHistory(
        TargetIdentity target,
        IEnumerable<TargetProbeStatistics> windowStatistics,
        TargetHealthPolicy? policy = null,
        Func<TargetProbeStatistics, PeerContext?>? peerContextProvider = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(windowStatistics);

        var evaluator = new TargetHealthEvaluator(target, policy);
        foreach (var window in windowStatistics.OrderBy(w => w.SampleStartedUtc))
        {
            var peerContext = peerContextProvider?.Invoke(window);
            evaluator.EvaluateWindow(window, peerContext);
        }

        return evaluator.History;
    }
}
