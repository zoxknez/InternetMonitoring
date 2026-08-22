using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// JSON null on a numeric field is how the writer records "not known". The reader must
/// treat it as absent, not as a failure to parse the record.
/// </summary>
public sealed class PayloadReaderTests
{
    [Fact]
    public void A_silent_traceroute_reads_back_without_an_answering_ttl()
    {
        var payload = new TracePayload(
            IncidentNumber: 4,
            Phase: "DuringOutage",
            TakenUtc: new DateTimeOffset(2026, 8, 21, 7, 11, 34, TimeSpan.Zero),
            Target: "1.1.1.1",
            ReachedTarget: false,
            PrivateHopCount: 0,
            FirstPublicHop: null,
            LastAnsweringTtl: null,
            StopsInsideHomeNetwork: false,
            Hops: [new TraceHop(1, null, null), new TraceHop(2, null, null)]);

        var json = EvidenceRoundTrip.Through(payload);
        var read = PayloadReader.Trace(json);

        Assert.NotNull(read);
        Assert.Null(read.LastAnsweringTtl);
        Assert.Null(read.FirstPublicHop);
        Assert.False(read.ReachedTarget);
        Assert.Equal(2, read.Hops.Count);
        Assert.Null(read.Hops[0].Address);
        Assert.Null(read.Hops[0].RoundTrip);
    }

    [Fact]
    public void A_session_with_unknown_link_speed_still_opens()
    {
        var payload = new SessionStartPayload(
            "S1", "3.0.0",
            new DateTimeOffset(2026, 8, 21, 6, 41, 27, TimeSpan.Zero),
            TimeSpan.FromHours(6),
            "TEST", "Wi-Fi", LinkMedium.Wireless, null, "192.168.1.1");

        var json = EvidenceRoundTrip.Through(payload);
        var read = PayloadReader.SessionStart(json);

        Assert.NotNull(read);
        Assert.Null(read.LinkSpeedBitsPerSecond);
    }

    [Fact]
    public void A_session_with_interface_id_reads_back_authoritative_id_and_schema4()
    {
        var payload = new SessionStartPayload(
            "S1", "3.0.0",
            new DateTimeOffset(2026, 8, 21, 6, 41, 27, TimeSpan.Zero),
            TimeSpan.FromHours(6),
            "TEST", "Wi-Fi", LinkMedium.Wireless, 100_000_000, "192.168.1.1",
            "{370E9134-7973-4017-BD92-CF72CB556DE4}");

        var json = EvidenceRoundTrip.Through(payload);
        var read = PayloadReader.SessionStart(json);

        Assert.NotNull(read);
        Assert.Equal("{370E9134-7973-4017-BD92-CF72CB556DE4}", read.InterfaceId);
        Assert.Equal(4, read.SchemaVersion);
    }
}
