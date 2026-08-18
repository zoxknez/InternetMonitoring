using System.Text.Json;
using IEM.Core.Model;
using IEM.Core.Scheduling;
using IEM.Core.Speed;
using IEM.Evidence;
using IEM.Legal;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// Writes the frozen snapshot of what this version produces, into <c>baseline/</c>.
/// <para>
/// Not a test - a tool, kept here because it needs the fixtures the tests already have. It
/// runs only when <c>IEM_WRITE_BASELINE=1</c>, because its output is committed and reviewed
/// rather than regenerated: a snapshot that rewrites itself on every run cannot catch the
/// thing it exists to catch.
/// </para>
/// <para>
/// The session is real - a real recorder, a real hash chain, real reports - and contains no
/// private data at all. The probe cycles are the synthetic ones the fault-catalogue tests use,
/// so the machine is "TEST-PC" and the addresses are the documentation ranges. That is the
/// only way to have both: an artefact that is genuinely what the program writes, and one that
/// can sit in a public repository.
/// </para>
/// </summary>
public sealed class BaselineSnapshotWriter
{
    private const string Trigger = "IEM_WRITE_BASELINE";

    [Fact]
    public async Task Write_the_snapshot_of_this_version()
    {
        if (Environment.GetEnvironmentVariable(Trigger) != "1")
        {
            return;
        }

        var target = Path.Combine(BaselineSnapshot.Root, "sesija");
        var scratch = Path.Combine(Path.GetTempPath(), $"iem-baseline-{Guid.NewGuid():N}");

        Directory.CreateDirectory(scratch);

        try
        {
            var paths = await RecordAsync(scratch);

            WriteSpeedNote(paths.Directory);
            WriteCaseJournal(paths.Directory);

            // Built last, so the checksums cover the two findings beside the session.
            EvidencePackage.Build(paths);

            Publish(paths.Directory, target);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    /// <summary>
    /// A session with one outage in it, long enough for the shared verdict to conclude
    /// anything at all.
    /// </summary>
    private static async Task<SessionPaths> RecordAsync(string root)
    {
        var clock = new ManualClock();
        var step = TimeSpan.FromSeconds(1);

        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();

        var source = new ScriptedProbeSource(
            clock,
            [healthy, healthy, down, down, down, healthy, healthy, healthy],
            step);

        var options = MonitorOptions.Default with
        {
            Cadence = new CadenceOptions
            {
                StableInterval = TimeSpan.FromMilliseconds(1),
                SuspectInterval = TimeSpan.FromMilliseconds(1),
                BurstInterval = TimeSpan.FromMilliseconds(1),
                IncidentInterval = TimeSpan.FromMilliseconds(1),
                RecoveryInterval = TimeSpan.FromMilliseconds(1),
                RecoveryHold = TimeSpan.FromSeconds(2),
            },
        };

        var engine = new MonitorEngine(source, options, clock);
        var paths = SessionPaths.ForNewSession(root, new DateTimeOffset(2026, 8, 18, 6, 0, 0, TimeSpan.Zero));

        var start = new SessionStartPayload(
            "S20260818060000",
            EvidenceModelVersion.ClassifierVersion,
            new DateTimeOffset(2026, 8, 18, 6, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(2),
            "TEST-PC", "Ethernet", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1");

        using (var recorder = EvidenceRecorder.Start(paths, engine, start))
        {
            await engine.RunAsync(step * 90, CancellationToken.None);
            recorder.Complete(engine.Statistics, new DateTimeOffset(2026, 8, 18, 6, 2, 0, TimeSpan.Zero));
        }

        return paths;
    }

    /// <summary>A measurement this version would write: with its schema version and its route state.</summary>
    private static void WriteSpeedNote(string directory)
    {
        var conditions = new SpeedMeasurementConditions(LinkMedium.Ethernet, 1_000_000_000, 100, 94.3)
        {
            ContractedUploadMbps = 20,
            MeasuredUploadMbps = 18.2,
            RouteState = MeasurementRouteState.AllResolvedRoutesMatch,
        };

        var result = new ThroughputResult(94.3, 523_000_000, TimeSpan.FromSeconds(10), ThroughputRefusal.None)
        {
            UploadMbps = 18.2,
            UploadBytes = 22_000_000,
            UploadDuration = TimeSpan.FromSeconds(10),
            IdleLatency = LatencyReading.From([TimeSpan.FromMilliseconds(12)]),
            DownloadLoadedLatency = LatencyReading.From([TimeSpan.FromMilliseconds(30)]),
            UploadLoadedLatency = LatencyReading.From([TimeSpan.FromMilliseconds(212)]),
        };

        SpeedMeasurementNote
            .From(new DateTimeOffset(2026, 8, 18, 6, 1, 0, TimeSpan.Zero), LinkMedium.Ethernet, 1000, conditions, result)
            .Write(directory);
    }

    /// <summary>A case this version would write: schema 2, with the legal context frozen into it.</summary>
    private static void WriteCaseJournal(string directory)
    {
        var complaint = new ComplaintCase
        {
            OperatorName = "Primer Telekom",
            SubscriberName = "____________________",
            EventDate = new DateOnly(2026, 8, 18),
            EventOrigin = FactOrigin.DerivedFromSession,
            EventEvidenceRef = "prekid-1",
            SubmittedDate = new DateOnly(2026, 8, 20),
        };

        CaseJournalStore.Save(
            directory,
            new CaseJournal { Case = complaint },
            new DateOnly(2026, 8, 20));
    }

    /// <summary>
    /// Copies the session into the repository, leaving out the archive - it is the same bytes
    /// again, and a hundred megabytes of them.
    /// </summary>
    private static void Publish(string session, string target)
    {
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }

        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(session))
        {
            // SQLite's write-ahead journal and shared-memory file are scaffolding, not
            // evidence: they exist while the database is open and say nothing afterwards.
            if (file.EndsWith("-wal", StringComparison.Ordinal) ||
                file.EndsWith("-shm", StringComparison.Ordinal))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        // The manifest the characterization tests read, so a file quietly disappearing from
        // the snapshot fails a test rather than shrinking the check.
        File.WriteAllText(
            Path.Combine(target, BaselineSnapshot.ManifestFile),
            JsonSerializer.Serialize(
                Directory.EnumerateFiles(target).Select(Path.GetFileName).Order(StringComparer.Ordinal),
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing over.
        }
    }
}
