using System.Reflection;
using System.Text.Json;
using IEM.Core.Model;
using IEM.Core.Probes;
using IEM.Service.Runtime;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

public sealed class InterfacePinningCoreTests
{
    [Fact]
    public void WIN_ARCH_01_Runtime_Must_Not_Reference_Windows_Assembly()
    {
        var runtimeAssembly = typeof(MonitorWorker).Assembly;
        var referencedAssemblies = runtimeAssembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, a => a.Name?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void WIN_SCHEMA_01_Schema4_Persists_InterfaceId_And_Legacy_Fallback_Is_1()
    {
        Assert.Equal(4, EvidenceModelVersion.SchemaVersion);
        Assert.Equal(1, EvidenceModelVersion.LegacySchemaVersion);

        var payload = new SessionStartPayload(
            "S20260822120000",
            "1.0.0",
            DateTimeOffset.UtcNow,
            TimeSpan.FromHours(1),
            "DESKTOP-TEST",
            "Wi-Fi",
            LinkMedium.Wireless,
            100_000_000,
            "192.168.1.1",
            "{370E9134-7973-4017-BD92-CF72CB556DE4}");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            payload.WriteTo(writer);
            writer.WriteEndObject();
        }

        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("{370E9134-7973-4017-BD92-CF72CB556DE4}", root.GetProperty("interfaceId").GetString());
        Assert.Equal("Wi-Fi", root.GetProperty("interface").GetString());
        Assert.Equal(4, root.GetProperty("schemaVersion").GetInt32());

        // Legacy reading test without schemaVersion
        var legacyJson = "{\"sessionId\":\"S1\",\"toolVersion\":\"0.9\",\"startedUtc\":\"2026-08-20T00:00:00Z\",\"plannedDuration\":\"01:00:00\",\"machine\":\"M\",\"interface\":\"Wi-Fi\",\"medium\":\"Wireless\"}";
        using var legacyDoc = JsonDocument.Parse(legacyJson);
        var parsedLegacy = PayloadReader.SessionStart(legacyDoc.RootElement);

        Assert.NotNull(parsedLegacy);
        Assert.Equal(1, parsedLegacy.SchemaVersion);
        Assert.Null(parsedLegacy.InterfaceId);
    }

    [Fact]
    public void WIN_IF_03_Preferred_Interface_Temporarily_Absent_Returns_Missing_Never_Falls_Back()
    {
        var pinnedId = "{NON-EXISTENT-GUID-9999-AAAA}";
        var inspector = new SystemLinkInspector(pinnedId, "NonExistentNic");

        var snapshot = inspector.Inspect();

        Assert.Equal(LinkStatus.Missing, snapshot.Status);
        Assert.False(snapshot.IsUp);
        Assert.Equal(pinnedId, snapshot.InterfaceId);
    }

    [Fact]
    public void WIN_IF_15_External_Route_Cannot_Failover_To_Different_Interface()
    {
        // Test that ProbeScheduler path coherence check skips probes if route belongs to different interface
        var link = new LinkSnapshot("Wi-Fi", "{370E9134-7973-4017-BD92-CF72CB556DE4}", LinkStatus.Up, LinkMedium.Wireless);
        var mismatchedPath = new ProbePath("{9A98AF38-98FB-11F1-B19A-6C6A775452CC}", "192.168.1.50", Resolved: true);

        // Reflect internal IsPathCoherent
        var method = typeof(ProbeScheduler).GetMethod("IsPathCoherent", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var isCoherent = (bool)method.Invoke(null, [mismatchedPath, link])!;
        Assert.False(isCoherent);

        var matchingPath = new ProbePath("{370E9134-7973-4017-BD92-CF72CB556DE4}", "172.17.70.242", Resolved: true);
        var isCoherentMatching = (bool)method.Invoke(null, [matchingPath, link])!;
        Assert.True(isCoherentMatching);
    }
}
