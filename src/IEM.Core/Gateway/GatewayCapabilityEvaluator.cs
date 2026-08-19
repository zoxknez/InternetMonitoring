namespace IEM.Core.Gateway;

/// <summary>
/// Learns, tracks, and assesses gateway capabilities across chronological observations.
/// Invariants 46-54.
/// </summary>
public sealed class GatewayCapabilityEvaluator
{
    private readonly GatewayIdentity _gateway;
    private readonly GatewayCapabilityPolicy _policy;
    private readonly List<GatewayAssessmentSnapshot> _history = new();

    private readonly Dictionary<GatewayCapabilityKind, CapabilityTracker> _trackers = new();
    private DateTimeOffset? _firstObservationUtc;
    private DateTimeOffset? _lastObservationUtc;

    public GatewayCapabilityEvaluator(GatewayIdentity gateway, GatewayCapabilityPolicy? policy = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _policy = policy ?? GatewayCapabilityPolicy.Default;

        foreach (var kind in Enum.GetValues<GatewayCapabilityKind>())
        {
            _trackers[kind] = new CapabilityTracker(kind);
        }
    }

    public GatewayIdentity Gateway => _gateway;
    public GatewayCapabilityPolicy Policy => _policy;
    public IReadOnlyList<GatewayAssessmentSnapshot> History => _history.AsReadOnly();

    /// <summary>
    /// Evaluates a single gateway observation and produces an assessment snapshot.
    /// </summary>
    public GatewayAssessmentSnapshot ProcessObservation(GatewayCapabilityObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        _firstObservationUtc ??= observation.AttemptedAtUtc;
        _lastObservationUtc = observation.AttemptedAtUtc;

        var tracker = _trackers[observation.CapabilityKind];
        tracker.ProcessObservation(observation, _policy);

        return RecordAssessmentSnapshot(observation);
    }

    /// <summary>
    /// Processes a batch of observations and produces an aggregate assessment snapshot.
    /// </summary>
    public GatewayAssessmentSnapshot ProcessBatch(IReadOnlyList<GatewayCapabilityObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (observations.Count == 0)
        {
            return RecordAssessmentSnapshot(null);
        }

        foreach (var obs in observations.OrderBy(o => o.AttemptedAtUtc))
        {
            _firstObservationUtc ??= obs.AttemptedAtUtc;
            _lastObservationUtc = obs.AttemptedAtUtc;
            _trackers[obs.CapabilityKind].ProcessObservation(obs, _policy);
        }

        return RecordAssessmentSnapshot(observations.Last());
    }

    public GatewayCapabilityProfile GetCurrentProfile()
    {
        var capabilities = _trackers.Values.Select(t => t.ToState()).ToList();
        var learnedFrom = _firstObservationUtc ?? DateTimeOffset.UtcNow;
        var learnedThrough = _lastObservationUtc ?? learnedFrom;
        var interpretationRefId = $"profile:v{_policy.PolicyVersion}:{_policy.PolicyHash}";

        return new GatewayCapabilityProfile(
            Gateway: _gateway,
            Capabilities: capabilities,
            LearnedFromUtc: learnedFrom,
            LearnedThroughUtc: learnedThrough,
            SourceEvidenceRefs: new[] { $"obs_count={_history.Count}" },
            InterpretationRefId: interpretationRefId,
            PolicyRefId: _policy.PolicyHash,
            ProfileVersion: _policy.PolicyVersion);
    }

    private GatewayAssessmentSnapshot RecordAssessmentSnapshot(GatewayCapabilityObservation? triggerObservation)
    {
        var reasons = new List<string>();
        var sourceEvidenceRefs = new List<string>();

        if (triggerObservation is not null)
        {
            sourceEvidenceRefs.Add($"obs:{triggerObservation.ObservationId}");
            sourceEvidenceRefs.Add($"kind={triggerObservation.CapabilityKind};outcome={triggerObservation.Outcome};method={triggerObservation.Method}");
        }

        var capabilityStates = _trackers.Values.Select(t => t.ToState()).ToList();

        // Evaluate overall behavior state
        var behavior = EvaluateBehaviorState(reasons);

        var snapshotId = $"gas-{_gateway.GatewayId}-{_history.Count + 1}";
        var interpretationRefId = $"eval:v{_policy.PolicyVersion}:{_policy.PolicyHash}";
        var evaluatedAt = _lastObservationUtc ?? DateTimeOffset.UtcNow;

        var snapshot = new GatewayAssessmentSnapshot(
            SnapshotId: snapshotId,
            Gateway: _gateway,
            BehaviorState: behavior,
            CapabilityStates: capabilityStates,
            ReasonCodes: reasons,
            SourceEvidenceRefs: sourceEvidenceRefs,
            InterpretationRefId: interpretationRefId,
            EvaluatedAtUtc: evaluatedAt);

        _history.Add(snapshot);
        return snapshot;
    }

