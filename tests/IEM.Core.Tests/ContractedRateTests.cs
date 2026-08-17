using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// The contracted rate is typed by hand, from a contract, by someone who is already annoyed
/// at their operator. So the field takes it the way it is written there - "100/20" - and
/// refuses anything it cannot read rather than half-understanding it: a typo quietly taken
/// for some other number is how a complaint ends up quoting a rate nobody ever contracted.
/// </summary>
public sealed class ContractedRateTests
{
    [Fact]
    public void A_bare_number_is_the_download_rate()
    {
        Assert.True(ContractedRate.TryParse("100", out var download, out var upload));

        Assert.Equal(100, download);
        Assert.Null(upload);
    }

    [Theory]
    [InlineData("100/20")]
    [InlineData("100 / 20")]
    [InlineData(" 100/20 ")]
    public void A_pair_is_download_then_upload(string text)
    {
        Assert.True(ContractedRate.TryParse(text, out var download, out var upload));

        Assert.Equal(100, download);
        Assert.Equal(20, upload);
    }

    /// <summary>
    /// Refusing "10,5" on a Serbian keyboard would be refusing the way the number is written
    /// here; refusing "10.5" would be refusing the way it is written in the contract PDF.
    /// </summary>
    [Theory]
    [InlineData("10,5", 10.5)]
    [InlineData("10.5", 10.5)]
    public void Both_decimal_marks_are_read_as_the_same_number(string text, double expected)
    {
        Assert.True(ContractedRate.TryParse(text, out var download, out _));
        Assert.Equal(expected, download);
    }

    /// <summary>
    /// Nothing typed is not a mistake: it means no contract was stated, and the measurement
    /// records honestly that it had nothing to compare against.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_field_means_no_contract_rather_than_an_error(string? text)
    {
        Assert.True(ContractedRate.TryParse(text, out var download, out var upload));

        Assert.Null(download);
        Assert.Null(upload);
    }

    [Theory]
    [InlineData("brzo")]
    [InlineData("100/20/5")]
    [InlineData("100/")]
    [InlineData("-100")]
    [InlineData("0")]
    [InlineData("100/x")]
    public void Anything_it_cannot_read_is_refused_rather_than_guessed_at(string text)
    {
        Assert.False(ContractedRate.TryParse(text, out var download, out var upload));

        Assert.Null(download);
        Assert.Null(upload);
    }

    [Fact]
    public void The_pair_is_written_back_the_way_it_was_entered()
    {
        Assert.Equal("100/20 Mbit/s", ContractedRate.Describe(100, 20));
        Assert.Equal("100 Mbit/s", ContractedRate.Describe(100, null));
        Assert.Equal("nije uneto", ContractedRate.Describe(null, null));
    }
}
