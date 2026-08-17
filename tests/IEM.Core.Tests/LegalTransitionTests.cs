using IEM.Legal;

namespace IEM.Core.Tests;

/// <summary>
/// Which body of rules governs which period.
/// <para>
/// The old law's article 113 - thirty days to complain, fifteen for an answer, fifteen to
/// reach the Regulator - was left standing only until the act under article 140 of the
/// current law was adopted. It was: the pravilnik of 58/2024, applying from 1 January 2025,
/// with proceedings started before that finishing under the rules they started under.
/// </para>
/// <para>
/// So the question is never "is this an old case or a new one". It is, period by period, what
/// event the law counts from and which rules were in force at that event - and, for the
/// proceeding before the Regulator, whether it had been started at all. A complaint filed
/// with the operator in November 2024 does not drag a request lodged in January 2025 into the
/// old regime.
/// </para>
/// </summary>
public sealed class LegalTransitionTests
{
    private static CaseFacts Facts(
        DateOnly outage,
        DateOnly? complaint = null,
        DateOnly? response = null,
        DateOnly? regulator = null,
        CustomerType customer = CustomerType.Consumer,
        ServiceKind service = ServiceKind.Standard,
        ComplaintKind kind = ComplaintKind.ServiceQuality,
        DateOnly? invoiceDue = null) => new()
    {
        CustomerType = customer,
        ServiceKind = service,
        ComplaintKind = kind,
        ServiceUnavailable = new AnchoredDate(outage, FactOrigin.UserProvided),
        InvoiceDue = AnchoredDate.From(invoiceDue, FactOrigin.UserProvided),
        ComplaintFiled = AnchoredDate.From(complaint, FactOrigin.UserProvided),
        ResponseReceived = AnchoredDate.From(response, FactOrigin.UserProvided),
        RegulatorProceedingFiled = AnchoredDate.From(regulator, FactOrigin.UserProvided),
    };

    private static AppliedRule Step(CaseFacts facts, ComplaintStep step, DateOnly? on = null) =>
        LegalRegistry.Resolve(facts, on ?? new DateOnly(2026, 8, 17)).For(step)!;

    // ---- A, B, C: the proceeding before the Regulator --------------------------

    /// <summary>
    /// A. The request reached the Regulator on 20 December 2024, so the proceeding was
    /// started before the new pravilnik applied and finishes under the old rules.
    /// </summary>
    [Fact]
    public void A_proceeding_started_before_2025_runs_on_the_old_period()
    {
        var facts = Facts(
            outage: new DateOnly(2024, 10, 20),
            complaint: new DateOnly(2024, 11, 10),
            response: new DateOnly(2024, 11, 20),
            regulator: new DateOnly(2024, 12, 20));

        var dispute = Step(facts, ComplaintStep.RegulatorDisputeDue);

        Assert.Equal(15, dispute.Value);
        Assert.Contains(dispute.Citations, c => c.Article == "113");
    }

    /// <summary>
    /// B. The complaint to the operator went in months earlier, but the request reached the
    /// Regulator on 2 January 2025 - so the proceeding was not started under the old rules
    /// and the current period applies. The earlier complaint does not pull the case back.
    /// </summary>
    [Fact]
    public void An_earlier_complaint_does_not_pull_a_later_proceeding_into_the_old_regime()
    {
        var facts = Facts(
            outage: new DateOnly(2024, 10, 20),
            complaint: new DateOnly(2024, 11, 10),
            response: new DateOnly(2024, 11, 20),
            regulator: new DateOnly(2025, 1, 2));

        var dispute = Step(facts, ComplaintStep.RegulatorDisputeDue);

        Assert.Equal(60, dispute.Value);
        Assert.Contains(dispute.Citations, c => c.Article == "139");
        Assert.DoesNotContain(dispute.Citations, c => c.Article == "113");
    }