    private GatewayBehaviorState EvaluateBehaviorState(List<string> reasons)
    {
        var missingPreviouslyObserved = _trackers.Values
            .Where(t => t.EvidenceState == CapabilityEvidenceState.PreviouslyObserved && !t.IsRecovering)
            .ToList();

        var observedSupported = _trackers.Values
            .Where(t => t.EvidenceState == CapabilityEvidenceState.ObservedSupported)
            .ToList();

        var recovering = _trackers.Values
            .Where(t => t.IsRecovering)
            .ToList();

        if (missingPreviouslyObserved.Count > 0)
        {
            foreach (var m in missingPreviouslyObserved)
            {
                reasons.Add($"Prethodno potvrđena sposobnost rutera '{m.Kind}' više ne odgovara ({m.ConsecutiveFailures} uzastopnih neuspeha).");
            }
            return GatewayBehaviorState.PreviouslyObservedCapabilityMissing;
        }

        if (recovering.Count > 0)
        {
            foreach (var r in recovering)
            {
                reasons.Add($"Sposobnost '{r.Kind}' u fazi oporavka ({r.ConsecutiveRecoverySuccesses}/{_policy.RecoveryWindowsRequired}).");
            }
            return GatewayBehaviorState.Recovering;
        }

        if (observedSupported.Count > 0)
        {
            reasons.Add($"Ruter normalno odgovara za {observedSupported.Count} potvrđene sposobnosti.");
            return GatewayBehaviorState.NormallyResponding;
        }

        reasons.Add("Sposobnosti rutera su u fazi početnog učenja (Unknown/ResponseNotYetObserved).");
        return GatewayBehaviorState.Unknown;
    }

    /// <summary>
    /// Deterministically rebuilds gateway assessment history from raw observations.
    /// Invariant 54: GATEWAY_CAPABILITY_IS_REBUILDABLE_FROM_PERSISTED_EVIDENCE.
    /// </summary>
    public static IReadOnlyList<GatewayAssessmentSnapshot> RebuildHistory(
        GatewayIdentity gateway,
        IEnumerable<GatewayCapabilityObservation> observations,
        GatewayCapabilityPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(observations);

        var evaluator = new GatewayCapabilityEvaluator(gateway, policy);
        foreach (var obs in observations.OrderBy(o => o.AttemptedAtUtc))
        {
            evaluator.ProcessObservation(obs);
        }

        return evaluator.History;
    }

    private sealed class CapabilityTracker
    {
        public GatewayCapabilityKind Kind { get; }
        public CapabilityEvidenceState EvidenceState { get; private set; } = CapabilityEvidenceState.Unknown;
        public DateTimeOffset? FirstObservedUtc { get; private set; }
        public DateTimeOffset? LastObservedUtc { get; private set; }
        public int SuccessfulObservationCount { get; private set; }
        public int EligibleAttemptCount { get; private set; }

        public int ConsecutiveFailures { get; private set; }
        public int ConsecutiveRecoverySuccesses { get; private set; }
        public bool IsRecovering => EvidenceState == CapabilityEvidenceState.PreviouslyObserved && ConsecutiveRecoverySuccesses > 0;

        public CapabilityTracker(GatewayCapabilityKind kind)
        {
            Kind = kind;
        }

        public void ProcessObservation(GatewayCapabilityObservation obs, GatewayCapabilityPolicy policy)
        {
            if (obs.Outcome == ObservationOutcome.NotConfigured)
            {
                EvidenceState = CapabilityEvidenceState.NotAssessed;
                return;
            }

            if (obs.Outcome == ObservationOutcome.LocalExecutionFailure)
            {
                // Local failure does NOT count against gateway capabilities
                return;
            }

            EligibleAttemptCount++;

            if (obs.Outcome == ObservationOutcome.Success)
            {
                SuccessfulObservationCount++;
                FirstObservedUtc ??= obs.AttemptedAtUtc;
                LastObservedUtc = obs.AttemptedAtUtc;
                ConsecutiveFailures = 0;

                // Invariant 47: OBSERVED_GATEWAY_CAPABILITY_IS_ESTABLISHED_ONLY_BY_POSITIVE_EVIDENCE
                if (EvidenceState == CapabilityEvidenceState.PreviouslyObserved)
                {
                    ConsecutiveRecoverySuccesses++;
                    if (ConsecutiveRecoverySuccesses >= policy.RecoveryWindowsRequired)
                    {
                        EvidenceState = CapabilityEvidenceState.ObservedSupported;
                        ConsecutiveRecoverySuccesses = 0;
                    }
                }
                else if (SuccessfulObservationCount >= policy.MinimumPositiveObservations)
                {
                    EvidenceState = CapabilityEvidenceState.ObservedSupported;
                }
            }
            else
            {
                ConsecutiveRecoverySuccesses = 0;
                ConsecutiveFailures++;

                // Invariant 46: ABSENCE_OF_GATEWAY_RESPONSE_NEVER_PROVES_UNSUPPORTED_CAPABILITY
                if (EvidenceState == CapabilityEvidenceState.Unknown)
                {
                    EvidenceState = CapabilityEvidenceState.ResponseNotYetObserved;
                }
                else if (EvidenceState == CapabilityEvidenceState.ObservedSupported)
                {
                    if (ConsecutiveFailures >= policy.MissingCapabilityConsecutiveWindows)
                    {
                        EvidenceState = CapabilityEvidenceState.PreviouslyObserved;
                    }
                }
            }
        }

        public GatewayCapabilityState ToState()
        {
            return new GatewayCapabilityState(
                Kind: Kind,
                EvidenceState: EvidenceState,
                FirstObservedUtc: FirstObservedUtc,
                LastObservedUtc: LastObservedUtc,
                SuccessfulObservationCount: SuccessfulObservationCount,
                EligibleAttemptCount: EligibleAttemptCount);
        }
    }
}
