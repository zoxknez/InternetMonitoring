namespace IEM.Legal;

/// <summary>
/// Every published body of rules, and how a case is matched against them.
/// <para>
/// The registry is append-only. When the law moves, a new ruleset version is added; the
/// existing ones are never edited, because a case decided under one is entitled to keep the
/// meaning it had. A test hashes each published version against a recorded digest, so an edit
/// fails the build instead of quietly rewriting the past.
/// </para>
/// </summary>
public static class LegalRegistry
{
    private static readonly DateOnly Checked = new(2026, 8, 17);

    /// <summary>The day the act adopted under article 140 began to apply.</summary>
    public static readonly DateOnly CurrentRegimeFrom = new(2025, 1, 1);

    /// <summary>
    /// The transitional regime. The old law's article 113 stayed in force only until the act
    /// under article 140 of the new one was adopted; it was, and this stopped applying.
    /// </summary>
    public static readonly LegalRuleset Legacy = new()
    {
        Id = "RS-ZEK-LEGACY",
        Version = "2026.08.17.1",
        Regime = LegalRegime.LegacyPre2025,
        AppliesTo = CurrentRegimeFrom.AddDays(-1),
        VerifiedAt = Checked,
        Rules =
        [
            new LegalRule
            {
                RuleId = "legacy/complaint-due/quality",
                Step = ComplaintStep.ComplaintDue,
                Value = 30,
                Anchor = LegalAnchor.ServiceUnavailableDate,
                FallbackAnchor = LegalAnchor.ServiceProvidedDate,
                Condition = new RuleCondition(
                    ComplaintKind: ComplaintKind.ServiceQuality,
                    Description: "prigovor na kvalitet usluge"),
                Citations = [LegalSources.ZekLegacy113],
            },
            new LegalRule
            {
                RuleId = "legacy/complaint-due/billing",
                Step = ComplaintStep.ComplaintDue,
                Value = 30,
                Anchor = LegalAnchor.InvoiceDueDate,
                Condition = new RuleCondition(
                    ComplaintKind: ComplaintKind.BillingAmount,
                    Description: "prigovor na iznos računa"),
                Citations = [LegalSources.ZekLegacy113],
            },
            new LegalRule
            {
                RuleId = "legacy/provider-response",
                Step = ComplaintStep.OperatorResponseDue,
                Value = 15,
                Anchor = LegalAnchor.ProviderComplaintFiled,
                Condition = new RuleCondition(Description: "svi korisnici"),
                Citations = [LegalSources.ZekLegacy113],
            },
            new LegalRule
            {
                RuleId = "legacy/regulator-dispute",
                Step = ComplaintStep.RegulatorDisputeDue,
                Value = 15,
                Anchor = LegalAnchor.ProviderResponseReceived,
                FallbackAnchor = LegalAnchor.ProviderResponseDue,
                TransitionPolicy = TransitionPolicy.FinishUnderStartedRules,
                Condition = new RuleCondition(Description: "svi korisnici"),
                Citations = [LegalSources.ZekLegacy113],
            },
        ],
    };

