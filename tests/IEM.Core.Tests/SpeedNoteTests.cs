using System.Text.Json;
using IEM.Core.Model;
using IEM.Core.Speed;

namespace IEM.Core.Tests;

/// <summary>
/// The note is where a measurement becomes a record, and it is built in one place so the
/// console, the window and the service cannot disagree about what the same figures mean.
/// They used to each assemble it by hand, and the window's copy filed every measurement it
/// ever took as unusable.
/// </summary>
public sealed class SpeedNoteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-speed-note", Guid.NewGuid().ToString("N"));

    public SpeedNoteTests() => Directory.CreateDirectory(_root);

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

    private static ThroughputResult Result() =>
        new(94.3, 523_000_000, TimeSpan.FromSeconds(10), ThroughputRefusal.None)
        {
            UploadMbps = 18.2,
            UploadBytes = 22_000_000,
            UploadDuration = TimeSpan.FromSeconds(10),
            IdleLatency = LatencyReading.From([TimeSpan.FromMilliseconds(12)]),
            DownloadLoadedLatency = LatencyReading.From([TimeSpan.FromMilliseconds(30)]),
            UploadLoadedLatency = LatencyReading.From([TimeSpan.FromMilliseconds(212)]),
        };

    private static SpeedMeasurementConditions Wired() =>
        new(LinkMedium.Ethernet, 1_000_000_000, 100, 94.3)
        {
            ContractedUploadMbps = 20,
            MeasuredUploadMbps = 18.2,
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
        };

    private static SpeedMeasurementNote Build(
        SpeedMeasurementConditions? conditions = null,
        ThroughputResult? result = null) =>
        SpeedMeasurementNote.From(
            new DateTimeOffset(2026, 8, 17, 10, 30, 0, TimeSpan.FromHours(2)),
            LinkMedium.Ethernet,
            linkSpeedMbps: 1000,
            conditions ?? Wired(),
            result ?? Result());

    [Fact]
    public void A_clean_wired_measurement_is_recorded_as_usable_with_both_verdicts()
    {
        var note = Build();

        Assert.True(note.ValidForComplaint);
        Assert.Empty(note.Defects);
        Assert.Equal(SpeedBand.AtAdvertised.Label(), note.BandLabel);
        Assert.Equal(SpeedBand.AtAdvertised.UploadLabel(), note.UploadBandLabel);
    }

    /// <summary>
    /// The window used to build this by hand and always wrote "invalid". Taken over a cable,
    /// on a quiet link, with the contract stated, the same measurement is usable - and the
    /// window has no business reaching a different conclusion than the console.
    /// </summary>
    [Fact]
    public void The_verdict_comes_from_the_conditions_rather_than_from_the_caller()
    {
        var overWifi = Build(Wired() with { Medium = LinkMedium.Wireless });

        Assert.False(overWifi.ValidForComplaint);
        Assert.Contains(overWifi.Defects, defect => defect.Contains("Wi-Fi", StringComparison.Ordinal));
    }

    /// <summary>
    /// Zero would read as a line that cannot send at all, which is a serious finding; what
    /// actually happened is that the sending half did not run.
    /// </summary>
    [Fact]
    public void A_measurement_without_the_sending_half_records_it_as_missing_not_as_nought()
    {
        var downloadOnly = new ThroughputResult(94.3, 523_000_000, TimeSpan.FromSeconds(10), ThroughputRefusal.None);

        var note = Build(Wired() with { MeasuredUploadMbps = null }, downloadOnly);

        Assert.Null(note.UploadMbps);
        Assert.Null(note.UploadBandLabel);
        Assert.Equal(0, note.UploadBytesTransferred);
    }

    [Fact]
    public void The_worse_direction_decides_the_latency_figure_that_is_recorded()
    {
        var note = Build();

        // 212 ms while sending against a 12 ms idle line: 200 ms, and the receiving
        // direction's milder 18 ms does not soften it.
        Assert.Equal(200, note.LatencyIncreaseMs);
        Assert.Equal(LoadedLatencyGrade.Severe.Label(), note.LoadedLatencyLabel);
        Assert.Equal(12, note.IdleLatencyMs);
    }

    [Fact]
    public void A_measurement_with_no_latency_sampling_records_no_latency_figures()
    {
        var bare = new ThroughputResult(94.3, 523_000_000, TimeSpan.FromSeconds(10), ThroughputRefusal.None);

        var note = Build(result: bare);

        Assert.Null(note.IdleLatencyMs);
        Assert.Null(note.LatencyIncreaseMs);
        Assert.Null(note.LoadedLatencyLabel);
    }

    // ---- Reading and writing ------------------------------------------------------

    [Fact]
    public void Everything_written_reads_back_identically()
    {
        var note = Build();
        note.Write(_root);

        var read = SpeedMeasurementNote.Read(_root);

        Assert.NotNull(read);
        Assert.Equal(note.UploadMbps, read.UploadMbps);
        Assert.Equal(note.ContractedUploadMbps, read.ContractedUploadMbps);
        Assert.Equal(note.UploadBandLabel, read.UploadBandLabel);
        Assert.Equal(note.IdleLatencyMs, read.IdleLatencyMs);
        Assert.Equal(note.LatencyUnderDownloadMs, read.LatencyUnderDownloadMs);
        Assert.Equal(note.LatencyUnderUploadMs, read.LatencyUnderUploadMs);
        Assert.Equal(note.LatencyIncreaseMs, read.LatencyIncreaseMs);
        Assert.Equal(note.LoadedLatencyLabel, read.LoadedLatencyLabel);
    }

    /// <summary>
    /// A note written by an earlier build has to keep reading. Evidence packages are kept for
    /// months while a complaint runs its course, and a report that refused to state a
    /// measurement because the file predates a new field would be losing evidence to a
    /// version number.
    /// </summary>
    [Fact]
    public void A_note_written_before_the_sending_half_existed_still_reads()
    {
        var legacy = JsonSerializer.Serialize(new
        {
            MeasuredAtUtc = new DateTimeOffset(2026, 8, 15, 10, 30, 0, TimeSpan.FromHours(2)),
            Medium = "Ethernet",
            LinkSpeedMbps = 1000d,
            ContractedMbps = 100d,
            DownloadMbps = 94.3,
            BytesTransferred = 523_000_000L,
            Duration = "00:00:10",
            ValidForComplaint = true,
            BandLabel = "na nivou oglašene",
            Defects = Array.Empty<string>(),
        });

        File.WriteAllText(Path.Combine(_root, SpeedMeasurementNote.FileName), legacy);

        var read = SpeedMeasurementNote.Read(_root);

        Assert.NotNull(read);
        Assert.Equal(94.3, read.DownloadMbps);
        Assert.True(read.ValidForComplaint);

        // Absent, not zero: the older measurement never took these readings.
        Assert.Null(read.UploadMbps);
        Assert.Null(read.LatencyIncreaseMs);
    }
}
