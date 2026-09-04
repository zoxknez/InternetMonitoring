using System.Collections.Immutable;
using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Quality;
using IEM.Core.Reports;
using IEM.Core.Reports.Renderers;
using IEM.Presentation.Contracts;
using IEM.Presentation.Models;
using IEM.Presentation.Semantics;
using IEM.Presentation.States;

namespace IEM.Presentation;

/// <summary>
/// Deterministic, platform-neutral projection of authoritative runtime and analysis facts.
/// It performs formatting only; network and evidence classifications remain owned by Core.
/// </summary>
public sealed class DefaultPresentationProjector : IPresentationProjector
{
    public ShellPresentationState ProjectShell(ShellProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Snapshot);

        var live = input.LiveSnapshot ?? MonitorSnapshot.Empty;
        var observed = live.SampleCount > 0;
        var monitor = ProjectMonitor(input.Snapshot);
        var evidence = ProjectEvidence(input.Snapshot);
        var caseState = ProjectCase(input.Snapshot, input.CaseWorkspace);
        var speed = ProjectSpeed(new SpeedProjectionInput(input.Snapshot, input.SpeedFacts));
        Severity? severity = observed ? live.CurrentState.SeverityOf() : null;
        var connectivity = observed ? ConnectivityOf(live.CurrentState) : ConnectivityPresentationState.Unknown;
        var tone = severity.HasValue ? ToneOf(severity.Value) : SemanticTone.Unknown;
        var progress = live.Progress;
        var mediumText = MediumText(live.Medium);
        var probes = observed ? ProjectProbes(live) : ImmutableArray<ProbePresentationState>.Empty;
        var availability = observed ? SerbianText.Percent(live.AvailabilityPercent) : "—";
        var upstreamAvailability = observed ? SerbianText.Percent(live.UpstreamAvailabilityPercent) : "—";
        var upstreamDowntime = observed ? SerbianText.Duration(live.UpstreamDowntime) : "—";
        var localDowntime = observed ? SerbianText.Duration(live.LocalDowntime) : "—";
        var unreachable = live.UnreachableTargetShare is { } share
            ? SerbianText.Percent(share, decimals: 1)
            : "nije mereno";