    /// <summary>The regime a case opened today runs on.</summary>
    public static readonly LegalRuleset Current = new()
    {
        Id = "RS-ZEK-2025",
        Version = "2026.08.17.1",
        Regime = LegalRegime.Current,
        AppliesFrom = CurrentRegimeFrom,
        VerifiedAt = Checked,
        Rules =
        [
            new LegalRule
            {
                RuleId = "current/complaint-due/quality",
                Step = ComplaintStep.ComplaintDue,
                Value = 30,
                Anchor = LegalAnchor.ServiceUnavailableDate,
                FallbackAnchor = LegalAnchor.ServiceProvidedDate,
                Condition = new RuleCondition(
                    ComplaintKind: ComplaintKind.ServiceQuality,
                    Description: "prigovor na kvalitet usluge, od dana nemogućnosti korišćenja"),
                Citations = [LegalSources.Zek139],
            },
            new LegalRule
            {
                RuleId = "current/complaint-due/billing",
                Step = ComplaintStep.ComplaintDue,
                Value = 30,
                Anchor = LegalAnchor.InvoiceDueDate,
                Condition = new RuleCondition(
                    ComplaintKind: ComplaintKind.BillingAmount,
                    Description: "prigovor na iznos računa, od dana dospeća računa"),
                Citations = [LegalSources.Zek139],
            },

            // Roaming, international traffic and value-added services first: the period is
            // the same whoever the subscriber is, so it has to outrank the consumer rule.
            new LegalRule
            {
                RuleId = "current/provider-response/roaming",
                Step = ComplaintStep.OperatorResponseDue,
                Value = 30,
                Anchor = LegalAnchor.ProviderComplaintFiled,
                Condition = new RuleCondition(
                    ServiceKind: ServiceKind.RoamingInternationalVas,
                    Description: "roming, međunarodni saobraćaj i usluge sa dodatom vrednošću"),
                Citations = [LegalSources.Zek139],
            },
            new LegalRule
            {
                RuleId = "current/provider-response/consumer",
                Step = ComplaintStep.OperatorResponseDue,
                Value = 8,
                Anchor = LegalAnchor.ProviderComplaintFiled,
                Condition = new RuleCondition(
                    CustomerType: CustomerType.Consumer,
                    Description: "potrošač, rok po propisu o zaštiti potrošača"),

                // Both consumer acts prescribe eight days, so this is one rule - but a case
                // from July 2026 has to cite the act that governed it in July 2026.
                Citations = [LegalSources.Zek139, LegalSources.Zzp88_2021, LegalSources.Zzp35_2026],
            },
            new LegalRule
            {
                RuleId = "current/provider-response/non-consumer",
                Step = ComplaintStep.OperatorResponseDue,
                Value = 30,
                Anchor = LegalAnchor.ProviderComplaintFiled,
                Condition = new RuleCondition(
                    CustomerType: CustomerType.NonConsumer,
                    Description: "korisnik koji nije potrošač, rok za rešavanje prigovora"),
                Citations = [LegalSources.Zek139],
            },

            new LegalRule
            {
                RuleId = "current/regulator-dispute",
                Step = ComplaintStep.RegulatorDisputeDue,
                Value = 60,
                Anchor = LegalAnchor.ProviderResponseReceived,
                FallbackAnchor = LegalAnchor.ProviderResponseDue,
                TransitionPolicy = TransitionPolicy.FinishUnderStartedRules,
                Condition = new RuleCondition(Description: "svi korisnici"),
                Citations = [LegalSources.Zek139, LegalSources.Pravilnik58_2024],
            },
            new LegalRule
            {
                RuleId = "current/regulator-decision",
                Step = ComplaintStep.RegulatorDecisionTarget,
                Value = 90,
                Anchor = LegalAnchor.RegulatorProceedingFiled,
                TransitionPolicy = TransitionPolicy.FinishUnderStartedRules,
                Condition = new RuleCondition(
                    Description: "rok za odluku Regulatora, produživ u složenim slučajevima"),
                Citations = [LegalSources.Zek140, LegalSources.Pravilnik58_2024],
            },
        ],
    };

    public static IReadOnlyList<LegalRuleset> All { get; } = [Legacy, Current];

    /// <summary>Which regime was in force on a given day.</summary>
    public static LegalRuleset On(DateOnly date) => date >= CurrentRegimeFrom ? Current : Legacy;

    /// <summary>
    /// Works out which period applied to each step of this case, and from what.
    /// <para>
    /// Step by step rather than case by case. The law counts each period from its own event,
    /// and the transitional rule speaks about a proceeding that has been started - so a
    /// complaint filed with the operator in November 2024 does not decide the period for a
    /// request that reaches the Regulator in January 2025.
    /// </para>
    /// </summary>
    public static ResolvedLegalContext Resolve(CaseFacts facts, DateOnly resolvedAt)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var applied = new List<AppliedRule>();
        var derived = new Dictionary<LegalAnchor, AnchoredDate>();

