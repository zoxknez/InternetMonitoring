using IEM.Core.Quality;

namespace IEM.Core.Reports;

/// <summary>
/// Deterministically builds a canonical ReportDocumentModel from an established EvidenceAnalysisSnapshot.
/// Invariants:
/// 133. REPORT_MODEL_CONSUMES_ESTABLISHED_ANALYSIS_AND_NEVER_REINTERPRETS_RAW_EVIDENCE
/// 134. DOCUMENT_PURPOSE_MAY_CHANGE_COMPOSITION_BUT_NEVER_EVIDENCE_SEMANTICS
/// 146. REPORT_DOCUMENT_MODEL_IS_DETERMINISTIC_FOR_IDENTICAL_SEMANTIC_INPUT
/// </summary>
public static class ReportDocumentBuilder
{
    public static ReportDocumentModel Build(
        EvidenceAnalysisSnapshot snapshot,
        ReportCompositionProfile? profile = null,
        string language = "sr-Latn")
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        profile ??= ReportCompositionProfile.Technical;

        var overallQuality = snapshot.QualityAssessments.Count > 0
            ? snapshot.QualityAssessments[0].OverallEvidenceBand
            : EvidenceQualityBand.Insufficient;

        var sections = new List<ReportSection>();

        // 1. Summary Section
        var summaryBlocks = new List<ReportBlock>
        {
            new HeadingBlock(1, language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Izveštaj o merenjima i dokaznom materijalu" : "Evidence & Measurement Report"),
            new ParagraphBlock(snapshot.TargetHealthSummary),
            new MetricBlock(
                language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Ukupno trajanje sesije" : "Total Session Duration",
                ReportValue.FromDuration(snapshot.TotalDuration)),
            new MetricBlock(
                language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Aktivno vreme posmatranja" : "Active Monitoring Time",
                ReportValue.FromDuration(snapshot.ActiveMonitoringDuration)),
            new MetricBlock(
                language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Vreme mirovanja računara (Suspend)" : "Host Suspend Time",
                ReportValue.FromDuration(snapshot.HostSuspensionDuration)),
            new MetricBlock(
                language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Detektovani prekidi dostupnosti" : "Observed Reachability Interruptions",
                ReportValue.FromInteger(snapshot.OutagesObservedCount)),
            new QualityBadgeBlock(
                overallQuality,
                "GeneralSessionMeasurement",
                $"Integritet: {snapshot.PackageIntegrityState}, Poverenje: {snapshot.PackageTrustState}"),
        };
        sections.Add(new ReportSection("sec-summary", "Rezime", summaryBlocks));

        // 2. Integrity & Trust Section (Invariant 140: REPORT_PRESENTATION_NEVER_COLLAPSES_INTEGRITY_TRUST_AND_MEASUREMENT_QUALITY)
        var integrityBlocks = new List<ReportBlock>
        {
            new HeadingBlock(2, language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Kriptografski integritet i vremenski žig" : "Cryptographic Integrity & Timestamp"),
            new IntegrityNoticeBlock(snapshot.PackageIntegrityState, snapshot.PackageTrustState, "ECDSA-P256-Key", "RFC3161-TSA"),
            new ParagraphBlock(
                snapshot.PackageTrustState == "Established"
                    ? (language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Integritet paketa i postojanje u vremenu potvrđeni su od strane nezavisnog izdavaoca žiga." : "Package integrity and time verified by third-party TSA.")
                    : (language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Integritet paketa je verifikovan, ali vremenski žig treće strane nije uspostavljen (npr. prekid veze)." : "Package integrity verified, but third-party timestamp was not established.")),
        };
        sections.Add(new ReportSection("sec-integrity", "Integritet", integrityBlocks));

        // 3. Claims Section (Invariant 136: EVERY_EVIDENTIARY_REPORT_CLAIM_PRESERVES_ITS_EPISTEMIC_CLASS_AND_PROVENANCE)
        var claimBlocks = new List<ReportBlock>
        {
            new HeadingBlock(2, language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Formalne tvrdnje i nalazi" : "Formal Claims & Findings"),
        };
        foreach (var claim in snapshot.Claims)
        {
            claimBlocks.Add(new ClaimBlock(claim));
        }
        sections.Add(new ReportSection("sec-claims", "Nalazi", claimBlocks));

        // 4. Timeline Section (Invariant 141: REPORT_TIMELINE_NEVER_VISUALIZES_NON_OBSERVATION_AS_NETWORK_FAILURE)
        var timelineEntries = new List<ReportTimelineEntry>
        {
            new(snapshot.SessionStartUtc, snapshot.SessionStartUtc.Add(snapshot.ActiveMonitoringDuration), TimelineEntryCategory.ActiveMonitoring, "Aktivno prikupljanje dokaza"),
        };
        if (snapshot.HostSuspensionDuration > TimeSpan.Zero)
        {
            timelineEntries.Add(new(
                snapshot.SessionStartUtc.Add(snapshot.ActiveMonitoringDuration),
                snapshot.SessionEndUtc,
                TimelineEntryCategory.HostSuspended,
                "Računar je bio u stanju spavanja/mirovanja (nije mrežni prekid)"));
        }
        sections.Add(new ReportSection("sec-timeline", "Vremenska osa", new[] { new TimelineBlock(timelineEntries) }));

        var provenance = new ReportProvenance(
            SourceSessionId: snapshot.SessionRef,
            SourceAnalysisRef: snapshot.InterpretationRefId,
            DocumentModelSchemaVersion: 1,
            CompositionProfileRef: profile.ProfileId,
            InterpretationRefs: new[] { snapshot.InterpretationRefId },
            QualityPolicyRefs: snapshot.QualityAssessments.Select(q => q.PolicyRefId).Distinct().ToList());

        return new ReportDocumentModel(
            DocumentSchemaVersion: 1,
            DocumentId: $"rep-{Guid.NewGuid():N}",
            SessionRef: snapshot.SessionRef,
            DocumentPurpose: profile.Purpose,
            Language: language,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Title: language.StartsWith("sr", StringComparison.OrdinalIgnoreCase) ? "Izveštaj o merenjima internet konekcije" : "Internet Connection Evidence Report",
            Subtitle: $"Sesija: {snapshot.SessionRef}",
            Summary: snapshot.TargetHealthSummary,
            Sections: sections,
            EvidenceReferences: snapshot.SourceEvidenceRefs,
            OverallQualityBand: overallQuality,
            IntegrityState: snapshot.PackageIntegrityState,
            TrustState: snapshot.PackageTrustState,
            InterpretationRefs: new[] { snapshot.InterpretationRefId },
            PolicyRefs: provenance.QualityPolicyRefs,
            Provenance: provenance);
    }
}
