using IEM.Core.Model;
using IEM.Core.Speed;
using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// Where a completed measurement is filed, and which session may claim it.
/// <para>
/// The question used to be "which folder is newest", which is not the same question at all. A
/// machine that ran a session last week and none since has a newest folder that was sealed,
/// checksummed and very possibly already emailed to an operator - and today's measurement was
/// being written into it, leaving the package describing a folder it no longer matched.
/// </para>
/// </summary>
public sealed class MeasurementFilingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"iem-filing-{Guid.NewGuid():N}");

    public MeasurementFilingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test run over.
        }
    }

    [Fact]
    public void A_sealed_session_never_takes_another_file()
    {
        var session = Session("Sesija_20260810_090000", sealedUp: true);

        Assert.Null(SessionPaths.FindOpen(_root));

        // Still the newest folder; that is exactly the answer that was wrong.
        Assert.Equal(session, SessionPaths.FindLatest(_root)!.Directory);
    }

    [Fact]
    public void An_open_session_is_where_the_measurement_goes()
    {
        var session = Session("Sesija_20260810_090000", sealedUp: false);

        Assert.Equal(session, SessionPaths.FindOpen(_root)!.Directory);
    }

    [Fact]
    public void A_folder_without_evidence_in_it_is_not_a_session()
    {
        // Created and abandoned before anything was recorded - a crash between making the
        // directory and writing the first entry. Nothing may be filed there.
        Directory.CreateDirectory(Path.Combine(_root, "Sesija_20260810_090000"));

        Assert.Null(SessionPaths.FindOpen(_root));
    }

    [Fact]
    public void The_newest_open_session_wins_over_an_older_one()
    {
        Session("Sesija_20260810_090000", sealedUp: false);
        var newer = Session("Sesija_20260812_090000", sealedUp: false);

        Assert.Equal(newer, SessionPaths.FindOpen(_root)!.Directory);
    }

    [Fact]
    public void A_sealed_session_hides_the_open_one_beneath_it()
    {
        // Deliberate: the older session is open only because it was abandoned, and a newer
        // sealed package means the machine has moved on. Filing into the abandoned one would
        // put today's measurement into a session that stopped recording days ago.
        Session("Sesija_20260810_090000", sealedUp: false);
        Session("Sesija_20260812_090000", sealedUp: true);

        Assert.Null(SessionPaths.FindOpen(_root));
    }

    private string Session(string name, bool sealedUp)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SirovaEvidencija.jsonl"), "{}\n");

        if (sealedUp)
        {
            File.WriteAllText(Path.Combine(directory, SessionPaths.ChecksumFileName), "hash\n");
        }

        return directory;
    }
}

/// <summary>
/// What the report says about a measurement that was not taken during the session it is
/// filed with.
/// </summary>
public sealed class MeasurementOutsideSessionTests
{
    private static readonly DateTimeOffset Started = new(2026, 8, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = new(2026, 8, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_measurement_taken_during_the_session_needs_no_explanation()
    {
        Assert.Null(SpeedText.OutsideSession(Started.AddHours(5), Started, Ended));
    }

    [Fact]
    public void A_measurement_taken_before_the_session_says_so()
    {
        var text = SpeedText.OutsideSession(Started.AddMinutes(-3), Started, Ended);

        Assert.NotNull(text);
        Assert.Contains("pre početka", text, StringComparison.Ordinal);
        Assert.Contains("ne period koji sesija pokriva", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measurement_taken_after_the_session_says_so()
    {
        var text = SpeedText.OutsideSession(Ended.AddMinutes(3), Started, Ended);

        Assert.NotNull(text);
        Assert.Contains("posle završetka", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_still_running_has_no_end_to_fall_outside_of()
    {
        Assert.Null(SpeedText.OutsideSession(Started.AddDays(9), Started, endedUtc: null));
    }
}