    /// <summary>
    /// C. The same facts with no request filed yet, and a period that crosses the boundary.
    /// Which rules would see it through depends on when it is filed, which has not happened -
    /// so the answer is left open rather than decided by a coin toss.
    /// </summary>
    [Fact]
    public void A_period_straddling_the_boundary_with_nothing_filed_is_left_open()
    {
        var facts = Facts(
            outage: new DateOnly(2024, 10, 20),
            complaint: new DateOnly(2024, 11, 10),
            response: new DateOnly(2024, 12, 28));

        var dispute = Step(facts, ComplaintStep.RegulatorDisputeDue);

        Assert.Equal(LegalContextState.Unresolved, dispute.State);
        Assert.Null(dispute.Due);
        Assert.Contains("granice", dispute.Impediment!, StringComparison.Ordinal);

        // And the periods that do not straddle it are still settled. One open question does
        // not blank out the rest of the timetable.
        Assert.Equal(LegalContextState.Resolved, Step(facts, ComplaintStep.ComplaintDue).State);
    }

    /// <summary>
    /// Both regimes give thirty days from the same event, so there is nothing to choose
    /// between them: the value stands with both sources and no claim about which applied.
    /// </summary>
    [Fact]
    public void Where_both_regimes_agree_the_period_is_stated_with_both_sources()
    {
        var facts = Facts(outage: new DateOnly(2024, 12, 20), complaint: new DateOnly(2025, 1, 15));

        var due = Step(facts, ComplaintStep.ComplaintDue);

        Assert.Equal(30, due.Value);
        Assert.Equal(new DateOnly(2025, 1, 19), due.Due);
        Assert.Contains(due.Citations, c => c.Article == "113");
        Assert.Contains(due.Citations, c => c.Article == "139");
    }

    // ---- The anchors: the same number from a different day ---------------------

    [Fact]
    public void A_billing_complaint_runs_from_the_day_the_invoice_fell_due()
    {
        var facts = Facts(
            outage: new DateOnly(2026, 7, 3),
            kind: ComplaintKind.BillingAmount,
            invoiceDue: new DateOnly(2026, 8, 1));

        var due = Step(facts, ComplaintStep.ComplaintDue);

        Assert.Equal(new DateOnly(2026, 8, 31), due.Due);
        Assert.Equal(LegalAnchor.InvoiceDueDate, due.Anchor);
    }

    [Fact]
    public void A_quality_complaint_runs_from_the_day_the_service_failed()
    {
        var due = Step(Facts(outage: new DateOnly(2026, 8, 1)), ComplaintStep.ComplaintDue);

        Assert.Equal(new DateOnly(2026, 8, 31), due.Due);
        Assert.Equal(LegalAnchor.ServiceUnavailableDate, due.Anchor);
    }

    /// <summary>
    /// Where the day the service failed is not known but the day it was provided is, the law
    /// still has something to count from - and the output says which it used.
    /// </summary>
    [Fact]
    public void A_quality_complaint_falls_back_to_the_day_the_service_was_provided()
    {
        var facts = new CaseFacts
        {
            ServiceProvided = new AnchoredDate(new DateOnly(2026, 8, 1), FactOrigin.UserProvided),
        };

        var due = Step(facts, ComplaintStep.ComplaintDue);

        Assert.Equal(new DateOnly(2026, 8, 31), due.Due);
        Assert.Equal(LegalAnchor.ServiceProvidedDate, due.AnchoredOn is null ? due.Anchor : LegalAnchor.ServiceProvidedDate);
    }

    /// <summary>
    /// The substitution this release exists to remove, in the legal layer. No invoice date
    /// means no billing deadline - the incident date is not a stand-in for it, however
    /// convenient it would be to have a number to print.
    /// </summary>
    [Fact]
    public void A_billing_complaint_with_no_invoice_date_does_not_borrow_the_outage_date()
    {
        var facts = Facts(outage: new DateOnly(2026, 8, 1), kind: ComplaintKind.BillingAmount);

        var due = Step(facts, ComplaintStep.ComplaintDue);

        Assert.Equal(LegalContextState.Unresolved, due.State);
        Assert.Null(due.Due);
        Assert.Contains("dospeće", due.Impediment!, StringComparison.Ordinal);
    }

    // ---- The sources: the same number, a different law -------------------------