        foreach (var step in Steps)
        {
            var rule = Resolve(step, facts, derived);

            if (rule is null)
            {
                continue;
            }

            applied.Add(rule);

            // The operator's answer was owed on this day, so the period to reach the
            // Regulator can run from it when no answer ever came. It inherits the provenance
            // of the filing date it was computed from.
            if (step == ComplaintStep.OperatorResponseDue &&
                rule.Due is { } due &&
                rule.AnchoredOn is { } from)
            {
                derived[LegalAnchor.ProviderResponseDue] =
                    new AnchoredDate(due, from.Origin, "rok za odgovor izračunat iz datuma podnošenja");
            }
        }

        // The identity of the ruleset the case as a whole ran on: whichever governed the
        // furthest step that could be settled, falling back to the regime in force today.
        // A case can legitimately straddle - the complaint under the old rules, the dispute
        // under the new - and it is the later one that says where the case now stands.
        var governing = On(resolvedAt);

        foreach (var rule in applied)
        {
            if (rule.RuleId is { } id && All.FirstOrDefault(set => set.Rules.Any(r => r.RuleId == id)) is { } owner)
            {
                governing = owner;
            }
        }

        return new ResolvedLegalContext
        {
            Ruleset = governing.Ref,
            Regime = governing.Regime,
            CustomerType = facts.CustomerType,
            ServiceKind = facts.ServiceKind,
            ComplaintKind = facts.ComplaintKind,
            AppliedRules = applied,
            ResolvedAt = resolvedAt,
        };
    }

    /// <param name="within">
    /// When given, the only body of rules this step may be settled under - the one the case
    /// was already decided by. Without it the regime is chosen from the calendar, which is
    /// right for a new case and wrong for one that already has an answer.
    /// </param>
    /// <summary>
    /// Settles what has become settleable since a case was last looked at, and changes
    /// nothing else.
    /// <para>
    /// A period that has already been resolved is carried over exactly as it was - not
    /// recomputed, not re-cited, not re-dated. A newer registry is not a new fact about the
    /// case, and until 2.7.1 any write at all re-resolved the whole thing, so recording the
    /// operator's answer under a corrected registry would quietly restate every deadline that
    /// had been computed months earlier.
    /// </para>
    /// <para>
    /// New facts are answered only within the body of rules the case already ran on. Reaching
    /// for today's registry because the frozen one has no answer would be the same defect
    /// wearing a better disguise: half the case under one set of rules and half under another,
    /// with a single identifier claiming otherwise.
    /// </para>
    /// </summary>
    public static ResolvedLegalContext Extend(
        CaseFacts facts,
        ResolvedLegalContext existing,
        DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(existing);

        var frozen = Find(existing.Ruleset);
        var applied = new List<AppliedRule>();
        var derived = new Dictionary<LegalAnchor, AnchoredDate>();

        foreach (var step in Steps)
        {
            var previous = existing.For(step);

            // Nothing settled yet: a new fact may settle it, within the frozen rules.
            if (previous is null || previous.Resolution == ResolutionState.Unresolved)
            {
                if (frozen is null)
                {
                    applied.Add(previous ?? Stranded(step, existing));
                    continue;
                }

                if (Resolve(step, facts, derived, frozen) is { } settled)
                {
                    applied.Add(settled);
                    Carry(step, settled);
                }
                else if (previous is not null)
                {
                    applied.Add(previous);
                }

                continue;
            }

            // Settled from the anchor the law names first, or already flagged: it stands. The
            // only thing that can happen to it is that one of the dates behind it changes,
            // and that is stated rather than applied.
            if (previous.Resolution != ResolutionState.Provisional)
            {
                applied.Add(previous with { Conflict = ConflictOn(previous, facts, derived) });
                Carry(step, previous);
                continue;
            }

            // Settled from a fallback, because the event the law counts from had not happened
            // yet. If it has now, the same frozen rules give the real date, and that becomes
            // the answer - the provisional one is kept beside it rather than thrown away.
            var upgraded = frozen is null ? null : Resolve(step, facts, derived, frozen);

            if (upgraded is { Resolution: ResolutionState.Resolved } &&
                upgraded.Due != previous.Due)
            {
                applied.Add(upgraded with
                {
                    Superseded = new PreviousResolution(
                        previous.Anchor,
                        previous.AnchoredOn?.Date,
                        previous.Due,
                        ResolutionChange.PrimaryAnchorBecameAvailable),
                });

                Carry(step, upgraded);
                continue;
            }

            // Still provisional. The fallback date itself changing is a changed fact like any
            // other, so it is flagged rather than quietly recomputed.
            applied.Add(previous with { Conflict = ConflictOn(previous, facts, derived) });
            Carry(step, previous);
        }

        return existing with
        {
            AppliedRules = applied,
            ResolvedAt = asOf,
        };

        void Carry(ComplaintStep step, AppliedRule rule)
        {
            if (step == ComplaintStep.OperatorResponseDue &&
                rule.Due is { } due &&
                rule.AnchoredOn is { } from)
            {
                derived[LegalAnchor.ProviderResponseDue] =
                    new AnchoredDate(due, from.Origin, "rok za odgovor izračunat iz datuma podnošenja");
            }
        }
    }

    /// <summary>
    /// A step that cannot be settled because the rules the case ran on are no longer
    /// published. Reaching for today's would put half the case under one regime and half
    /// under another, with one identifier claiming otherwise.
    /// </summary>
    private static AppliedRule Stranded(ComplaintStep step, ResolvedLegalContext existing) => new()
    {
        Step = step,
        State = LegalContextState.Unresolved,
        Resolution = ResolutionState.Unresolved,
        Impediment =
            $"pravila pod kojima je predmet razrešen ({existing.Ruleset}) nisu više u registru, " +
            "pa se nov rok ne može izvesti na isti način",
    };

    /// <summary>The published rules with exactly this identity, or null when there are none.</summary>
    public static LegalRuleset? Find(LegalRulesetRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return All.FirstOrDefault(set =>
            set.Id == reference.Id &&
            set.Version == reference.Version &&
            set.ContentHash == reference.ContentHash);
    }

    /// <summary>
    /// Whether the date an already-settled period was counted from has since changed.
    /// <para>
    /// Someone correcting the day their service failed is doing something legitimate, and the
    /// deadlines really do move with it - but not silently, and not by this path. The stated
    /// disagreement is the honest interim answer until a case can carry a history of
    /// resolutions.
    /// </para>
    /// </summary>
    private static string? ConflictOn(
        AppliedRule resolved,
        CaseFacts facts,
        IReadOnlyDictionary<LegalAnchor, AnchoredDate> derived)
    {
        if (resolved.Anchor is not { } anchor || resolved.AnchoredOn is not { } was)
        {
            return null;
        }

        var now = facts.On(anchor) ?? (derived.TryGetValue(anchor, out var value) ? value : null);

        // No advice about starting a new case. Whether one is needed is a legal judgement, and
        // this program has neither the facts nor the standing to make it - it can only say
        // what it noticed. The date is formatted without a trailing period of its own: the
        // Serbian format already ends with one, and two in a row read as a typing mistake.
        return now is not null && now.Date != was.Date
            ? $"Datum od kog je ovaj rok računat promenjen je sa {was.Date:dd.MM.yyyy.} na " +
              $"{now.Date:dd.MM.yyyy.} Sačuvani rok nije automatski promenjen; potrebna je " +
              "provera pravnog konteksta predmeta."
            : null;
    }

    /// <summary>In the order the procedure runs, because later steps count from earlier ones.</summary>
    private static readonly ComplaintStep[] Steps =
    [
        ComplaintStep.ComplaintDue,
        ComplaintStep.OperatorResponseDue,
        ComplaintStep.RegulatorDisputeDue,
        ComplaintStep.RegulatorDecisionTarget,
    ];

    private static AppliedRule? Resolve(
        ComplaintStep step,
        CaseFacts facts,
        IReadOnlyDictionary<LegalAnchor, AnchoredDate> derived,
        LegalRuleset? within = null)
    {
        // Both regimes anchor a given step on the same event, so either rule can say what to
        // look for before the regime itself has been settled.
        var reference =
            within?.RuleFor(step, facts.CustomerType, facts.ServiceKind, facts.ComplaintKind) ??
            Current.RuleFor(step, facts.CustomerType, facts.ServiceKind, facts.ComplaintKind) ??
            Legacy.RuleFor(step, facts.CustomerType, facts.ServiceKind, facts.ComplaintKind);

        if (reference is null)
        {
            return null;
        }

        // Which anchor actually produced a date, not which one the rule names first. Recording
        // the primary one regardless is what made a period counted from the day an answer was
        // owed claim to have been counted from the day it arrived.
        var usedAnchor = reference.Anchor;
        var anchored = Lookup(reference.Anchor);

        if (anchored is null && reference.FallbackAnchor is { } fallback && Lookup(fallback) is { } byFallback)
        {
            usedAnchor = fallback;
            anchored = byFallback;
        }

        if (anchored is null)
        {
            // A step of the procedure that has not been taken yet is not an open question -
            // there is simply no deadline to state until the complaint is filed, or the
            // answer arrives, or the request reaches the Regulator. Only a missing fact about
            // the world leaves a period genuinely unsettled.
            return IsProcedural(reference.Anchor)
                ? null
                : new AppliedRule
                {
                    Step = step,
                    State = LegalContextState.Unresolved,
                    Resolution = ResolutionState.Unresolved,
                    Anchor = reference.Anchor,
                    Impediment = $"nije poznat datum: {Describe(reference.Anchor)}",
                };
        }

        var governing = within ?? Governing(reference, anchored, facts);

        if (governing is null)
        {
            return new AppliedRule
            {
                Step = step,
                State = LegalContextState.Unresolved,
                Resolution = ResolutionState.Unresolved,
                Anchor = usedAnchor,
                AnchoredOn = anchored,
                Impediment =
                    "rok pada preko granice između starog i važećeg režima, a postupak pred " +
                    "Regulatorom nije pokrenut, pa se ne može utvrditi koji rok važi",
            };
        }

        var rule = governing.RuleFor(step, facts.CustomerType, facts.ServiceKind, facts.ComplaintKind);

        if (rule is null)
        {
            return new AppliedRule
            {
                Step = step,
                State = LegalContextState.Unresolved,
                Resolution = ResolutionState.Unresolved,
                Anchor = usedAnchor,
                AnchoredOn = anchored,
                Impediment = "režim koji važi za ovaj predmet ne poznaje ovaj rok",
            };
        }

        // A date whose provenance was never established is not a verified fact. It is used -
        // refusing to compute at all would help nobody - but the result says it was
        // reconstructed, so nobody relies on it as though somebody had confirmed it.
        var state = anchored.Origin is FactOrigin.UserProvided or FactOrigin.DerivedFromSession
            ? LegalContextState.Resolved
            : LegalContextState.InferredFromRecordedDates;

        return new AppliedRule
        {
            Step = step,
            State = state,
            Resolution = usedAnchor == rule.Anchor
                ? ResolutionState.Resolved
                : ResolutionState.Provisional,
            RuleId = rule.RuleId,
            Value = rule.Value,
            Anchor = usedAnchor,
            AnchoredOn = anchored,
            Due = rule.DueFrom(anchored.Date),
            Citations = Citations(rule, governing, step, facts, anchored),
        };

        AnchoredDate? Lookup(LegalAnchor anchor) =>
            facts.On(anchor) ?? (derived.TryGetValue(anchor, out var value) ? value : null);
    }

    /// <summary>
    /// Which regime governs this period, or null when that is genuinely open.
    /// </summary>
    private static LegalRuleset? Governing(LegalRule reference, AnchoredDate anchored, CaseFacts facts)
    {
        if (reference.TransitionPolicy != TransitionPolicy.FinishUnderStartedRules)
        {
            return On(anchored.Date);
        }

        // A proceeding already before the Regulator finishes under the rules it started
        // under, and the day it started is the day the request was received.
        if (facts.RegulatorProceedingFiled is { } filed)
        {
            return On(filed.Date);
        }

        var atAnchor = On(anchored.Date);
        var rule = atAnchor.RuleFor(
            reference.Step, facts.CustomerType, facts.ServiceKind, facts.ComplaintKind);

        // Nothing has been started and the period crosses the boundary: which rules would see
        // it through depends on when the request is filed, which has not happened yet.
        return rule is not null && On(rule.DueFrom(anchored.Date)) != atAnchor ? null : atAnchor;
    }

    /// <summary>
    /// The sources for this period, filtered to those in force when it started.
    /// <para>
    /// Both regimes are cited only where the period runs across the boundary between them and
    /// they prescribe the same thing from the same event - the thirty days for a complaint do
    /// not change. There is nothing to choose between them, so the output states the value
    /// with both sources and makes no claim about which regime applied. A period that lies
    /// entirely inside one regime cites that one alone; naming a repealed article beside a
    /// deadline from 2026 would be noise at best and misleading at worst.
    /// </para>
    /// </summary>
    private static IReadOnlyList<LegalCitation> Citations(
        LegalRule rule,
        LegalRuleset governing,
        ComplaintStep step,
        CaseFacts facts,
        AnchoredDate anchored)
    {
        var citations = rule.CitationsOn(anchored.Date).ToList();

        // Settled by an event rather than by the calendar - the proceeding was started under
        // one regime and finishes under it - so there is no ambiguity to report.
        if (rule.TransitionPolicy != TransitionPolicy.ApplyRuleInForceAtAnchor ||
            On(rule.DueFrom(anchored.Date)) == governing)
        {
            return citations;
        }

        var other = All.FirstOrDefault(set => set != governing)
            ?.RuleFor(step, facts.CustomerType, facts.ServiceKind, facts.ComplaintKind);

        if (other is not null && other.Value == rule.Value && other.Anchor == rule.Anchor)
        {
            citations.AddRange(other.Citations.Where(c => !citations.Contains(c)));
        }

        return citations;
    }

    /// <summary>
    /// Whether this anchor is a step of the procedure rather than a fact about the service.
    /// The procedure's own steps happen in order; the facts either are known or are not.
    /// </summary>
    private static bool IsProcedural(LegalAnchor anchor) => anchor
        is LegalAnchor.ProviderComplaintFiled
        or LegalAnchor.ProviderResponseReceived
        or LegalAnchor.ProviderResponseDue
        or LegalAnchor.RegulatorProceedingFiled;

    private static string Describe(LegalAnchor anchor) => anchor switch
    {
        LegalAnchor.InvoiceDueDate => "dospeće spornog računa",
        LegalAnchor.ServiceProvidedDate => "dan pružanja usluge",
        LegalAnchor.ServiceUnavailableDate => "dan nemogućnosti korišćenja usluge",
        LegalAnchor.ProviderComplaintFiled => "dan podnošenja prigovora operateru",
        LegalAnchor.ProviderResponseReceived => "dan prijema odgovora operatera",
        LegalAnchor.ProviderResponseDue => "dan do kog je operater bio dužan da odgovori",
        _ => "dan podnošenja zahteva Regulatoru",
    };
}
