using IEM.Core.Model;
using IEM.Evidence;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// Every report says which reasoning produced its conclusions - and it has to be the
/// reasoning the session was recorded under, not whichever version happens to be installed
/// when somebody rebuilds the report.
/// <para>
/// Rebuilding a report from an untouched chain years later is a deliberate feature: a session
/// cut short by a power cut still has intact evidence, and getting a document out of it must
/// not mean repeating a two-day test. Printing today's version numbers over that old session
/// defeats the one thing these numbers exist for - telling a discrepancy apart from a changed
/// algorithm.
/// </para>
/// </summary>
public sealed class ModelVersionReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-versions", Guid.NewGuid().ToString("N"));

    public ModelVersionReportTests() => Directory.CreateDirectory(_root);

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

    private static SessionSnapshot Session() => new(
        "S1",
        new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 15, 8, 0, 0, TimeSpan.Zero),
        "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1",
        MonitoredTime: TimeSpan.FromHours(48),
        GapTime: TimeSpan.Zero,
        UpstreamDowntime: TimeSpan.Zero,
        LocalDowntime: TimeSpan.Zero,
        AvailabilityPercent: 100,
        UpstreamAvailabilityPercent: 100,
        SampleCount: 100,
        Incidents: [],
        Gaps: [],
        Latency: [],
        Traces: []);

    private string RenderHtml(SessionSnapshot session)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.html");
        HtmlReportBuilder.Write(path, session, "abc123", true);
        return File.ReadAllText(path);
    }

    [Fact]
    public void The_report_states_the_versions_the_session_was_recorded_under()
    {
        var html = RenderHtml(Session() with
        {
            SchemaVersion = 2,
            ClassifierVersion = "2.1.0",
            AttributionModelVersion = "1.9",
            ConfidenceModelVersion = "1.0",
        });

        Assert.Contains("format zapisa 2", html, StringComparison.Ordinal);
        Assert.Contains("klasifikacija 2.1.0", html, StringComparison.Ordinal);
        Assert.Contains("model pripisivanja 1.9", html, StringComparison.Ordinal);
        Assert.Contains("model pouzdanosti 1.0", html, StringComparison.Ordinal);

        // And not this build's numbers, which is the whole failure being fixed.
        Assert.DoesNotContain(
            $"format zapisa {EvidenceModelVersion.SchemaVersion}", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// An index written before the versions were stored has nothing to say about them, so the
    /// report falls back to the current ones rather than printing a blank.
    /// </summary>
    [Fact]
    public void An_index_that_never_recorded_them_falls_back_to_the_current_ones()
    {
        var html = RenderHtml(Session());

        Assert.Contains($"format zapisa {EvidenceModelVersion.SchemaVersion}", html, StringComparison.Ordinal);
        Assert.Contains(
            $"klasifikacija {EvidenceModelVersion.ClassifierVersion}", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The versions travel with the session through the chain, which is what makes the index
    /// disposable: rebuilt from the raw log, it comes back with the old session's numbers.
    /// </summary>
    [Fact]
    public void The_versions_survive_the_round_trip_through_the_chain()
    {
        var recorded = new SessionStartPayload(
            "S1", "2.3.0", DateTimeOffset.UtcNow, TimeSpan.FromHours(48),
            "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1")
        {
            SchemaVersion = 2,
            ClassifierVersion = "2.1.0",
            AttributionModelVersion = "1.9",
            ConfidenceModelVersion = "1.0",
        };

        var read = PayloadReader.SessionStart(EvidenceRoundTrip.Through(recorded));

        Assert.NotNull(read);
        Assert.Equal(2, read.SchemaVersion);
        Assert.Equal("2.1.0", read.ClassifierVersion);
        Assert.Equal("1.9", read.AttributionModelVersion);
        Assert.Equal("1.0", read.ConfidenceModelVersion);
    }

    /// <summary>A session opened now records what this build actually applied.</summary>
    [Fact]
    public void A_new_session_records_this_builds_versions()
    {
        var payload = new SessionStartPayload(
            "S1", "2.5.0", DateTimeOffset.UtcNow, TimeSpan.FromHours(48),
            "PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1");

        Assert.Equal(EvidenceModelVersion.SchemaVersion, payload.SchemaVersion);
        Assert.Equal(EvidenceModelVersion.ClassifierVersion, payload.ClassifierVersion);
        Assert.Equal(EvidenceModelVersion.AttributionModelVersion, payload.AttributionModelVersion);
        Assert.Equal(EvidenceModelVersion.ConfidenceModelVersion, payload.ConfidenceModelVersion);
    }
}
