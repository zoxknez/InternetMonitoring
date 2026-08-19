namespace IEM.Core.Time;

/// <summary>
/// Evaluates clock continuity, boot transitions, and suspend intervals between chronological samples.
/// Invariants 97-113.
/// </summary>
public static class TimeContinuityEvaluator
{
    public static ClockContinuityAssessment EvaluateTransition(
        ClockSample prev,
        ClockSample curr,
        TimeContinuityPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(prev);
        ArgumentNullException.ThrowIfNull(curr);
        policy ??= TimeContinuityPolicy.Default;

        var interpretationRefId = $"tcont:v{policy.PolicyVersion}:{policy.PolicyHash}";
        var reasons = new List<string>();

        // 1. Invariant 101 & 107: BOOT_IDENTITY_CHANGE_SPLITS_TIME_CONTINUITY & MONOTONIC_DURATION_IS_NEVER_COMPUTED_ACROSS_BOOT_INSTANCES
        if (!string.Equals(prev.BootInstanceId, curr.BootInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"Detektovana granica restarta sistema (Prethodni boot: '{prev.BootInstanceId}', Novi boot: '{curr.BootInstanceId}'). Monotoni interval se ne računa preko granice.");
            return new ClockContinuityAssessment(
                PreviousSampleRef: prev.SampleId,
                CurrentSampleRef: curr.SampleId,
                State: ClockContinuityState.BootBoundaryObserved,
                WallClockDelta: curr.CapturedUtc - prev.CapturedUtc,
                MonotonicDelta: TimeSpan.Zero,
                ActiveElapsedDelta: curr.ActiveElapsedExcludingSuspend,
                BootElapsedDelta: curr.BootElapsedIncludingSuspend,
                Divergence: TimeSpan.Zero,
                SuspendDuration: TimeSpan.Zero,
                ReasonCodes: reasons,
                InterpretationRefId: interpretationRefId);
        }

        var wallDelta = curr.CapturedUtc - prev.CapturedUtc;

        var freq = curr.MonotonicFrequency > 0 ? curr.MonotonicFrequency : (prev.MonotonicFrequency > 0 ? prev.MonotonicFrequency : 1);
        var monotonicTicks = curr.MonotonicTimestamp - prev.MonotonicTimestamp;
        var monotonicDelta = TimeSpan.FromSeconds((double)monotonicTicks / freq);

        var activeDelta = curr.ActiveElapsedExcludingSuspend - prev.ActiveElapsedExcludingSuspend;
        var bootDelta = curr.BootElapsedIncludingSuspend - prev.BootElapsedIncludingSuspend;

        var suspendDuration = bootDelta > activeDelta ? bootDelta - activeDelta : TimeSpan.Zero;
        var divergence = wallDelta - monotonicDelta;

        // 2. Invariant 97 & 108: HOST_SUSPENSION_GAP_NEVER_CONTRIBUTES_NETWORK_OUTAGE_DURATION
        if (suspendDuration >= policy.SuspendDetectionTolerance)
        {
            reasons.Add($"Detektovan interval mirovanja/spavanja računara (Suspend/Sleep) u trajanju od približno {suspendDuration.TotalMinutes:F1} min (BootDelta={bootDelta.TotalMinutes:F1}m, ActiveDelta={activeDelta.TotalMinutes:F1}m).");
            return new ClockContinuityAssessment(
                PreviousSampleRef: prev.SampleId,
                CurrentSampleRef: curr.SampleId,
                State: ClockContinuityState.SuspendIntervalObserved,
                WallClockDelta: wallDelta,
                MonotonicDelta: monotonicDelta,
                ActiveElapsedDelta: activeDelta,
                BootElapsedDelta: bootDelta,
                Divergence: divergence,
                SuspendDuration: suspendDuration,
                ReasonCodes: reasons,
                InterpretationRefId: interpretationRefId);
        }

        // 3. Invariant 98 & 104: CLOCK_DISCONTINUITY_NEVER_IDENTIFIES_AN_UNPROVEN_ADJUSTMENT_CAUSE
        if (divergence > policy.WallVsMonotonicTolerance)
        {
            reasons.Add($"Sistemski UTC sat pomeren je unapred za približno {divergence.TotalSeconds:F2} s u odnosu na monotoni vremenski tok.");
            return new ClockContinuityAssessment(
                PreviousSampleRef: prev.SampleId,
                CurrentSampleRef: curr.SampleId,
                State: ClockContinuityState.ForwardAdjustmentObserved,
                WallClockDelta: wallDelta,
                MonotonicDelta: monotonicDelta,
                ActiveElapsedDelta: activeDelta,
                BootElapsedDelta: bootDelta,
                Divergence: divergence,
                SuspendDuration: suspendDuration,
                ReasonCodes: reasons,
                InterpretationRefId: interpretationRefId);
        }

        if (divergence < -policy.WallVsMonotonicTolerance)
        {
            reasons.Add($"Sistemski UTC sat pomeren je unazad za približno {Math.Abs(divergence.TotalSeconds):F2} s u odnosu na monotoni vremenski tok.");
            return new ClockContinuityAssessment(
                PreviousSampleRef: prev.SampleId,
                CurrentSampleRef: curr.SampleId,
                State: ClockContinuityState.BackwardAdjustmentObserved,
                WallClockDelta: wallDelta,
                MonotonicDelta: monotonicDelta,
                ActiveElapsedDelta: activeDelta,
                BootElapsedDelta: bootDelta,
                Divergence: divergence,
                SuspendDuration: suspendDuration,
                ReasonCodes: reasons,
                InterpretationRefId: interpretationRefId);
        }

        // 4. Counter regression
        if (monotonicTicks < 0)
        {
            reasons.Add("Zabeležena anomalija regresije monotonog brojača.");
            return new ClockContinuityAssessment(
                PreviousSampleRef: prev.SampleId,
                CurrentSampleRef: curr.SampleId,
                State: ClockContinuityState.CounterDiscontinuity,
                WallClockDelta: wallDelta,
                MonotonicDelta: monotonicDelta,
                ActiveElapsedDelta: activeDelta,
                BootElapsedDelta: bootDelta,
                Divergence: divergence,
                SuspendDuration: suspendDuration,
                ReasonCodes: reasons,
                InterpretationRefId: interpretationRefId);
        }

        // 5. Continuous
        reasons.Add("Kontinualan i usklađen vremenski tok.");
        return new ClockContinuityAssessment(
            PreviousSampleRef: prev.SampleId,
            CurrentSampleRef: curr.SampleId,
            State: ClockContinuityState.Continuous,
            WallClockDelta: wallDelta,
            MonotonicDelta: monotonicDelta,
            ActiveElapsedDelta: activeDelta,
            BootElapsedDelta: bootDelta,
            Divergence: divergence,
            SuspendDuration: suspendDuration,
            ReasonCodes: reasons,
            InterpretationRefId: interpretationRefId);
    }

