using System.Globalization;

namespace IEM.Legal;

/// <summary>How a period behaves when the law changes between its start and its end.</summary>
public enum TransitionPolicy
{
    /// <summary>
    /// The rule in force when the clock started governs it, whatever happens afterwards.
    /// The ordinary case: a complaint period that began under one regime is not shortened by
    /// a change three weeks later.
    /// </summary>
    ApplyRuleInForceAtAnchor,

    /// <summary>
    /// A proceeding already started finishes under the rules it started under.
    /// <para>
    /// The transitional clause of the 58/2024 pravilnik, and the reason this cannot be
    /// decided from a single case date: if the request reached the Regulator before 1 January
    /// 2025 the old rules see it through; if it did not, the new ones apply even though the
    /// complaint to the operator went in months earlier. Where the proceeding has not been
    /// started and the period straddles the boundary, the answer is genuinely open - and the
    /// program says so rather than picking a side.
    /// </para>
    /// </summary>
    FinishUnderStartedRules,
}

/// <summary>The unit a period is counted in.</summary>
public enum LegalUnit
{
    Days,
    Hours,
}

/// <summary>Which cases a rule speaks to. A null field means "any".</summary>
/// <param name="Description">The condition in words, for the report and the letter.</param>
public sealed record RuleCondition(
    CustomerType? CustomerType = null,
    ServiceKind? ServiceKind = null,
    ComplaintKind? ComplaintKind = null,
    string Description = "")
{
    public bool Matches(CustomerType customer, ServiceKind service, ComplaintKind complaint) =>
        (CustomerType is null || CustomerType == customer) &&
        (ServiceKind is null || ServiceKind == service) &&
        (ComplaintKind is null || ComplaintKind == complaint);

    /// <summary>How many fields it pins down, so the most specific rule wins.</summary>
    public int Specificity =>
        (CustomerType is null ? 0 : 1) + (ServiceKind is null ? 0 : 1) + (ComplaintKind is null ? 0 : 1);
}

/// <summary>
/// One period, with what it runs from, who it applies to, and where it comes from.
/// <para>
/// A value with its provenance attached, rather than a constant. The constants were the
/// problem: <c>DaysForOperatorResponse = 15</c> sat in a record whose comment promised it was
/// configurable, no production call site ever passed a different one, and when the number
/// turned out to be wrong for a consumer there was no way to tell which cases had been
/// computed under it.
/// </para>
/// </summary>
public sealed record LegalRule
{
    public required string RuleId { get; init; }

    public required ComplaintStep Step { get; init; }

    public required int Value { get; init; }

    public LegalUnit Unit { get; init; } = LegalUnit.Days;

    /// <summary>The event this period is counted from.</summary>
    public required LegalAnchor Anchor { get; init; }

    /// <summary>
    /// Used when <see cref="Anchor"/> has no date. The complaint period runs from the day the
    /// service failed; where that is not known but the day it was provided is, the law still
    /// has something to count from.
    /// </summary>
    public LegalAnchor? FallbackAnchor { get; init; }

    public RuleCondition Condition { get; init; } = new();

    public TransitionPolicy TransitionPolicy { get; init; } = TransitionPolicy.ApplyRuleInForceAtAnchor;

    public required IReadOnlyList<LegalCitation> Citations { get; init; }

    /// <summary>
    /// The citations that were in force on a given day.
    /// <para>
    /// Both consumer protection acts prescribe eight days, so the rule is one rule - but a
    /// case from July 2026 must cite the act that governed it in July 2026.
    /// </para>
    /// </summary>
    public IReadOnlyList<LegalCitation> CitationsOn(DateOnly date)
    {
        var applicable = Citations.Where(c => c.AppliesOn(date)).ToArray();
        return applicable.Length > 0 ? applicable : Citations;
    }

    public DateOnly DueFrom(DateOnly anchorDate) =>
        Unit == LegalUnit.Hours ? anchorDate.AddDays(Value / 24) : anchorDate.AddDays(Value);

    /// <summary>The rule in one line, canonical enough to hash.</summary>
    public string Canonical() => string.Join(
        '|',
        RuleId,
        Step,
        Value.ToString(CultureInfo.InvariantCulture),
        Unit,
        Anchor,
        FallbackAnchor?.ToString() ?? "-",
        Condition.CustomerType?.ToString() ?? "*",
        Condition.ServiceKind?.ToString() ?? "*",
        Condition.ComplaintKind?.ToString() ?? "*",
        TransitionPolicy,
        string.Join(',', Citations.Select(c => c.SourceId)));
}
