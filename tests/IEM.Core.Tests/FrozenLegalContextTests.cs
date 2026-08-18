using IEM.Legal;

namespace IEM.Core.Tests;

/// <summary>
/// A case that has been answered does not change its mind because the program was updated.
/// <para>
/// 2.7 froze the rules into the case file and read them back - but every write re-resolved
/// the whole case, so recording the operator's answer under a corrected registry restated
/// deadlines that had been settled months earlier. The invariant held across reads and broke
/// across writes, which is the half nobody was testing.
/// </para>
/// </summary>
public sealed class FrozenLegalContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-frozen", Guid.NewGuid().ToString("N"));

    private static readonly DateOnly Filed = new(2026, 3, 10);
    private static readonly DateOnly Today = new(2026, 3, 12);

    public FrozenLegalContextTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private static ComplaintCase Case(DateOnly? responded = null) => new()
    {
        OperatorName = "Operater",
        SubscriberName = "Petar Petrović",
        EventDate = new DateOnly(2026, 3, 2),
        EventOrigin = FactOrigin.UserProvided,
        SubmittedDate = Filed,
        OperatorRespondedDate = responded,
    };

    /// <summary>
    /// A ruleset that is not in the registry - what a case decided under a version that has
    /// since been superseded looks like from here.
    /// </summary>
    private static ResolvedLegalContext UnderOtherRules(ResolvedLegalContext original) =>
        original with { Ruleset = original.Ruleset with { Version = "2099.01.01.9" } };

    /// <summary>LEGAL_RESOLVED_MILESTONE_NEVER_CHANGES_ON_SAVE.</summary>
    [Fact]
    public void A_resolved_milestone_is_not_recomputed_when_the_case_is_saved_again()
    {
        var first = CaseJournalStore.Save(_root, new CaseJournal { Case = Case() }, Today);
        var complaintDue = first.For(ComplaintStep.ComplaintDue)!;

        // The registry moves on, and a new fact arrives at the same time.
        var second = CaseJournalStore.Save(
            _root,
            new CaseJournal { Case = Case(responded: new DateOnly(2026, 3, 16)) },
            new DateOnly(2026, 3, 16));

        var after = second.For(ComplaintStep.ComplaintDue)!;

        Assert.Equal(complaintDue.Due, after.Due);
        Assert.Equal(complaintDue.Value, after.Value);
        Assert.Equal(complaintDue.RuleId, after.RuleId);
        Assert.Equal(complaintDue.AnchoredOn, after.AnchoredOn);
        Assert.Equal(complaintDue.Citations, after.Citations);
    }

    /// <summary>NEW_ANCHOR_ONLY_RESOLVES_DEPENDENT_MILESTONE.</summary>
    [Fact]
    public void A_new_fact_settles_only_what_depended_on_it()
    {
        var before = CaseJournalStore.Save(_root, new CaseJournal { Case = Case() }, Today);

        // Nothing has reached the Regulator, so that period is open; the two before it are not.
        Assert.Equal(LegalContextState.Resolved, before.For(ComplaintStep.ComplaintDue)!.State);
        Assert.Equal(LegalContextState.Resolved, before.For(ComplaintStep.OperatorResponseDue)!.State);
        Assert.Null(before.For(ComplaintStep.RegulatorDecisionTarget));

        var filedWithRegulator = Case(responded: new DateOnly(2026, 3, 16)) with
        {
            RegulatorFiledDate = new DateOnly(2026, 4, 1),
        };

        var after = CaseJournalStore.Save(
            _root, new CaseJournal { Case = filedWithRegulator }, new DateOnly(2026, 4, 1));

        // The new date settles the decision target, which had nothing to stand on before.
        Assert.Equal(90, after.For(ComplaintStep.RegulatorDecisionTarget)!.Value);
        Assert.Equal(new DateOnly(2026, 6, 30), after.For(ComplaintStep.RegulatorDecisionTarget)!.Due);

        // And leaves everything that was already settled exactly as it was.
        Assert.Equal(
            before.For(ComplaintStep.OperatorResponseDue)!.Due,
            after.For(ComplaintStep.OperatorResponseDue)!.Due);
    }

    /// <summary>REGISTRY_UPDATE_ALONE_NEVER_CHANGES_CASE_MEANING.</summary>
    [Fact]
    public void Saving_without_a_new_fact_leaves_the_case_untouched()
    {
        var before = CaseJournalStore.Save(_root, new CaseJournal { Case = Case() }, Today);

        // Load, save again, nothing new - a fortnight later, under whatever registry ships.
        var reloaded = CaseJournalStore.Load(_root)!;
        var after = CaseJournalStore.Save(_root, reloaded, new DateOnly(2026, 3, 26));

        Assert.Equal(before.Ruleset, after.Ruleset);
        Assert.Equal(Meaning(before), Meaning(after));
    }

    /// <summary>
    /// Every period as a line of text: which rule, how long, from which event, from which
    /// date, falling when, and on whose authority.
    /// <para>
    /// Compared this way rather than as objects because a record compares its citation list
    /// by reference, so two identical timetables rebuilt from the same file would differ.
    /// What has to stay the same is the meaning, and this is what the meaning is.
    /// </para>
    /// </summary>
    private static string Meaning(ResolvedLegalContext context) => string.Join(
        " // ",
        context.AppliedRules.Select(rule =>
            $"{rule.Step}|{rule.State}|{rule.RuleId}|{rule.Value}|{rule.Anchor}|" +
            $"{rule.AnchoredOn?.Date:O}|{rule.Due:O}|" +
            string.Join(',', rule.Citations.Select(citation => citation.SourceId))));

    /// <summary>
    /// The rules a case ran under are gone from the registry. Nothing new is settled by
    /// reaching for today's - half a case under one set of rules and half under another,
    /// with one identifier claiming otherwise, is the same defect in better clothes.
    /// </summary>
    [Fact]
    public void A_case_decided_under_rules_no_longer_published_settles_nothing_new()
    {
        var original = Case().Resolve(Today);

        var extended = LegalRegistry.Extend(
            (Case(responded: new DateOnly(2026, 3, 16)) with
            {
                RegulatorFiledDate = new DateOnly(2026, 4, 1),
            }).Facts,
            UnderOtherRules(original),
            new DateOnly(2026, 4, 1));

        var decision = extended.For(ComplaintStep.RegulatorDecisionTarget);

        Assert.Equal(LegalContextState.Unresolved, decision!.State);
        Assert.Null(decision.Due);
        Assert.Contains("nisu više u registru", decision.Impediment!, StringComparison.Ordinal);

        // What was already settled still stands.
        Assert.Equal(
            original.For(ComplaintStep.ComplaintDue)!.Due,
            extended.For(ComplaintStep.ComplaintDue)!.Due);
    }

    /// <summary>
    /// Someone corrects the day their service failed. The deadlines really do move with it -
    /// but not silently, and not by this path.
    /// </summary>
    [Fact]
    public void Changing_a_date_a_deadline_was_counted_from_is_flagged_rather_than_applied()
    {
        var before = CaseJournalStore.Save(_root, new CaseJournal { Case = Case() }, Today);
        var original = before.For(ComplaintStep.ComplaintDue)!.Due;

        var corrected = Case() with { EventDate = new DateOnly(2026, 2, 20) };
        var after = CaseJournalStore.Save(_root, new CaseJournal { Case = corrected }, Today);

        var complaintDue = after.For(ComplaintStep.ComplaintDue)!;

        Assert.Equal(original, complaintDue.Due);
        Assert.NotNull(complaintDue.Conflict);
        Assert.Contains("20.02.2026.", complaintDue.Conflict!, StringComparison.Ordinal);
        Assert.True(after.HasConflicts);
    }
}