    /// <summary>
    /// Evaluates boot identity transitions given consecutive boot observations.
    /// Invariant 100, 109, 110.
    /// </summary>
    public static BootIdentityAssessment EvaluateBoot(
        BootObservation? previous,
        BootObservation? current,
        TimeContinuityPolicy? policy = null)
    {
        policy ??= TimeContinuityPolicy.Default;
        var interpretationRefId = $"tboot:v{policy.PolicyVersion}:{policy.PolicyHash}";
        var reasons = new List<string>();
        var sourceRefs = new List<string>();

        if (current == null)
        {
            if (previous != null)
            {
                sourceRefs.Add($"prev_obs:{previous.ObservationId}");
                reasons.Add($"Prethodni boot identitet '{previous.BootInstanceId}' je poznat, ali trenutni boot identitet nije dostupan.");
                reasons.Add("PREVIOUS_BOOT_ID_KNOWN_CURRENT_UNAVAILABLE");
            }
            else
            {
                reasons.Add("Trenutni boot identitet nije dostupan.");
            }

            reasons.Add("BOOT_IDENTITY_AMBIGUOUS");
            reasons.Add("BOOT_ID_UNAVAILABLE");

            return new BootIdentityAssessment(
                BootInstanceId: null,
                State: BootContinuityState.Ambiguous,
                ReasonCodes: reasons,
                SourceEvidenceRefs: sourceRefs,
                InterpretationRefId: interpretationRefId);
        }

        sourceRefs.Add($"obs:{current.ObservationId}");

        if (previous == null)
        {
            reasons.Add($"Uspostavljen početni identitet pokretanja sistema '{current.BootInstanceId}'.");
            return new BootIdentityAssessment(
                current.BootInstanceId,
                BootContinuityState.Established,
                reasons,
                sourceRefs,
                interpretationRefId);
        }

        sourceRefs.Add($"prev_obs:{previous.ObservationId}");

        if (string.Equals(previous.BootInstanceId, current.BootInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"Potvrđen kontinuitet istog pokretanja sistema '{current.BootInstanceId}'.");
            return new BootIdentityAssessment(
                current.BootInstanceId,
                BootContinuityState.Continued,
                reasons,
                sourceRefs,
                interpretationRefId);
        }

        reasons.Add($"Detektovan restart sistema: prelaz sa boot identiteta '{previous.BootInstanceId}' na '{current.BootInstanceId}'.");
        return new BootIdentityAssessment(
            current.BootInstanceId,
            BootContinuityState.Changed,
            reasons,
            sourceRefs,
            interpretationRefId);
    }

    /// <summary>
    /// Deterministically rebuilds clock continuity assessments from persisted samples.
    /// Invariant 112: TIME_CONTINUITY_IS_REBUILDABLE_FROM_PERSISTED_TEMPORAL_EVIDENCE.
    /// </summary>
    public static IReadOnlyList<ClockContinuityAssessment> RebuildHistory(
        IEnumerable<ClockSample> samples,
        TimeContinuityPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        policy ??= TimeContinuityPolicy.Default;

        var list = samples.ToList();
        var result = new List<ClockContinuityAssessment>();

        for (var i = 1; i < list.Count; i++)
        {
            result.Add(EvaluateTransition(list[i - 1], list[i], policy));
        }

        return result.AsReadOnly();
    }
}
