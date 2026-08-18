using System.Security.Cryptography;
using System.Text;

namespace IEM.Legal;

/// <summary>Which body of rules a case runs on.</summary>
public enum LegalRegime
{
    /// <summary>
    /// The transitional regime: the old law's article 113, left standing until the act under
    /// article 140 of the new one was adopted.
    /// </summary>
    LegacyPre2025,

    /// <summary>ZEK 35/2023 with the pravilnik adopted under article 140, from 1 January 2025.</summary>
    Current,
}

/// <summary>
/// Identifies a body of rules exactly, so an old case can be recomputed and come out the same.
/// <para>
/// The identifier alone is not enough. Change a period inside <c>RS-ZEK-2025</c> - because the
/// law moved, or because an entry turned out to be wrong - and every case that recorded only
/// the identifier silently acquires the new meaning. The version and the hash are what make
/// "this case was decided under these rules" a statement that can be checked.
/// </para>
/// </summary>
public sealed record LegalRulesetRef(string Id, string Version, string ContentHash)
{
    public override string ToString() => $"{Id} {Version} ({ContentHash[..16]})";
}

/// <summary>
/// One regime's periods.
/// <para>
/// Immutable once published. A change to the law produces a new version with its own hash; the
/// existing one is never edited, because cases decided under it are entitled to keep the
/// meaning they had.
/// </para>
/// </summary>
public sealed record LegalRuleset
{
    public required string Id { get; init; }

    /// <summary>Bumped whenever any rule in it changes. Never edited in place.</summary>
    public required string Version { get; init; }

    public required LegalRegime Regime { get; init; }

    public DateOnly? AppliesFrom { get; init; }

    public DateOnly? AppliesTo { get; init; }

    public required IReadOnlyList<LegalRule> Rules { get; init; }

    public required DateOnly VerifiedAt { get; init; }

    public bool AppliesOn(DateOnly date) =>
        (AppliesFrom is not { } from || date >= from) &&
        (AppliesTo is not { } to || date <= to);

    /// <summary>
    /// A digest of every rule in it. Two rulesets with the same hash mean the same thing; a
    /// case that recorded one can be recomputed against the other without changing.
    /// </summary>
    public string ContentHash => _hash ??= Hash();

    private string? _hash;

    public LegalRulesetRef Ref => new(Id, Version, ContentHash);

    private string Hash()
    {
        var canonical = string.Join(
            '\n',
            [$"{Id}|{Version}|{Regime}", .. Rules.Select(rule => rule.Canonical())]);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// The rule for this step and these facts, or null when the regime has none.
    /// <para>
    /// The most specific match wins, so "roaming, whoever the subscriber is" outranks
    /// "consumer, whatever the service".
    /// </para>
    /// </summary>
    public LegalRule? RuleFor(ComplaintStep step, CustomerType customer, ServiceKind service, ComplaintKind complaint) =>
        Rules
            .Where(rule => rule.Step == step && rule.Condition.Matches(customer, service, complaint))
            .OrderByDescending(rule => rule.Condition.Specificity)
            .FirstOrDefault();
}

/// <summary>How well the legal position of a case could be established.</summary>
public enum LegalContextState
{
    /// <summary>The case carries the rules it was decided under.</summary>
    Resolved,

    /// <summary>
    /// No recorded rules, but the dates on file are enough and consistent enough to work out
    /// which applied. Said out loud in the output, because it is a reconstruction.
    /// </summary>
    InferredFromRecordedDates,

    /// <summary>
    /// Not established. No deadline is declared either expired or open on this basis - which
    /// is the whole point of having the state at all.
    /// </summary>
    Unresolved,
}

/// <summary>What is known about a case, as the events the law counts from.</summary>
public sealed record CaseFacts
{
    public CustomerType CustomerType { get; init; } = CustomerType.Consumer;

    public ServiceKind ServiceKind { get; init; } = ServiceKind.Standard;

    public ComplaintKind ComplaintKind { get; init; } = ComplaintKind.ServiceQuality;

    public AnchoredDate? InvoiceDue { get; init; }

    public AnchoredDate? ServiceProvided { get; init; }

    public AnchoredDate? ServiceUnavailable { get; init; }

    public AnchoredDate? ComplaintFiled { get; init; }

    public AnchoredDate? ResponseReceived { get; init; }

    public AnchoredDate? RegulatorProceedingFiled { get; init; }

    /// <summary>The date recorded for an anchor, or null when there is none.</summary>
    public AnchoredDate? On(LegalAnchor anchor) => anchor switch
    {
        LegalAnchor.InvoiceDueDate => InvoiceDue,
        LegalAnchor.ServiceProvidedDate => ServiceProvided,
        LegalAnchor.ServiceUnavailableDate => ServiceUnavailable,
        LegalAnchor.ProviderComplaintFiled => ComplaintFiled,
        LegalAnchor.ProviderResponseReceived => ResponseReceived,
        LegalAnchor.RegulatorProceedingFiled => RegulatorProceedingFiled,
        _ => null,
    };
}

/// <summary>
/// How settled a period is.
/// <para>
/// Separate from <see cref="LegalContextState"/>, which says how good the provenance of the
/// dates is. This says whether the period is final: a deadline counted from the day an answer
/// was <em>owed</em> is not the same kind of answer as one counted from the day it actually
/// arrived, even though both are computed and both have a date.
/// </para>
/// </summary>
public enum ResolutionState
{
    /// <summary>Nothing to count from yet.</summary>
    Unresolved,

