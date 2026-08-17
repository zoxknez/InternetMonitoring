using IEM.Storage;

namespace IEM.Core.Tests;

/// <summary>
/// The mark that says "this tool is deliberately filling the line right now".
/// <para>
/// It is a file because the measurement and the monitoring are usually different processes -
/// the service monitors while the console measures, or the window measures while the service
/// monitors. Everything here is about the two failure directions: a mark that outlives the
/// measurement would silence a two-day test, and a mark that never arrives puts our own
/// transfer into the evidence as the operator's fault.
/// </para>
/// </summary>
public sealed class MeasurementMarkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-marker", Guid.NewGuid().ToString("N"));

    public MeasurementMarkerTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void Nothing_is_marked_until_a_measurement_holds_it()
    {
        Assert.False(MeasurementMarker.IsHeld(_root));
    }

    [Fact]
    public void A_held_mark_is_visible_to_another_reader_and_gone_after_release()
    {
        using (MeasurementMarker.Hold(_root))
        {
            Assert.True(MeasurementMarker.IsHeld(_root));
        }

        Assert.False(MeasurementMarker.IsHeld(_root));
        Assert.False(File.Exists(MeasurementMarker.PathFor(_root)));
    }

    /// <summary>
    /// A process killed mid-measurement cannot clean up after itself, and a mark left behind
    /// would suspend assessment for the rest of the test - a safeguard turned into a session
    /// that records nothing and reports that all was well.
    /// </summary>
    [Fact]
    public void A_mark_left_behind_stops_being_honoured()
    {
        // Held for a moment that has already passed: the same file an abandoned process
        // leaves behind, once its ceiling has run out.
        MeasurementMarker.Hold(_root, TimeSpan.FromMilliseconds(-1));

        Assert.True(File.Exists(MeasurementMarker.PathFor(_root)));
        Assert.False(MeasurementMarker.IsHeld(_root));
    }

    [Fact]
    public void Clearing_removes_a_mark_whoever_left_it()
    {
        MeasurementMarker.Hold(_root, TimeSpan.FromMinutes(5));
        MeasurementMarker.Clear(_root);

        Assert.False(MeasurementMarker.IsHeld(_root));
    }

    /// <summary>
    /// Silencing the monitoring on the strength of a file we cannot read would be the wrong
    /// way round: unreadable means unknown, and unknown means carry on measuring.
    /// </summary>
    [Fact]
    public void An_unreadable_mark_is_treated_as_absent()
    {
        File.WriteAllText(MeasurementMarker.PathFor(_root), "{ nije json");

        Assert.False(MeasurementMarker.IsHeld(_root));
    }

    /// <summary>
    /// A measurement with nowhere to write the mark still measures. The cost is the old
    /// behaviour - a session beside it records the load - not a failed measurement.
    /// </summary>
    [Fact]
    public void With_no_folder_to_write_into_the_mark_is_simply_not_taken()
    {
        using var handle = MeasurementMarker.Hold(null);

        Assert.False(MeasurementMarker.IsHeld(null));
    }

    [Fact]
    public void Releasing_twice_is_not_an_error()
    {
        var handle = MeasurementMarker.Hold(_root);
        handle.Dispose();
        handle.Dispose();

        Assert.False(MeasurementMarker.IsHeld(_root));
    }
}
