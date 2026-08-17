using IEM.Core.Model;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// A speed figure is judged by different rules than an outage, and keeping the two apart is
/// the whole point. An outage recorded over Wi-Fi is perfectly good evidence - the
/// connection was down, and how the computer was attached to the router does not change
/// that. A <em>speed</em> measured over Wi-Fi proves nothing about the service, because the
/// slowest link in the path is the one the customer owns.
/// </summary>
public sealed class SpeedMeasurementTests
{
    /// <summary>
    /// A measurement whose path was checked and came back agreeing. Stated rather than left
    /// to a default, because the default is now "nobody checked" - which costs a measurement
    /// its standing, and should.
    /// </summary>
    private static SpeedMeasurementConditions Wired(double measured, double? contracted = 100) =>
        new(LinkMedium.Ethernet, 1_000_000_000, contracted, measured)
        {
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
        };

    // ---- What makes a measurement usable at all -------------------------------

    [Fact]
    public void A_clean_wired_measurement_can_support_a_complaint()
    {
        var validity = SpeedMeasurementValidity.Of(Wired(60));

        Assert.True(validity.IsValidForComplaint);
        Assert.Empty(validity.Defects);
    }

    /// <summary>
    /// The single most common reason a speed complaint is dismissed, and rightly so.
    /// </summary>
    [Fact]
    public void A_measurement_over_wifi_cannot_prove_contracted_speed()
    {
        var conditions = Wired(60) with { Medium = LinkMedium.Wireless };
        var validity = SpeedMeasurementValidity.Of(conditions);

        Assert.False(validity.IsValidForComplaint);
        Assert.Contains(SpeedMeasurementDefect.NotWired, validity.Defects);
        Assert.Contains("mrežnog kabla", SpeedMeasurementDefect.NotWired.Explain(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The distinction that must not be lost: the same Wi-Fi connection produces perfectly
    /// good outage evidence. Rolling the two rules together would either discard valid
    /// evidence from every laptop, or let a Wi-Fi speed figure into a complaint.
    /// </summary>
    [Fact]
    public void The_same_wifi_connection_still_produces_valid_outage_evidence()
    {
        var cycle = CycleBuilder.Wireless().AllExternalFail().Build();

        var verdict = new IEM.Core.Classification.StateClassifier().Classify(cycle);

        Assert.True(verdict.IsOutage);
        Assert.False(SpeedMeasurementValidity.Of(Wired(60) with { Medium = LinkMedium.Wireless }).IsValidForComplaint);
    }

    /// <summary>
    /// A 100 Mbit/s port cannot demonstrate a 200 Mbit/s service however slow the line is -
    /// the measurement would show the cable, not the connection.
    /// </summary>
    [Fact]
    public void A_port_no_faster_than_the_contract_cannot_demonstrate_it()
    {
        var conditions = new SpeedMeasurementConditions(
            LinkMedium.Ethernet, LinkSpeedBitsPerSecond: 100_000_000, ContractedDownloadMbps: 200, 95);

        var validity = SpeedMeasurementValidity.Of(conditions);

        Assert.Contains(SpeedMeasurementDefect.LinkSlowerThanContract, validity.Defects);
    }

    [Fact]
    public void A_measurement_taken_while_the_link_was_busy_is_not_usable()
    {
        var validity = SpeedMeasurementValidity.Of(Wired(60) with { LinkWasIdle = false });

        Assert.Contains(SpeedMeasurementDefect.LinkBusy, validity.Defects);
    }

    [Fact]
    public void A_measurement_through_a_vpn_or_two_adapters_is_not_usable()
    {
        var validity = SpeedMeasurementValidity.Of(
            Wired(60) with { RouteState = MeasurementRouteState.MixedRoutes });

        Assert.Contains(SpeedMeasurementDefect.PathAmbiguous, validity.Defects);
    }

    [Fact]
    public void A_measurement_that_left_entirely_through_another_adapter_is_not_usable()
    {
        var validity = SpeedMeasurementValidity.Of(
            Wired(60) with { RouteState = MeasurementRouteState.OtherRouteOnly });

        Assert.False(validity.IsValidForComplaint);
        Assert.Contains(SpeedMeasurementDefect.PathElsewhere, validity.Defects);
    }

    /// <summary>
    /// The one that used to pass. Conditions built without anybody consulting the route table
    /// recorded "one path, no VPN" and went into a complaint as a verified measurement of a
    /// link nobody had established it travelled over.
    /// </summary>
    [Fact]
    public void A_measurement_whose_path_was_never_checked_is_not_usable()
    {
        var validity = SpeedMeasurementValidity.Of(
            new SpeedMeasurementConditions(LinkMedium.Ethernet, 1_000_000_000, 100, 60));

        Assert.False(validity.IsValidForComplaint);
        Assert.Contains(SpeedMeasurementDefect.PathUnverified, validity.Defects);
    }

    [Fact]
    public void Without_a_contract_there_is_nothing_to_compare_against()
    {
        var validity = SpeedMeasurementValidity.Of(Wired(60, contracted: null));

        Assert.Contains(SpeedMeasurementDefect.ContractUnknown, validity.Defects);
        Assert.Null(SpeedMeasurementValidity.Judge(Wired(60, contracted: null)));
    }

    // ---- Where the figure falls -----------------------------------------------

    [Theory]
    [InlineData(95, SpeedBand.AtAdvertised)]
    [InlineData(90, SpeedBand.AtAdvertised)]
    [InlineData(85, SpeedBand.NormallyAvailable)]
    [InlineData(80, SpeedBand.NormallyAvailable)]
    [InlineData(75, SpeedBand.AboveMinimum)]
    [InlineData(70, SpeedBand.AboveMinimum)]
    [InlineData(69, SpeedBand.BelowMinimum)]
    [InlineData(30, SpeedBand.BelowMinimum)]
    public void The_band_follows_the_share_of_the_contracted_rate(double measured, SpeedBand expected)
    {
        Assert.Equal(expected, SpeedMeasurementValidity.Judge(Wired(measured, contracted: 100)));
    }

    /// <summary>
    /// Only the bottom band is grounds for a complaint, and the wording has to say which
    /// conditions that still depends on.
    /// </summary>
    [Fact]
    public void Only_a_figure_below_the_minimum_is_described_as_grounds_for_a_complaint()
    {
        Assert.Contains("osnov za prigovor", SpeedBand.BelowMinimum.Explain(), StringComparison.Ordinal);
        Assert.Contains("preko kabla", SpeedBand.BelowMinimum.Explain(), StringComparison.Ordinal);

        // And a good figure does not conclude the other way either. "Nema osnova za prigovor
        // po brzini" was a verdict on the service drawn from one sample, in the operator's
        // favour - the same substitution this release removes, pointed the other direction.
        foreach (var band in new[] { SpeedBand.NormallyAvailable, SpeedBand.AtAdvertised })
        {
            Assert.DoesNotContain("Nema osnova", band.Explain(), StringComparison.Ordinal);
            Assert.Contains("ne znači da je usluga uredna", band.Explain(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// P0-7. "Uobičajeno dostupna brzina" is defined by the Pravilnik as at least 80 % of the
    /// contracted rate held over 90 % of the measurement time. One measurement cannot satisfy
    /// a condition about a share of time, however good the number is - so the label says where
    /// the figure falls and the explanation says what it would take to conclude.
    /// </summary>
    [Fact]
    public void No_single_measurement_is_labelled_with_the_regulators_conclusion()
    {
        foreach (var band in Enum.GetValues<SpeedBand>())
        {
            Assert.DoesNotContain("uobičajeno dostupna", band.Label(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("uobičajeno dostupna", band.UploadLabel(), StringComparison.OrdinalIgnoreCase);

            Assert.Contains("90 % vremena", band.Explain(), StringComparison.Ordinal);
            Assert.Contains("serija merenja", band.Explain(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A single reading between the floor and the usual level is not a case, and saying so
    /// keeps someone from filing a complaint they will lose.
    /// </summary>
    [Fact]
    public void One_reading_above_the_minimum_is_not_presented_as_a_case()
    {
        var explanation = SpeedBand.AboveMinimum.Explain();

        Assert.Contains("nije osnov za prigovor", explanation, StringComparison.Ordinal);
        Assert.Contains("ponovljena merenja", explanation, StringComparison.Ordinal);
    }

    // ---- The sending direction ---------------------------------------------------

    /// <summary>
    /// A connection that meets its download figure while falling short on upload is an
    /// ordinary complaint - and one a download-only measurement could not state at all.
    /// </summary>
    [Fact]
    public void The_sending_direction_is_judged_against_its_own_contracted_rate()
    {
        var conditions = Wired(95, contracted: 100) with
        {
            ContractedUploadMbps = 20,
            MeasuredUploadMbps = 8,
        };

        Assert.Equal(SpeedBand.AtAdvertised, SpeedMeasurementValidity.Judge(conditions));
        Assert.Equal(SpeedBand.BelowMinimum, SpeedMeasurementValidity.JudgeUpload(conditions));
    }

    [Fact]
    public void Without_a_contracted_upload_rate_the_sending_direction_is_not_judged()
    {
        var measuredOnly = Wired(95) with { MeasuredUploadMbps = 8 };
        var contractedOnly = Wired(95) with { ContractedUploadMbps = 20 };

        Assert.Null(SpeedMeasurementValidity.JudgeUpload(measuredOnly));
        Assert.Null(SpeedMeasurementValidity.JudgeUpload(contractedOnly));
    }

    [Theory]
    [InlineData(19, SpeedBand.AtAdvertised)]
    [InlineData(16.5, SpeedBand.NormallyAvailable)]
    [InlineData(14.5, SpeedBand.AboveMinimum)]
    [InlineData(12, SpeedBand.BelowMinimum)]
    public void The_sending_band_follows_the_same_shares_as_the_receiving_one(
        double measured,
        SpeedBand expected)
    {
        var conditions = Wired(95) with { ContractedUploadMbps = 20, MeasuredUploadMbps = measured };

        Assert.Equal(expected, SpeedMeasurementValidity.JudgeUpload(conditions));
    }

    [Fact]
    public void The_sending_verdict_says_which_direction_it_is_about()
    {
        foreach (var band in Enum.GetValues<SpeedBand>())
        {
            Assert.Contains("slanje", band.UploadLabel(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_speed_figure_points_at_the_official_measurement()
    {
        Assert.Contains("NetTest", SpeedText.OfficialMeasurementNote, StringComparison.Ordinal);
        Assert.Contains("ne", SpeedText.OfficialMeasurementNote, StringComparison.Ordinal);
        Assert.NotEmpty(SpeedText.Checklist);
    }

    [Fact]
    public void Every_defect_says_what_is_wrong_in_plain_language()
    {
        foreach (var defect in Enum.GetValues<SpeedMeasurementDefect>())
        {
            Assert.NotEmpty(defect.Explain());
        }
    }
}
