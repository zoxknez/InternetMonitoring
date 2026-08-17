using IEM.Core;
using IEM.Core.Model;
using IEM.Core.Scheduling;
using IEM.Evidence;
using IEM.Storage;
using IEM.Storage.Evidence;

namespace IEM.Core.Tests;

/// <summary>
/// A report asked for while the session is still running.
/// <para>
/// The console offers it, the window has a button for it, and somebody two days into a test
/// has every reason to look at what they have. Until this was fixed the document they got
/// said "Stvarno nadzirano 0,0 s" and "Dostupnost 100 %" over a table listing hundreds of
/// samples and every recorded pause - because every total in the summary row is written
/// once, at the end, and an unfinished session has not reached that point.
/// </para>
/// </summary>
public sealed class OpenSessionReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iem-open", Guid.NewGuid().ToString("N"));

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

    private static IReadOnlyList<ProbeCycle> OutageScript()
    {
        var healthy = CycleBuilder.Wired().Build();
        var down = CycleBuilder.Wired().AllExternalFail().Build();
        return [healthy, healthy, down, down, down, healthy, healthy, healthy];
    }

    /// <summary>Records a session and optionally closes it, exactly as the service does.</summary>
    private async Task<SessionPaths> RecordAsync(
        bool complete,
        bool checkpoints = true,
        TimeSpan? wallClockJump = null)
    {
        var clock = new ManualClock();
        var step = TimeSpan.FromSeconds(1);
        var source = new ScriptedProbeSource(clock, OutageScript(), step);

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
        var paths = SessionPaths.ForNewSession(_root, DateTimeOffset.Now);

        var start = new SessionStartPayload(
            "S1", "2.2.0", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1),
            "TEST-PC", "Ethernet 4", LinkMedium.Ethernet, 1_000_000_000, "192.168.1.1");

        // Checkpoints fire on real elapsed time, and this fixture drives a simulated clock
        // through ninety samples in milliseconds - so with the shipped interval none is ever
        // written and the totals under test never exist. Zero makes every sample anchor its
        // running totals, which is the path being exercised.
        var recording = checkpoints
            ? new RecorderOptions
            {
                FirstCheckpointDelay = TimeSpan.Zero,
                CheckpointInterval = TimeSpan.Zero,
            }
            : new RecorderOptions
            {
                // Far beyond anything this fixture reaches, so the derivation is exercised
                // rather than the anchor.
                FirstCheckpointDelay = TimeSpan.FromDays(1),
                CheckpointInterval = TimeSpan.FromDays(1),
            };

        using (var recorder = EvidenceRecorder.Start(paths, engine, start, recording))
        {
            await engine.RunAsync(step * 45, CancellationToken.None);

            // An NTP correction or somebody changing the clock: wall time moves, the
            // monotonic counter does not.
            if (wallClockJump is { } jump)
            {
                clock.JumpWallClock(jump);
            }

            await engine.RunAsync(step * 45, CancellationToken.None);

            if (complete)
            {
                recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);
            }
        }

        return paths;
    }

    /// <summary>
    /// The figures a running session reports have to be the ones it measured, not the
    /// column defaults. Monitored time above zero is the whole point: availability is a
    /// fraction of it, and at zero the percentage means nothing.
    /// </summary>
    [Fact]
    public async Task An_unfinished_session_reports_what_it_has_measured()
    {
        var paths = await RecordAsync(complete: false);
        var result = EvidencePackage.Build(paths);

        Assert.True(result.Verification.Valid);

        var text = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Rezime.txt"));

        Assert.DoesNotContain("Stvarno nadzirano:      0,0 s", text, StringComparison.Ordinal);
        Assert.Contains("Kraj:                   nije završeno", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A session with recorded outages must not read as a perfect connection just because it
    /// has not been closed yet. This is the failure that mattered: the number a person would
    /// copy into a complaint was the one contradicting their own evidence.
    /// </summary>
    [Fact]
    public async Task An_unfinished_session_with_outages_does_not_claim_full_availability()
    {
        var paths = await RecordAsync(complete: false);
        EvidencePackage.Build(paths);

        var summary = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Rezime.txt"));

        Assert.NotEqual("0", Value(summary, "Prekida ukupno:"));
        Assert.NotEqual("0,0 s", Value(summary, "Stvarno nadzirano:"));
        Assert.NotEqual("100 %", Value(summary, "Dostupnost:"));
    }

    /// <summary>
    /// The derivation has to agree with the engine, or an unfinished report and a finished
    /// one would tell different stories about the same measurements. Same script, same
    /// number of samples: the only difference is whether the closing entry was written.
    /// </summary>
    [Fact]
    public async Task The_derived_totals_agree_with_the_ones_the_engine_writes()
    {
        var open = await RecordAsync(complete: false);
        var closed = await RecordAsync(complete: true);

        EvidencePackage.Build(open);
        EvidencePackage.Build(closed);

        var a = await File.ReadAllTextAsync(Path.Combine(open.Directory, "Rezime.txt"));
        var b = await File.ReadAllTextAsync(Path.Combine(closed.Directory, "Rezime.txt"));

        // The counts and the downtime come from the same incident rows either way, so these
        // have to match exactly rather than approximately.
        Assert.Equal(Value(b, "Prekida ukupno:"), Value(a, "Prekida ukupno:"));
        Assert.Equal(Value(b, "Od toga kod operatera:"), Value(a, "Od toga kod operatera:"));
        Assert.Equal(Value(b, "Nedostupnost operatera:"), Value(a, "Nedostupnost operatera:"));
        Assert.Equal(Value(b, "Lokalna nedostupnost:"), Value(a, "Lokalna nedostupnost:"));

        // Monitored time is reconstructed from the span the samples cover rather than
        // accumulated interval by interval, so it lands within a sample of the engine's.
        Assert.Equal(Value(b, "Stvarno nadzirano:"), Value(a, "Stvarno nadzirano:"));
        Assert.Equal(Value(b, "Dostupnost:"), Value(a, "Dostupnost:"));
    }

    /// <summary>A closed session keeps the engine's own totals, untouched by the derivation.</summary>
    [Fact]
    public async Task A_finished_session_still_reports_the_engines_own_totals()
    {
        var paths = await RecordAsync(complete: true);
        EvidencePackage.Build(paths);

        var summary = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Rezime.txt"));

        Assert.DoesNotContain("nije završeno", summary, StringComparison.Ordinal);
        Assert.NotEqual("0,0 s", Value(summary, "Stvarno nadzirano:"));
    }

    /// <summary>
    /// Without a checkpoint the totals still have to come from somewhere, and that somewhere
    /// is the monotonic counter.
    /// <para>
    /// Sessions recorded in bursts shorter than the checkpoint interval never write one, and
    /// neither did any session recorded before checkpoints were read back at all. A real
    /// nine-hour session on the author's machine was made of forty-second runs and had none.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Totals_are_derived_when_the_session_never_wrote_a_checkpoint()
    {
        var paths = await RecordAsync(complete: false, checkpoints: false);
        EvidencePackage.Build(paths);

        var summary = await File.ReadAllTextAsync(Path.Combine(paths.Directory, "Rezime.txt"));

        Assert.NotEqual("0,0 s", Value(summary, "Stvarno nadzirano:"));
        Assert.NotEqual("0", Value(summary, "Prekida ukupno:"));
    }

    /// <summary>
    /// The invariant the whole clock design rests on, checked at the one place that had
    /// broken it.
    /// <para>
    /// <c>IClock</c> records wall-clock time for display and forbids deriving a duration
    /// from it, because an NTP correction moves it. The first version of this derivation
    /// took the span between the first and last sample timestamps - so a clock correction
    /// mid-session would have moved monitored time, and with it the availability figure
    /// that goes into the complaint. Same session, same samples, one shifted by an hour:
    /// the figures must not move.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_clock_correction_does_not_move_the_monitored_time()
    {
        var steady = await RecordAsync(complete: false, checkpoints: false);
        var jumped = await RecordAsync(complete: false, checkpoints: false, wallClockJump: TimeSpan.FromHours(1));

        EvidencePackage.Build(steady);
        EvidencePackage.Build(jumped);

        var a = await File.ReadAllTextAsync(Path.Combine(steady.Directory, "Rezime.txt"));
        var b = await File.ReadAllTextAsync(Path.Combine(jumped.Directory, "Rezime.txt"));

        Assert.Equal(Value(a, "Stvarno nadzirano:"), Value(b, "Stvarno nadzirano:"));
        Assert.Equal(Value(a, "Ukupno vreme:"), Value(b, "Ukupno vreme:"));
        Assert.Equal(Value(a, "Dostupnost:"), Value(b, "Dostupnost:"));
    }

    /// <summary>Reads a labelled figure out of the summary the report writes.</summary>
    private static string Value(string summary, string label)
    {
        var line = summary
            .Split(Environment.NewLine, StringSplitOptions.None)
            .FirstOrDefault(l => l.TrimStart().StartsWith(label, StringComparison.Ordinal));

        Assert.NotNull(line);
        return line.Trim()[label.Length..].Trim();
    }
}