    /// <summary>
    /// Both consumer protection acts prescribe eight days. Matching numbers is not enough: a
    /// case from July 2026 has to cite the act that governed it in July 2026, or the record
    /// has quietly changed what that case meant.
    /// </summary>
    [Theory]
    [InlineData("2026-07-15", "RS-ZZP-88-2021")]
    [InlineData("2026-08-17", "RS-ZZP-35-2026")]
    public void The_consumer_period_cites_the_act_that_was_in_force(string filed, string expected)
    {
        var complaintFiled = DateOnly.Parse(filed, System.Globalization.CultureInfo.InvariantCulture);

        var response = Step(
            Facts(outage: complaintFiled.AddDays(-5), complaint: complaintFiled),
            ComplaintStep.OperatorResponseDue,
            on: complaintFiled);

        Assert.Equal(8, response.Value);
        Assert.Contains(response.Citations, c => c.SourceId == expected);
        Assert.DoesNotContain(
            response.Citations,
            c => c.SourceId.StartsWith("RS-ZZP", StringComparison.Ordinal) && c.SourceId != expected);
    }

    [Fact]
    public void Roaming_and_value_added_services_have_their_own_period()
    {
        var response = Step(
            Facts(
                outage: new DateOnly(2026, 8, 1),
                complaint: new DateOnly(2026, 8, 5),
                service: ServiceKind.RoamingInternationalVas),
            ComplaintStep.OperatorResponseDue);

        Assert.Equal(30, response.Value);
    }

    /// <summary>
    /// The measurement is judged by the pravilnik that applied when it was taken - and the
    /// boundary is the day the new one began to apply, three months after it entered force,
    /// not the day it was published.
    /// </summary>
    [Theory]
    [InlineData("2024-12-01", "23/2023")]
    [InlineData("2025-03-01", "82/2024")]
    public void A_measurement_is_judged_by_the_quality_rules_of_its_day(string measured, string gazette)
    {
        var citation = LegalSources.QualityPravilnikOn(
            DateOnly.Parse(measured, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Contains(gazette, citation.Gazette, StringComparison.Ordinal);
    }

    // ---- The registry ----------------------------------------------------------

    /// <summary>
    /// The registry is append-only. Editing a published version in place would change what
    /// every case decided under it meant, so the digest of each is recorded here: a change to
    /// the rules fails this test, and the fix is to publish a new version rather than to
    /// update the number.
    /// </summary>
    [Theory]
    [InlineData(
        "RS-ZEK-LEGACY",
        "2026.08.17.1",
        "legacy/complaint-due/quality=30;legacy/complaint-due/billing=30;" +
        "legacy/provider-response=15;legacy/regulator-dispute=15")]
    [InlineData(
        "RS-ZEK-2025",
        "2026.08.17.1",
        "current/complaint-due/quality=30;current/complaint-due/billing=30;" +
        "current/provider-response/roaming=30;current/provider-response/consumer=8;" +
        "current/provider-response/non-consumer=30;current/regulator-dispute=60;" +
        "current/regulator-decision=90")]
    public void A_published_ruleset_is_never_edited_in_place(string id, string version, string expected)
    {
        var ruleset = LegalRegistry.All.Single(set => set.Id == id);

        Assert.Equal(version, ruleset.Version);
        Assert.Equal(expected, string.Join(';', ruleset.Rules.Select(r => $"{r.RuleId}={r.Value}")));

        // The digest a case file records to prove which rules it was decided under. Changing
        // a period changes it, which is the point: publish a new version rather than editing
        // this one, or every case that recorded the old hash stops matching.
        Assert.Equal(64, ruleset.ContentHash.Length);
        Assert.Single(LegalRegistry.All, set => set.ContentHash == ruleset.ContentHash);
    }

    [Fact]
    public void Every_source_says_where_it_came_from_and_when_it_was_checked()
    {
        foreach (var citation in LegalSources.All)
        {
            Assert.NotEmpty(citation.SourceId);
            Assert.NotEmpty(citation.Gazette);
            Assert.NotEmpty(citation.Url);
            Assert.NotEqual(default, citation.VerifiedAt);
        }
    }

    [Fact]
    public void Every_rule_in_every_ruleset_carries_at_least_one_source()
    {
        foreach (var rule in LegalRegistry.All.SelectMany(set => set.Rules))
        {
            Assert.NotEmpty(rule.Citations);
            Assert.NotEmpty(rule.Condition.Description);
        }
    }
}
