using IEM.App.Controls;
using IEM.Core;
using IEM.Core.Model;

namespace IEM.App.ViewModels;

/// <summary>
/// Rolling history behind the two charts.
/// <para>
/// Samples arrive as often as ten times a second during an incident, which is exactly
/// when the detail matters least on screen: nobody reads a hundred-millisecond column.
/// So arrivals are folded into fixed slices of session time, keeping the worst severity
/// and the extremes of latency in each. Averaging instead would smooth away the spikes,
/// and drawing every sample would make the strip scroll faster the worse things got.
/// </para>
/// </summary>
public sealed class LiveHistory(int capacity = 600)
{
    private readonly int _capacity = capacity;
    private readonly List<TimelineSlice> _slices = [];
    private readonly List<LatencyPoint> _latency = [];

    private TimeSpan _sliceWidth = TimeSpan.FromSeconds(1);
    private long _currentSlice = -1;
    private Severity _sliceSeverity = Severity.Ok;
    private double? _sliceMin;
    private double? _sliceMax;
    private double _sliceSum;
    private int _sliceCount;

    public IReadOnlyList<TimelineSlice> Slices => _slices;

    public IReadOnlyList<LatencyPoint> Latency => _latency;

    public int Capacity => _capacity;

    /// <summary>
    /// Widens the slice so a whole planned session fits without scrolling.
    /// <para>
    /// A two-day test at one-second slices would need a hundred and seventy thousand
    /// columns for a strip a thousand pixels wide. Sizing the slice to the plan means the
    /// picture on screen matches the one in the exported report.
    /// </para>
    /// </summary>
    public void PlanFor(TimeSpan? plannedDuration)
    {
        _sliceWidth = plannedDuration is { } planned && planned > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Max(1d, planned.TotalSeconds / _capacity))
            : TimeSpan.FromSeconds(1);
    }

    public void Clear()
    {
        _slices.Clear();
        _latency.Clear();
        _currentSlice = -1;
        ResetSlice();
    }

    /// <summary>
    /// Replaces the history with what a session has already recorded.
    /// <para>
    /// Someone who opens the window an hour into a test wants to see the hour, not a
    /// blank chart that starts the moment they happened to look. The session's own index
    /// holds all of it, already bucketed, so the picture on screen matches the one the
    /// report will produce.
    /// </para>
    /// </summary>
    /// <param name="buckets">Pre-bucketed history, oldest first.</param>
    /// <param name="elapsed">Session time covered, used to keep incoming samples aligned.</param>
    public void Load(IReadOnlyList<(double? Min, double? Average, double? Max, bool Outage, bool Degraded)> buckets,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        Clear();

        foreach (var bucket in buckets)
        {
            var severity = bucket.Outage
                ? Severity.Outage
                : bucket.Degraded ? Severity.Degraded : Severity.Ok;

            _slices.Add(new TimelineSlice(severity));
            _latency.Add(new LatencyPoint(bucket.Min, bucket.Average, bucket.Max));
        }

        while (_slices.Count > _capacity)
        {
            _slices.RemoveAt(0);
            _latency.RemoveAt(0);
        }

        // Continue from where the loaded history ends, so the next arriving sample extends
        // the picture instead of starting a second one beside it.
        _currentSlice = _sliceWidth > TimeSpan.Zero ? elapsed.Ticks / _sliceWidth.Ticks : 0;
    }

    /// <summary>Folds one snapshot in. Returns true when the visible history changed.</summary>
    public bool Add(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var slice = (long)(snapshot.Elapsed.Ticks / _sliceWidth.Ticks);

        if (_currentSlice < 0)
        {
            _currentSlice = slice;
        }

        var advanced = false;

        // A resumed session can jump several slices at once, and the gap between them is
        // time nobody observed. Filling it with the last known state would draw a healthy
        // green stretch across a period when the monitor was not even running.
        while (slice > _currentSlice)
        {
            Commit();
            _currentSlice++;
            advanced = true;

            if (slice > _currentSlice)
            {
                Append(new TimelineSlice(Severity.Info), new LatencyPoint(null, null, null));
            }
        }

        Accumulate(snapshot);

        // The slice still being filled is shown as it accumulates, so the strip moves
        // while a session runs rather than jumping once a minute on a long test.
        return advanced || UpdateOpenSlice();
    }

    private void Accumulate(MonitorSnapshot snapshot)
    {
        var severity = snapshot.CurrentState.SeverityOf();
        if (severity > _sliceSeverity)
        {
            _sliceSeverity = severity;
        }

        if (snapshot.CurrentLatency is not { } latency)
        {
            return;
        }

        var value = latency.TotalMilliseconds;
        _sliceMin = _sliceMin is { } min ? Math.Min(min, value) : value;
        _sliceMax = _sliceMax is { } max ? Math.Max(max, value) : value;
        _sliceSum += value;
        _sliceCount++;
    }

    private bool UpdateOpenSlice()
    {
        if (_slices.Count == 0)
        {
            Append(CurrentSlice(), CurrentLatency());
            return true;
        }

        var slice = CurrentSlice();
        var latency = CurrentLatency();

        if (_slices[^1] == slice && _latency[^1] == latency)
        {
            return false;
        }

        _slices[^1] = slice;
        _latency[^1] = latency;
        return true;
    }

    private void Commit()
    {
        if (_slices.Count == 0)
        {
            Append(CurrentSlice(), CurrentLatency());
        }
        else
        {
            _slices[^1] = CurrentSlice();
            _latency[^1] = CurrentLatency();
        }

        ResetSlice();
        Append(new TimelineSlice(Severity.Ok), new LatencyPoint(null, null, null));
    }

    private TimelineSlice CurrentSlice() => new(_sliceSeverity);

    private LatencyPoint CurrentLatency() =>
        _sliceCount == 0
            ? new LatencyPoint(null, null, null)
            : new LatencyPoint(_sliceMin, _sliceSum / _sliceCount, _sliceMax);

    private void Append(TimelineSlice slice, LatencyPoint latency)
    {
        _slices.Add(slice);
        _latency.Add(latency);

        // Drop from the front once full. An open-ended session would otherwise grow
        // without limit for as long as someone leaves it running.
        while (_slices.Count > _capacity)
        {
            _slices.RemoveAt(0);
            _latency.RemoveAt(0);
        }
    }

    private void ResetSlice()
    {
        _sliceSeverity = Severity.Ok;
        _sliceMin = null;
        _sliceMax = null;
        _sliceSum = 0;
        _sliceCount = 0;
    }
}
