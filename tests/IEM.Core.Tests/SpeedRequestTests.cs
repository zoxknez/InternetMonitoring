using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// Scheduling exists for one case: measure at three in the morning, when the line is quiet
/// and nobody is awake to press a button. A schedule that lives inside a window or a console
/// cannot serve that case, so the instruction is a file on disk and the service carries it
/// out - which puts the rules about when it is still worth carrying out here, where they can
/// be tested without waiting until three in the morning.
/// </summary>
public sealed class SpeedRequestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-speed-request", Guid.NewGuid().ToString("N"));

    public SpeedRequestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Not worth failing a test over a leftover temp directory.
        }
    }

    private static readonly DateTimeOffset ThreeInTheMorning =
        new(2026, 8, 18, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Everything_asked_for_survives_the_round_trip()
    {
        var request = new SpeedRequest(ThreeInTheMorning, 100, 20, "Ethernet", MeasureUpload: false);
        request.Write(_root);

        var read = SpeedRequest.Read(_root);

        Assert.Equal(request, read);
    }

    [Fact]
    public void With_nothing_scheduled_there_is_no_request()
    {
        Assert.Null(SpeedRequest.Read(_root));
    }

    /// <summary>
    /// A corrupt instruction is treated as none at all, exactly as for sessions: guessing at
    /// what someone meant and then saturating their connection on that guess would be worse.
    /// </summary>
    [Fact]
    public void An_unreadable_request_is_treated_as_no_request()
    {
        File.WriteAllText(SpeedRequest.PathFor(_root), "{ nije json");

        Assert.Null(SpeedRequest.Read(_root));
    }

    [Fact]
    public void Clearing_removes_the_instruction()
    {
        new SpeedRequest(ThreeInTheMorning).Write(_root);
        SpeedRequest.Clear(_root);

        Assert.Null(SpeedRequest.Read(_root));
        Assert.False(File.Exists(SpeedRequest.PathFor(_root)));
    }

    [Fact]
    public void Clearing_nothing_is_not_an_error()
    {
        SpeedRequest.Clear(_root);
    }

    // ---- When it is due, and when it stopped being worth doing --------------------

    [Fact]
    public void It_is_due_from_its_moment_onward()
    {
        var request = new SpeedRequest(ThreeInTheMorning);

        Assert.False(request.IsDue(ThreeInTheMorning - TimeSpan.FromMinutes(1)));
        Assert.True(request.IsDue(ThreeInTheMorning));
        Assert.True(request.IsDue(ThreeInTheMorning + TimeSpan.FromMinutes(10)));
    }

    /// <summary>
    /// A machine switched off overnight comes back to an instruction whose moment passed
    /// hours ago. Measuring then would file a midday figure against a request that meant
    /// three in the morning - and the hour was the entire reason for scheduling it.
    /// </summary>
    [Fact]
    public void An_instruction_whose_moment_passed_long_ago_is_no_longer_worth_carrying_out()
    {
        var request = new SpeedRequest(ThreeInTheMorning);

        Assert.False(request.IsStale(ThreeInTheMorning + TimeSpan.FromMinutes(30)));
        Assert.False(request.IsStale(ThreeInTheMorning + SpeedRequest.Grace));
        Assert.True(request.IsStale(ThreeInTheMorning + SpeedRequest.Grace + TimeSpan.FromMinutes(1)));
    }

    /// <summary>
    /// Stored as an absolute moment rather than as a delay, so an hour spent restarting does
    /// not restart the countdown with it.
    /// </summary>
    [Fact]
    public void The_moment_is_absolute_rather_than_counted_from_when_it_was_written()
    {
        new SpeedRequest(ThreeInTheMorning).Write(_root);

        var read = SpeedRequest.Read(_root);

        Assert.NotNull(read);
        Assert.Equal(ThreeInTheMorning, read.DueAtUtc);
        Assert.Equal(TimeSpan.Zero, read.DueAtUtc.Offset);
    }

    [Fact]
    public void Rewriting_replaces_the_instruction_rather_than_queueing_another()
    {
        new SpeedRequest(ThreeInTheMorning, 100).Write(_root);
        new SpeedRequest(ThreeInTheMorning.AddHours(2), 200).Write(_root);

        var read = SpeedRequest.Read(_root);

        Assert.NotNull(read);
        Assert.Equal(ThreeInTheMorning.AddHours(2), read.DueAtUtc);
        Assert.Equal(200, read.ContractedDownloadMbps);
    }

    /// <summary>The sending half is measured unless it was deliberately left out.</summary>
    [Fact]
    public void The_sending_half_is_measured_by_default()
    {
        Assert.True(new SpeedRequest(ThreeInTheMorning).MeasureUpload);
    }
}