        return new ShellPresentationState(
            IsRunning: input.Interaction.IsRunning,
            Fault: input.Interaction.Fault,
            ActiveTab: input.Interaction.ActiveTab,
            SelectedDuration: input.Interaction.SelectedDuration,
            Durations: input.Interaction.Durations,
            TimelineCapacity: input.Interaction.TimelineCapacity,
            SurvivesClosing: input.HostFacts.SurvivesClosing,
            BackgroundClaimLabel: input.HostFacts.BackgroundClaimLabel,
            BackgroundClaimDetail: input.HostFacts.BackgroundClaimDetail,
            RestartClaimLabel: input.HostFacts.RestartClaimLabel,
            RestartClaimDetail: input.HostFacts.RestartClaimDetail,
            HostDescription: input.HostFacts.HostDescription,
            Verdict: observed
                ? SessionVerdict.Evaluate(live.MonitoredTime, live.UpstreamIncidentCount, live.LocalDowntime)
                : null,
            StateLabel: observed
                ? live.CurrentState.Label()
                : input.Interaction.IsRunning ? "Čekanje na prvi uzorak" : "Spremno",
            StateExplanation: observed
                ? live.CurrentState.Explanation()
                : input.Interaction.IsRunning ? "Sesija je pokrenuta; čeka se prvi ciklus merenja." : "Nadzor nije pokrenut.",
            Connectivity: connectivity,
            CurrentSeverity: severity,
            Tone: tone,
            LatencyText: observed && live.CurrentLatency is { } latency ? $"{latency.TotalMilliseconds:F0} ms" : "—",
            ElapsedText: input.Interaction.IsRunning ? SerbianText.Duration(live.Elapsed) : "—",
            AvailabilityText: availability,
            UpstreamAvailabilityText: upstreamAvailability,
            DowntimeText: upstreamDowntime,
            LocalDowntimeText: localDowntime,
            UnreachableTargetsText: unreachable,
            ShowWirelessWarning: live.Medium == LinkMedium.Wireless,
            StatusPill: input.Interaction.IsRunning ? "NADZOR U TOKU" : "NADZOR NIJE POKRENUT",
            ProgressPercent: progress.GetValueOrDefault() * 100d,
            HasProgress: progress.HasValue,
            MediumText: mediumText,
            Metrics: ProjectMetrics(live, observed, availability, upstreamAvailability, upstreamDowntime, localDowntime, unreachable),
            EndsAtText: EndsAtText(input, live),
            Probes: probes,
            RemainingValue: RemainingText(live, input.Interaction.IsRunning),
            FactsLine: FactsLine(live, input.Interaction.IsRunning, mediumText),
            CaseText: input.CaseWorkspace.CaseJournalText,
            Timeline: input.History.Timeline,
            Latency: input.History.Latency,
            SpeedScheduleAmount: input.Interaction.SpeedScheduleAmount,
            SelectedSpeedScheduleUnit: input.Interaction.SelectedSpeedScheduleUnit,
            SpeedScheduleUnits: input.Interaction.SpeedScheduleUnits,
            ContractedRateText: input.Interaction.ContractedRateText,
            SpeedStatus: input.Interaction.SpeedStatus,
            SpeedBusy: input.Interaction.SpeedBusy,
            IsUpdateBannerVisible: input.Update.IsUpdateBannerVisible,
            UpdateVersionText: input.Update.UpdateVersionText,
            UpdateSummaryText: input.Update.UpdateSummaryText,
            UpdateReleaseNotesUrl: input.Update.UpdateReleaseNotesUrl,
            UpdateDownloadUrl: input.Update.UpdateDownloadUrl,
            Monitor: monitor,
            Evidence: evidence,
            Case: caseState,
            Speed: speed);
    }

    public MonitorPresentationState ProjectMonitor(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Analysis is not { } analysis)
        {
            return MonitorPresentationState.Initial with
            {
                TargetHealthSummary = snapshot.RuntimeState == SessionRuntimeState.Monitoring
                    ? "Čekanje na prve rezultate merenja…"
                    : MonitorPresentationState.Initial.TargetHealthSummary,
                Tone = snapshot.RuntimeState == SessionRuntimeState.Monitoring
                    ? SemanticTone.Info
                    : SemanticTone.Unknown,
            };
        }

        var quality = analysis.QualityAssessments.FirstOrDefault()?.OverallEvidenceBand;
        var band = quality.HasValue ? QualityOf(quality.Value) : QualityPresentationBand.Unknown;
        var qualityText = QualityLabel(band);
        if (snapshot.RuntimeState == SessionRuntimeState.Monitoring)
        {
            qualityText += " (privremeno)";
        }

        var timeline = ImmutableArray.CreateBuilder<MonitorTimelinePresentationItem>();
        if (analysis.ActiveMonitoringDuration > TimeSpan.Zero)
        {
            timeline.Add(new MonitorTimelinePresentationItem(
                analysis.SessionStartUtc,
                analysis.SessionStartUtc + analysis.ActiveMonitoringDuration,
                TimelinePresentationCategory.ActiveMonitoring,
                "Aktivni nadzor",
                "Aktivno osmatranje mrežnih meta."));
        }

        if (analysis.HostSuspensionDuration > TimeSpan.Zero)
        {
            timeline.Add(new MonitorTimelinePresentationItem(
                analysis.SessionEndUtc - analysis.HostSuspensionDuration,
                analysis.SessionEndUtc,
                TimelinePresentationCategory.HostSuspended,
                "Pauza računara",
                "Računar nije osmatrao mrežu; ovaj period nije prikazan kao prekid veze."));
        }

        return new MonitorPresentationState(
            TargetHealthSummary: analysis.TargetHealthSummary,
            ProbeHealthSummary: analysis.ProbeHealthSummary,
            QualityBand: band,
            QualityBandText: qualityText,
            TotalDuration: SerbianText.Duration(analysis.TotalDuration),
            ActiveDuration: SerbianText.Duration(analysis.ActiveMonitoringDuration),
            SuspendDuration: SerbianText.Duration(analysis.HostSuspensionDuration),
            InterruptionsCount: analysis.OutagesObservedCount,
            Tone: ToneOf(band),
            TimelineItems: timeline.ToImmutable());
    }

    public EvidencePresentationState ProjectEvidence(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Analysis is not { } analysis)
        {
            return EvidencePresentationState.Initial;
        }

        var firstAssessment = analysis.QualityAssessments.FirstOrDefault();
        var overall = firstAssessment is null
            ? QualityPresentationBand.Unknown
            : QualityOf(firstAssessment.OverallEvidenceBand);
        var integrity = IntegrityOf(analysis.PackageIntegrityState);
        var trust = TrustOf(analysis.PackageTrustState);
        var claims = analysis.Claims.Select(claim => ProjectClaim(claim, analysis)).ToImmutableArray();

        return new EvidencePresentationState(
            OverallQualityBand: overall,
            OverallQualityText: QualityLabel(overall) +
                (snapshot.RuntimeState == SessionRuntimeState.Monitoring ? " (privremeno)" : string.Empty),
            IntegrityState: integrity,
            IntegrityLabel: IntegrityLabel(integrity),
            TrustState: trust,
            TrustLabel: TrustLabel(trust),
            PackageVerificationSummary: PackageSummary(integrity, trust),
            Tone: EvidenceTone(integrity, trust, overall),
            Claims: claims);
    }

    public SpeedPresentationState ProjectSpeed(SpeedProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Execution);

        return input.Execution switch
        {
            SpeedExecutionFacts.NotRun => new SpeedPresentationState.NotRun(),
            SpeedExecutionFacts.Executing value => new SpeedPresentationState.Executing(
                value.MeasurementIntent,
                value.RequestedInterface),
            SpeedExecutionFacts.Refused value => new SpeedPresentationState.Refused(
                value.MeasurementIntent,
                value.RequestedInterface,
                value.RefusalReason),
            SpeedExecutionFacts.Succeeded value => new SpeedPresentationState.Succeeded(
                value.MeasurementIntent,
                value.RequestedInterface,
                value.ObservedPath,
                value.PathAgreement,
                value.TunnelIndication,
                value.DownloadThroughputMbps,
                value.UploadThroughputMbps),
            _ => new SpeedPresentationState.NotRun("Stanje merenja nije poznato."),
        };
    }

    public CasePresentationState ProjectCase(
        PresentationSnapshot snapshot,
        Contracts.CaseWorkspaceState workspace)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(workspace);

        var preview = snapshot.CanonicalReport is not { } report
            ? CasePresentationState.Initial.PreviewText
            : workspace.SelectedProfile.Purpose == DocumentPurpose.RegulatorySubmission
                ? new RatelRegulatoryComposer().RenderToString(report, SerbianText.Culture)
                : new ComplaintNarrativeComposer().RenderToString(report, SerbianText.Culture);

        return new CasePresentationState(
            workspace.OperatorName,
            workspace.ContractNumber,
            workspace.UserContact,
            workspace.SelectedProfile,
            preview,
            workspace.UserStatements);
    }

    private static ClaimPresentationItem ProjectClaim(ReportClaim claim, EvidenceAnalysisSnapshot analysis)
    {
        var assessment = analysis.QualityAssessments.FirstOrDefault(item =>
            string.Equals(item.AssessmentId, claim.QualityAssessmentRef, StringComparison.Ordinal) ||
            string.Equals(item.Subject.ClaimRef, claim.ClaimId, StringComparison.Ordinal));
        var quality = assessment is not null
            ? QualityOf(assessment.OverallEvidenceBand)
            : Enum.TryParse<QualityPresentationBand>(claim.QualityAssessmentRef, true, out var parsed)
                ? parsed
                : QualityPresentationBand.Unknown;

        return new ClaimPresentationItem(
            claim.ClaimId,
            claim.StatementKey,
            claim.EpistemicClass,
            claim.EpistemicClass switch
            {
                EpistemicClass.Fact => "Činjenica",
                EpistemicClass.Inference => "Izvođenje",
                _ => "Procena",
            },
            claim.StructuredValue?.Format(SerbianText.Culture) ?? "Nije utvrđeno (Unknown)",
            claim.SupportState,
            quality,
            claim.QualityAssessmentRef);
    }

    private static ImmutableArray<MetricPresentationItem> ProjectMetrics(
        MonitorSnapshot live,
        bool observed,
        string availability,
        string upstreamAvailability,
        string upstreamDowntime,
        string localDowntime,
        string unreachable) =>
        [
            new("Dostupnost", availability, "od nadziranog vremena"),
            new("Bez lokalnih kvarova", upstreamAvailability, "bez vaše opreme"),
            new("Prekida iza rutera", observed ? live.UpstreamIncidentCount.ToString(SerbianText.Culture) : "—",
                observed ? $"ukupno {live.IncidentCount.ToString(SerbianText.Culture)}" : "nije mereno"),
            new("Nedostupnost iza rutera", upstreamDowntime, $"lokalno {localDowntime}"),
            new("Mete bez odgovora", unreachable, "poslednji uzorak"),
            new("Nenadzirano", observed ? SerbianText.Duration(live.GapTime) : "—", "spavanje ili restart"),
        ];

    private static ImmutableArray<ProbePresentationState> ProjectProbes(MonitorSnapshot live) =>
        [
            Probe("Ruter", live.Gateway),
            Probe("Ping", live.ExternalIcmp),
            Probe("TCP", live.ExternalTcp),
            Probe("DNS", live.Dns),
            Probe("HTTP", live.Http),
        ];

    private static ProbePresentationState Probe(string name, ProbeTally tally)
    {
        var tone = tally.IsSilent ? SemanticTone.Neutral
            : tally.AllSucceeded ? SemanticTone.Good
            : tally.AllFailed ? SemanticTone.Bad
            : SemanticTone.Warning;
        var detail = tally.IsSilent
            ? "nije provereno"
            : $"{tally.Succeeded.ToString(SerbianText.Culture)}/{tally.Attempted.ToString(SerbianText.Culture)}";
        return new ProbePresentationState(name, detail, tone);
    }

    private static string EndsAtText(ShellProjectionInput input, MonitorSnapshot live)
    {
        var planned = live.PlannedDuration ?? input.Interaction.SelectedDuration.Duration;
        if (planned == Timeout.InfiniteTimeSpan)
        {
            return "Test nema rok. Traje dok ga ne zaustavite.";
        }

        var started = live.StartedUtc ?? input.Snapshot.CapturedAtUtc;
        return $"Test se završava {SerbianText.DateTime(started + planned)}.";
    }

    private static string RemainingText(MonitorSnapshot live, bool isRunning)
    {
        if (!isRunning)
        {
            return "—";
        }

        if (live.Remaining is { } remaining)
        {
            return SerbianText.Duration(remaining);
        }

        return live.PlannedDuration is null || live.PlannedDuration == Timeout.InfiniteTimeSpan
            ? "bez roka"
            : "završavanje";
    }

    private static string FactsLine(MonitorSnapshot live, bool running, string mediumText) => running
        ? string.Join("   ·   ",
        [
            live.InterfaceName ?? "nepoznat adapter",
            mediumText,
            live.GatewayAddress is { } gateway ? $"ruter {gateway}" : "ruter nepoznat",
            $"{live.SampleCount.ToString("N0", SerbianText.Culture)} uzoraka",
            live.SessionId ?? "—",
        ])
        : string.Empty;

    private static ConnectivityPresentationState ConnectivityOf(NetworkState state) => state.SeverityOf() switch
    {
        Severity.Ok => ConnectivityPresentationState.Online,
        Severity.Degraded => ConnectivityPresentationState.Degraded,
        Severity.Outage => ConnectivityPresentationState.Outage,
        _ => ConnectivityPresentationState.Unknown,
    };

    private static SemanticTone ToneOf(Severity severity) => severity switch
    {
        Severity.Ok => SemanticTone.Good,
        Severity.Info => SemanticTone.Info,
        Severity.Degraded => SemanticTone.Warning,
        Severity.Outage => SemanticTone.Bad,
        _ => SemanticTone.Unknown,
    };

    private static SemanticTone ToneOf(QualityPresentationBand band) => band switch
    {
        QualityPresentationBand.Strong => SemanticTone.Good,
        QualityPresentationBand.Moderate => SemanticTone.Info,
        QualityPresentationBand.Limited => SemanticTone.Warning,
        QualityPresentationBand.Insufficient => SemanticTone.Bad,
        _ => SemanticTone.Unknown,
    };

    private static QualityPresentationBand QualityOf(EvidenceQualityBand band) => band switch
    {
        EvidenceQualityBand.Strong => QualityPresentationBand.Strong,
        EvidenceQualityBand.Moderate => QualityPresentationBand.Moderate,
        EvidenceQualityBand.Limited => QualityPresentationBand.Limited,
        EvidenceQualityBand.Insufficient => QualityPresentationBand.Insufficient,
        _ => QualityPresentationBand.Unknown,
    };

    private static string QualityLabel(QualityPresentationBand band) => band switch
    {
        QualityPresentationBand.Strong => "Jak (Strong)",
        QualityPresentationBand.Moderate => "Umeren (Moderate)",
        QualityPresentationBand.Limited => "Ograničen (Limited)",
        QualityPresentationBand.Insufficient => "Nedovoljan (Insufficient)",
        _ => "Nepoznato",
    };

    private static IntegrityPresentationState IntegrityOf(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "verified" or "valid" => IntegrityPresentationState.Verified,
        "incomplete" or "notfinalized" => IntegrityPresentationState.Incomplete,
        "invalid" or "compromised" => IntegrityPresentationState.Invalid,
        _ => IntegrityPresentationState.Unknown,
    };

    private static string IntegrityLabel(IntegrityPresentationState state) => state switch
    {
        IntegrityPresentationState.Verified => "Potvrđen (Verified)",
        IntegrityPresentationState.Incomplete => "Nedovršen (Incomplete)",
        IntegrityPresentationState.Invalid => "Neispravan (Invalid)",
        _ => "Nepoznato",
    };

    private static TrustPresentationState TrustOf(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "established" or "trusted" => TrustPresentationState.Established,
        "notestablished" or "not_established" => TrustPresentationState.NotEstablished,
        "notapplicable" or "not_applicable" => TrustPresentationState.NotApplicable,
        _ => TrustPresentationState.Unknown,
    };

    private static string TrustLabel(TrustPresentationState state) => state switch
    {
        TrustPresentationState.Established => "Uspostavljeno (Established)",
        TrustPresentationState.NotEstablished => "Nije uspostavljeno (NotEstablished)",
        TrustPresentationState.NotApplicable => "Nije primenljivo",
        _ => "Nepoznato",
    };

    private static string PackageSummary(IntegrityPresentationState integrity, TrustPresentationState trust) =>
        (integrity, trust) switch
        {
            (IntegrityPresentationState.Verified, TrustPresentationState.Established) =>
                "Integritet paketa i spoljašnje poverenje su potvrđeni.",
            (IntegrityPresentationState.Verified, TrustPresentationState.NotEstablished) =>
                "Integritet paketa je potvrđen; spoljašnje poverenje vremenskog pečata nije uspostavljeno.",
            (IntegrityPresentationState.Invalid, _) =>
                "Integritet dokaznog paketa nije validan.",
            (IntegrityPresentationState.Incomplete, _) =>
                "Paket još nije zatvoren i zato nema konačnu proveru.",
            _ => "Stanje dokaznog paketa nije utvrđeno.",
        };

    private static SemanticTone EvidenceTone(
        IntegrityPresentationState integrity,
        TrustPresentationState trust,
        QualityPresentationBand quality)
    {
        if (integrity == IntegrityPresentationState.Invalid)
        {
            return SemanticTone.Bad;
        }

        if (integrity == IntegrityPresentationState.Incomplete ||
            trust == TrustPresentationState.NotEstablished ||
            quality == QualityPresentationBand.Limited)
        {
            return SemanticTone.Warning;
        }

        return integrity == IntegrityPresentationState.Verified &&
               trust == TrustPresentationState.Established &&
               quality == QualityPresentationBand.Strong
            ? SemanticTone.Good
            : ToneOf(quality);
    }

    private static string MediumText(LinkMedium medium) => medium switch
    {
        LinkMedium.Ethernet => "žičana (Ethernet)",
        LinkMedium.Wireless => "bežična (Wi‑Fi)",
        _ => "nepoznato",
    };
}
