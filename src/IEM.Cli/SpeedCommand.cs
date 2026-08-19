using IEM.Core.Model;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Core.Speed;
using IEM.Storage;
using IEM.Windows;

namespace IEM.Cli;

/// <summary>
/// Measures download speed and says whether the result could stand behind a complaint.
/// <para>
/// The judgement matters more than the number. A figure measured over Wi-Fi, or while the
/// machine was downloading something else, is worse than no figure at all - the operator
/// only has to ask what else was running, and the rest of the report goes with it. So the
/// conditions are checked first and stated alongside the result either way.
/// </para>
/// </summary>
public static class SpeedCommand
{
    /// <summary>How long the queue waits by default when the link is busy.</summary>
    public static readonly TimeSpan DefaultQueueTimeout = TimeSpan.FromMinutes(10);

    /// <param name="contractedUploadMbps">Contracted sending rate, when the user supplied one.</param>
    /// <param name="measureUpload">
    /// False leaves the sending half out. It exists for a metered connection, where the
    /// second transfer doubles what the measurement costs in data.
    /// </param>
    public static async Task<bool> RunAsync(
        double? contractedMbps,
        string? interfaceName,
        TimeSpan? queueTimeout = null,
        string? outputRoot = null,
        TimeSpan? startDelay = null,
        double? contractedUploadMbps = null,
        bool measureUpload = true)
    {
        Console.WriteLine();
        Console.WriteLine("  MERENJE BRZINE");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine();

        // The link is inspected after any delay, not before: the adapter state that matters
        // is the one at the moment of measuring, not the one when the command was typed.
        if (startDelay is { } delay && delay > TimeSpan.Zero && !await WaitUntilRunAsync(delay).ConfigureAwait(false))
        {
            return false;
        }

        await using var linkInspection = WindowsLinkInspection.Create(interfaceName);
        var link = linkInspection.Inspector.Inspect();

        Console.WriteLine($"  Adapter: {link.InterfaceName} ({Medium(link.Medium)})");

        if (link.LinkSpeedBitsPerSecond is { } bits)
        {
            Console.WriteLine($"  Port:    {bits / 1_000_000d:0.###} Mbit/s");
        }

        Console.WriteLine();

        if (link.Medium != LinkMedium.Ethernet)
        {
            WriteWarning("  Veza nije preko kabla. Merenje će se izvršiti, ali NE MOŽE se koristiti");
            WriteWarning("  za dokazivanje ugovorene brzine.");
            Console.WriteLine();
        }

        // Deliberately no proxy: Windows proxy auto-detection adds silent latency before the
        // transfer, and an intercepting proxy silently caps the rate. A measurement that
        // quietly went through one is worse than none, and a network that only reaches the
        // internet through a proxy refuses the measurement honestly instead.
        // Records every connection it opens, so the figure can say which adapter actually
        // carried it rather than which one the route table predicted.
        var observer = new ConnectionObserver();
        using var httpClient = MeasurementHttpClient.Create(observer);

        // The round-trip probes travel on a client of their own. Sharing the one carrying
        // fifty megabytes would have them queue behind it inside this process, and the delay
        // the measurement exists to attribute to the connection would be partly ours.
        using var latencyClient = MeasurementHttpClient.Create();

        var activity = new LinkActivityMonitor();
        var measurement = new ThroughputMeasurement(
            httpClient,
            activity,
            ThroughputOptions.Default with { MeasureUpload = measureUpload },
            clock: null,
            new LoadedLatencySampler(latencyClient));

        var wait = queueTimeout ?? DefaultQueueTimeout;

        // "beskonacno" arrives as a timespan one tick below zero that means forever, not a
        // duration; treating it as one would put the deadline in the past and end the wait
        // before it began.
        var endless = wait == Timeout.InfiniteTimeSpan;

        Console.WriteLine(wait > TimeSpan.Zero
            ? $"  Čeka se da veza bude mirna (najduže {SerbianText.Duration(wait)})…"
            : endless
                ? "  Čeka se da veza bude mirna (bez roka)…"
                : "  Proverava se da li je veza mirna…");

        var quiet = await WaitForQuietAsync(measurement, activity, link.InterfaceId, wait).ConfigureAwait(false);

        if (!quiet)
        {
            WriteWarning("  Veza je i dalje zauzeta. Zatvorite preuzimanja, video pozive i striming,");
            WriteWarning($"  pa ponovite - ili pustite da program čeka duže: --cekaj 30m");
            Console.WriteLine();
            return false;
        }

        // Whether the transfer will actually leave through the adapter named above. The
        // probes of a monitoring session are pinned to their link; a speed measurement is
        // not, so on a machine with a docking station or a VPN the figure can describe one
        // adapter while being labelled with another.
        //
        // Asked here rather than before the queue, for the same reason the adapter itself is
        // inspected late: after ten minutes of waiting for a quiet link, the routing that
        // matters is the one at the moment of measuring.
        var route = MeasurementPath.Resolve(link.InterfaceId);

        if (route.State != MeasurementRouteState.AllResolvedRoutesMatch)
        {
            WriteWarning($"  Putanja merenja: {route.Describe()}.");
            WriteWarning("  Merenje se izvršava i biće zapisano, ali se ne može pripisati ovoj vezi.");
            Console.WriteLine();
        }

        Console.WriteLine("  Merenje u toku…");
        Console.WriteLine();

        // Held around the transfer only, not around the queue: while we wait for the link to
        // go quiet the connection is genuinely idle and a session beside us is measuring
        // something real. From here on it is not - we are the load.
        using var marker = MeasurementMarker.Hold(outputRoot);

        var result = await measurement.MeasureAsync(connectionHealthy: true, CancellationToken.None)
                                      .ConfigureAwait(false);

        if (!result.Ran)
        {
            WriteWarning($"  Merenje nije izvršeno: {Describe(result.Refusal)}");
            Console.WriteLine();
            return false;
        }

        Console.WriteLine($"  Preuzimanje: {result.DownloadMbps:0.##} Mbit/s");

        if (result.UploadMbps is { } upload)
        {
            Console.WriteLine($"  Slanje:      {upload:0.##} Mbit/s");
        }

        var transferred = (result.BytesTransferred + result.UploadBytes) / 1_000_000d;
        Console.WriteLine($"  Preneseno:   {transferred:0.#} MB za {SerbianText.Duration(result.Duration)} po smeru");
        Console.WriteLine();

        var conditions = new SpeedMeasurementConditions(
            link.Medium, link.LinkSpeedBitsPerSecond, contractedMbps, result.DownloadMbps)
        {
            LinkWasIdle = true,
            ConnectionHealthy = true,

            // Checked rather than assumed, and an unresolved check stays unresolved. It used
            // to fall back to "one path" - which is what this claimed unconditionally before
            // there was any check at all, dressed up as a verified finding.
            RouteState = route.State,
            ActualPath = PathAgreement.Of(link.InterfaceId, observer.Attempts),
            ContractedUploadMbps = contractedUploadMbps,
            MeasuredUploadMbps = result.UploadMbps,
        };

        var band = SpeedMeasurementValidity.Judge(conditions);

        if (band is { } judged)
        {
            Console.WriteLine($"  Ugovoreno preuzimanje: {contractedMbps:0.##} Mbit/s");
            Console.WriteLine($"  Ocena:                 {judged.Label()}");
            Console.WriteLine();
            Console.WriteLine($"  {judged.Explain()}");
            Console.WriteLine();
        }

        if (SpeedMeasurementValidity.JudgeUpload(conditions) is { } uploadBand)
        {
            Console.WriteLine($"  Ugovoreno slanje:      {contractedUploadMbps:0.##} Mbit/s");
            Console.WriteLine($"  Ocena:                 {uploadBand.UploadLabel()}");
            Console.WriteLine();
        }

        WriteLoadedLatency(result);

        // After the figure, not before it: this is the one path statement that describes what
        // happened rather than what the route table predicted, and it can only be made once
        // the connections have actually been opened.
        Console.WriteLine($"  Veze merenja: {conditions.ActualPath.Describe()}.");
        Console.WriteLine();

        var validity = SpeedMeasurementValidity.Of(conditions);

        if (validity.IsValidForComplaint)
        {
            Console.WriteLine("  Merenje ispunjava uslove za korišćenje uz prigovor.");
        }
        else
        {
            WriteWarning("  Merenje NE ispunjava uslove za dokazivanje ugovorene brzine:");
            Console.WriteLine();

            foreach (var defect in validity.Defects)
            {
                Console.WriteLine($"    • {defect.Explain()}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Uslovi koje program ne može da proveri, pa ih proverite sami:");

        foreach (var item in SpeedText.Checklist)
        {
            Console.WriteLine($"    • {item}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {SpeedText.OfficialMeasurementNote}");
        Console.WriteLine();

        SaveNote(link, conditions, result, outputRoot);

        return validity.IsValidForComplaint;
    }

    /// <summary>
    /// What the connection did to everything else while it was busy.
    /// <para>
    /// Printed apart from the speed because it is a separate complaint: a line that reaches
    /// its contracted rate can still be useless for calls and games, and until now this was
    /// the one thing the regulators' tools measured that this one could not say at all.
    /// </para>
    /// </summary>
    private static void WriteLoadedLatency(ThroughputResult result)
    {
        if (result.IdleLatency is not { } idle || result.LatencyIncreaseUnderLoad is not { } increase)
        {
            return;
        }

        Console.WriteLine("  KAŠNJENJE POD OPTEREĆENJEM");
        Console.WriteLine($"    Veza mirna:       {idle.Median.TotalMilliseconds:0.#} ms");

        if (result.DownloadLoadedLatency is { } underDownload)
        {
            Console.WriteLine($"    Tokom preuzimanja: {underDownload.Median.TotalMilliseconds:0.#} ms");
        }

        if (result.UploadLoadedLatency is { } underUpload)
        {
            Console.WriteLine($"    Tokom slanja:      {underUpload.Median.TotalMilliseconds:0.#} ms");
        }

        var grade = LoadedLatency.Grade(increase);

        Console.WriteLine($"    Povećanje:         {increase.TotalMilliseconds:0.#} ms  ({grade.Label()})");
        Console.WriteLine();

        if (grade == LoadedLatencyGrade.Slight)
        {
            Console.WriteLine($"  {grade.Explain()}");
        }
        else
        {
            WriteWarning($"  {grade.Explain()}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Files the measurement beside the session it belongs to, so the report can state it
    /// with its verdict rather than leaving it behind on a console that will close.
    /// <para>
    /// The latest session is used however it ended: a measurement taken the day after a
    /// two-day test describes the same connection, and the report already carries its own
    /// dates for everything. With no session at all the note waits in the output folder,
    /// where the next session's report will not find it - and the message says so plainly
    /// rather than pretending it landed somewhere useful.
    /// </para>
    /// </summary>
    private static void SaveNote(
        LinkSnapshot link,
        SpeedMeasurementConditions conditions,
        ThroughputResult result,
        string? outputRoot)
    {
        try
        {
            var note = SpeedMeasurementNote.From(
                DateTimeOffset.UtcNow,
                link.Medium,
                link.LinkSpeedBitsPerSecond / 1_000_000d,
                conditions,
                result);

            if (outputRoot is null)
            {
                return;
            }

            // Beside the session only when one is open. The newest folder is not the same
            // question: it can be a session sealed and emailed last week, and this file would
            // land inside a package whose checksums no longer described it.
            var session = SessionPaths.FindOpen(outputRoot);
            var directory = session?.Directory ?? outputRoot;

            note.Write(directory);

            Console.WriteLine($"  Merenje je sačuvano: {Path.Combine(directory, SpeedMeasurementNote.FileName)}");

            if (session is null)
            {
                Console.WriteLine("  Nijedna sesija nije u toku, pa merenje stoji samostalno. Sesija koja");
                Console.WriteLine("  se pokrene posle ovoga preuzeće ga u svoj izveštaj i paket.");
            }

            Console.WriteLine();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A measurement that cannot be filed is still a measurement; the numbers above
            // stand on their own and the refusal to save must not eat the result.
            WriteWarning($"  Merenje nije moglo biti sačuvano uz sesiju: {ex.Message}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Parks the command until the chosen moment, then carries on with the measurement.
    /// <para>
    /// A connection whose speed dips at a particular hour is best measured at that hour, and
    /// nobody wants to sit by the console until then. The countdown says when the measurement
    /// starts, quietly - a line a minute for six hours is not information, it is noise - and
    /// Ctrl+C cancels the whole thing without having measured anything.
    /// </para>
    /// </summary>
    /// <returns>True when the waiting completed and measuring may proceed.</returns>
    private static async Task<bool> WaitUntilRunAsync(TimeSpan delay)
    {
        using var cancelled = new CancellationTokenSource();

        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancelled.Cancel();
        };

        Console.CancelKeyPress += handler;

        try
        {
            var startsAt = DateTimeOffset.Now + delay;
            Console.WriteLine($"  Merenje je zakačano: kreće u {startsAt:HH:mm} (za {SerbianText.Duration(delay)}).");
            Console.WriteLine("  Prekida se sa Ctrl+C.");
            Console.WriteLine();

            var started = DateTimeOffset.Now;
            var nextReport = started + TimeSpan.FromMinutes(10);

            while (!cancelled.IsCancellationRequested)
            {
                var elapsed = DateTimeOffset.Now - started;

                if (elapsed >= delay)
                {
                    Console.WriteLine("  Vreme je. Merenje kreće.");
                    Console.WriteLine();
                    return true;
                }

                // Quiet for the long stretch, a line a minute for the last five - that is when
                // someone watching for the moment actually wants to see it move.
                var left = delay - elapsed;
                var due = left <= TimeSpan.FromMinutes(5) || DateTimeOffset.Now >= nextReport;

                if (due)
                {
                    if (left > TimeSpan.FromMinutes(5))
                    {
                        nextReport = DateTimeOffset.Now + TimeSpan.FromMinutes(10);
                    }

                    Console.WriteLine($"  Do merenja: {SerbianText.Duration(left)}  (kreće u {startsAt:HH:mm})");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancelled.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Console.WriteLine();
            Console.WriteLine("  Zakazano merenje je otkazano. Ništa nije mereno ni sačuvano.");
            Console.WriteLine();
            return false;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>
    /// Stands in the queue until the link has been quiet long enough, or the patience runs out.
    /// <para>
    /// The person measuring for a complaint has already decided they want the measurement;
    /// "try again later" is not an answer for them. So instead of giving up after a moment,
    /// the command waits its turn: it watches the link, says what it sees, and measures the
    /// moment the connection goes quiet. Giving up remains a legitimate outcome, but only
    /// once the caller-chosen patience has actually run out - a machine that is genuinely
    /// busy for an evening should not hold a console hostage forever.
    /// </para>
    /// </summary>
    /// <param name="activity">Read directly, so the counters are sampled once per tick.</param>
    /// <param name="maxWait">How long to stand in the queue. Zero checks and gives up at once.</param>
    private static async Task<bool> WaitForQuietAsync(
        ThroughputMeasurement measurement,
        LinkActivityMonitor activity,
        string interfaceId,
        TimeSpan maxWait)
    {
        var deadline = maxWait == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + maxWait;
        var nextReport = DateTimeOffset.MinValue;

        while (DateTimeOffset.UtcNow <= deadline)
        {
            // The reading is taken once and handed over, rather than letting the measurement
            // sample again behind our backs: the counters belong to this loop's tick.
            var reading = activity.Sample(interfaceId);
            measurement.Observe(reading);

            if (measurement.ReadyToMeasure)
            {
                return true;
            }

            if (reading is not null && DateTimeOffset.UtcNow >= nextReport)
            {
                nextReport = DateTimeOffset.UtcNow.AddSeconds(10);

                var left = deadline - DateTimeOffset.UtcNow;
                var patience = maxWait == Timeout.InfiniteTimeSpan
                    ? "bez roka"
                    : SerbianText.Duration(left > TimeSpan.Zero ? left : TimeSpan.Zero);

                Console.WriteLine(reading.IsIdle
                    ? $"  Veza je mirna ({measurement.IdleFor?.TotalSeconds:0}s od 10s). Čeka se…"
                    : $"  Veza je zauzeta ({reading.BytesPerSecond / 1000d:0.#} KB/s). Čeka se da utihne " +
                      $"({patience} strpljenja)…");
            }

            if (deadline - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(1))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        return measurement.ReadyToMeasure;
    }

    private static string Describe(ThroughputRefusal refusal) => refusal.Explain();


    private static string Medium(LinkMedium medium) => medium switch
    {
        LinkMedium.Ethernet => "kabl",
        LinkMedium.Wireless => "Wi-Fi",
        _ => "nepoznat tip",
    };

    private static void WriteWarning(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