    /// <summary>
    /// Settled from a fallback anchor because the primary one was not available. Correct for
    /// now and replaceable later: the law counts this period from an event that has not
    /// happened yet, and when it does the same frozen rules give a different date.
    /// </summary>
    Provisional,

    /// <summary>Settled from the anchor the law names first. This does not change by itself.</summary>
    Resolved,

    /// <summary>
    /// A date that was already used to count this period has since been given a different
    /// value. The period keeps the date it had; the disagreement is stated.
    /// </summary>
    Conflict,
}

/// <summary>Why a period stopped being what it was, and what it was.</summary>
public enum ResolutionChange
{
    /// <summary>
    /// The event the law counts this period from finally happened, so the provisional
    /// answer taken from the fallback anchor gave way to the real one.
    /// </summary>
    PrimaryAnchorBecameAvailable,
}

/// <param name="Reason">What replaced it.</param>
public sealed record PreviousResolution(
    LegalAnchor? Anchor,
    DateOnly? AnchoredOn,
    DateOnly? Due,
    ResolutionChange Reason);

/// <summary>One period as it applied to one case: the rule, what it was counted from, and when it falls.</summary>
public sealed record AppliedRule
{
    public required ComplaintStep Step { get; init; }

    public required LegalContextState State { get; init; }

    public string? RuleId { get; init; }

    public int? Value { get; init; }

    /// <summary>
    /// The event this period was actually counted from - the fallback when the primary one
    /// had no date yet, not the one the rule names first.
    /// </summary>
    /// <remarks>
    /// It used to record the rule's primary anchor whatever was actually used, so a period
    /// counted from the day an answer was owed claimed to have been counted from the day it
    /// arrived. When the answer then arrived, the two disagreed and the case reported a
    /// conflict where nothing had gone wrong.
    /// </remarks>
    public LegalAnchor? Anchor { get; init; }

    /// <summary>How settled this period is.</summary>
    public ResolutionState Resolution { get; init; } = ResolutionState.Unresolved;

    /// <summary>
    /// The provisional answer this one replaced, kept so the case does not lose what it said
    /// before. One step back, not a history - that is 3.0 work.
    /// </summary>
    public PreviousResolution? Superseded { get; init; }

    /// <summary>The date it was counted from, with where that date came from.</summary>
    public AnchoredDate? AnchoredOn { get; init; }

    public DateOnly? Due { get; init; }

    public IReadOnlyList<LegalCitation> Citations { get; init; } = [];

    /// <summary>Why it could not be settled, when it could not.</summary>
    public string? Impediment { get; init; }

    /// <summary>
    /// Set when a date this period was counted from has since been given a different value.
    /// <para>
    /// The period itself is left exactly as it was resolved. Recomputing it silently would
    /// change what a case meant on the strength of an edit nobody was shown - so the old
    /// value stands and the disagreement is stated. Reconciling the two properly needs a
    /// history of resolutions, which is 3.0 work.
    /// </para>
    /// <para>
    /// A fallback anchor giving way to the primary one is <em>not</em> this. That is the law
    /// working as written, and it has its own state.
    /// </para>
    /// </summary>
    public string? Conflict { get; init; }
}

/// <summary>
/// The rules as they applied to one case, frozen.
/// <para>
/// Written into the case file rather than recomputed on demand. A report produced in 2028 for
/// a case from 2026 has to reproduce the legal position that was applied then - which means
/// carrying the version, the hash, the periods, and the dates each one was counted from. An
/// identifier alone would let a later correction to the registry rewrite the past.
/// </para>
/// </summary>
public sealed record ResolvedLegalContext
{
    public required LegalRulesetRef Ruleset { get; init; }

    public required LegalRegime Regime { get; init; }

    public required CustomerType CustomerType { get; init; }

    public required ServiceKind ServiceKind { get; init; }

    public required ComplaintKind ComplaintKind { get; init; }

    public required IReadOnlyList<AppliedRule> AppliedRules { get; init; }

    public required DateOnly ResolvedAt { get; init; }

    /// <summary>The weakest of the individual states: one open question makes the case uncertain.</summary>
    public LegalContextState State => AppliedRules.Count == 0
        ? LegalContextState.Unresolved
        : AppliedRules.Max(rule => rule.State);

    public AppliedRule? For(ComplaintStep step) =>
        AppliedRules.FirstOrDefault(rule => rule.Step == step);

    /// <summary>True when a fact behind an already-settled period has since been changed.</summary>
    public bool HasConflicts => AppliedRules.Any(rule => rule.Conflict is not null);
}
