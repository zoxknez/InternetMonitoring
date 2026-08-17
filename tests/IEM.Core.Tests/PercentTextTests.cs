using IEM.Core.Presentation;

namespace IEM.Core.Tests;

/// <summary>
/// How a percentage is written.
/// <para>
/// Shared by the window, the console, both reports and the complaint letter, so these cases
/// fix the wording in all of them at once. Two of them are not about tidiness at all: a
/// figure that rounds to a clean 100 or a clean 0 states something about the service that
/// the measurement does not support, in a document someone sends to their operator.
/// </para>
/// </summary>
public sealed class PercentTextTests
{
    [Theory]
    [InlineData(100d, "100 %")]
    [InlineData(0d, "0 %")]
    [InlineData(99.5d, "99,5 %")]
    [InlineData(98.25d, "98,25 %")]
    public void Only_the_decimals_that_carry_a_digit_are_written(double value, string expected) =>
        Assert.Equal(expected, SerbianText.Percent(value));

    /// <summary>
    /// Two decimals are all a person reads, and all a report needs. Anything finer turns the
    /// figure into noise: "99,9968 %" says no more than "99,99 %" and is harder to read.
    /// </summary>
    [Theory]
    [InlineData(99.9968d, "99,99 %")]
    [InlineData(99.9987d, "99,99 %")]
    [InlineData(99.9871d, "99,99 %")]
    [InlineData(99.9874d, "99,99 %")]
    [InlineData(87.3d, "87,3 %")]
    public void Availability_is_written_to_at_most_two_decimals(double value, string expected) =>
        Assert.Equal(expected, SerbianText.Percent(value));

    /// <summary>
    /// Half a second of outage across two days is 99,99971 %. Rounded to four decimals that
    /// is 100,0000, and printed as "100 %" it tells an operator the service never failed -
    /// which is the one thing the whole session exists to contradict.
    /// </summary>
    [Fact]
    public void A_connection_that_failed_is_never_written_as_a_clean_hundred()
    {
        var availability = 100d - (0.5d / TimeSpan.FromDays(2).TotalSeconds * 100d);

        Assert.True(availability < 100d, "fixture mora biti ispod sto");
        Assert.NotEqual("100 %", SerbianText.Percent(availability));
        Assert.StartsWith("99,9", SerbianText.Percent(availability), StringComparison.Ordinal);
    }

    /// <summary>The same rule at the other end: measured loss is never written as no loss.</summary>
    [Fact]
    public void Measured_loss_is_never_written_as_a_clean_zero()
    {
        Assert.NotEqual("0 %", SerbianText.Percent(0.004d, decimals: 1));
        Assert.NotEqual("0 %", SerbianText.Percent(0.00001d));
    }

    /// <summary>Exactly zero is zero; the guard above must not push a true zero off it.</summary>
    [Fact]
    public void A_true_zero_stays_zero()
    {
        Assert.Equal("0 %", SerbianText.Percent(0d));
        Assert.Equal("0 %", SerbianText.Percent(0d, decimals: 1));
    }

    /// <summary>Serbian writes decimals with a comma, whatever the machine's locale is.</summary>
    [Fact]
    public void The_decimal_separator_is_a_comma()
    {
        Assert.Contains(',', SerbianText.Percent(99.75d));
        Assert.DoesNotContain('.', SerbianText.Percent(99.75d));
    }

    [Theory]
    [InlineData(0d, "0 %")]
    [InlineData(2.5d, "2,5 %")]
    [InlineData(12d, "12 %")]
    public void Loss_is_written_to_one_decimal(double value, string expected) =>
        Assert.Equal(expected, SerbianText.Percent(value, decimals: 1));
}
