using System.Globalization;
using IEM.Cli;
using IEM.Core;
using IEM.Core.Presentation;
using IEM.Core.Probes;
using IEM.Storage;
using IEM.Windows;
using IEM.Storage.Evidence;

// The application is Serbian regardless of the language of the user's Windows. Without
// this, dates in an evidence report would come out as 8/13/2026 with a decimal point on
// an English install, which is not something to discover after a 48-hour run.
CultureInfo.DefaultThreadCurrentCulture = SerbianText.Culture;
CultureInfo.DefaultThreadCurrentUICulture = SerbianText.Culture;
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (!CommandLine.TryParse(args, out var settings, out var error))
{
    Console.Error.WriteLine(error);
    Console.WriteLine();
    CommandLine.PrintUsage();
    return 2;
}

if (settings.ShowHelp)
{
    CommandLine.PrintUsage();
    return 0;
}

if (settings.VerifyDirectory is { } directoryToVerify)
{
    // Non-zero on a broken chain, so this is usable from a script as well as by hand.
    return VerifyCommand.Run(directoryToVerify) ? 0 : 1;
}

if (settings.ReportDirectory is { } directoryToReport)
{
    return ReportCommand.Run(directoryToReport) ? 0 : 1;
}

if (settings.ComplaintDirectory is { } directoryToComplainAbout)
{
    return ComplaintCommand.Run(directoryToComplainAbout, settings.OperatorName, settings.OutputRoot) ? 0 : 1;
}

if (settings.ShowCase ||
    settings.ComplaintSubmitted is not null ||
    settings.OperatorResponded is not null ||
    settings.OperatorUpheld is not null ||
    settings.RegulatorDirectory is not null)
{
    return CaseCommand.Run(settings);
}

if (settings.MeasureSpeed)
{
    // Non-zero when the measurement cannot support a complaint, so a script can tell a
    // usable figure from one taken under conditions the operator would dismiss.
    return await SpeedCommand.RunAsync(
        settings.ContractedDownloadMbps,
        settings.InterfaceName,
        settings.SpeedQueueTimeout,
        settings.OutputRoot,
        settings.SpeedStartDelay,
        settings.ContractedUploadMbps,
        settings.MeasureUpload) ? 0 : 1;
}

if (settings.ShowWireless)
{
    return await WifiCommand.RunAsync(settings.InterfaceName) ? 0 : 1;
}

if (settings.ShowPaths)
{
    // Non-zero when traffic leaves through more than one adapter, so a script can refuse
    // to start a long test in a state where its result could not attribute anything.
    return PathCommand.Run(settings.PathTarget) ? 0 : 1;
}

var options = MonitorOptions.Default;

// Wireless detail comes through the Windows layer. Without it a dropped Wi-Fi adapter
// cannot be told apart from a router that stopped broadcasting.
await using var linkInspection = WindowsLinkInspection.Create(settings.InterfaceName);
var linkInspector = linkInspection.Inspector;

// Routing is resolved per destination and probes are pinned to the source address it
// reports, so every measurement records which link actually carried it.
// The last argument: while a speed measurement is deliberately loading the line - this
// process or another one - the period is excluded from assessment rather than recorded as
// the delay and loss it certainly is.
MeasurementMarker.Clear(settings.OutputRoot);

await using var probeSource = new NetworkProbeSource(
    options.Probes,
    linkInspector,
    clock: null,
    new RouteResolver(),
    BoundPing.Instance,
    () => MeasurementMarker.IsHeld(settings.OutputRoot));

var engine = new MonitorEngine(probeSource, options);
var reporter = new ConsoleReporter(engine, settings);

var startedAt = DateTimeOffset.Now;
var link = linkInspector.Inspect();

EvidenceRecorder? recorder = null;

// Takes a trace at each incident boundary, off the sampling path. This is what turns
// "nothing answered" into "the path stopped at the operator's edge".
await using var tracer = new IncidentPathTracer();

if (!settings.NoRecording)
{
    var paths = SessionPaths.ForNewSession(settings.OutputRoot, startedAt);

    var start = new SessionStartPayload(
        SessionId: $"S{startedAt.ToLocalTime():yyyyMMddHHmmss}",
        ToolVersion: typeof(CommandLine).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        StartedUtc: startedAt.ToUniversalTime(),
        PlannedDuration: settings.Duration,
        MachineName: Environment.MachineName,
        InterfaceName: link.InterfaceName,
        Medium: link.Medium,
        LinkSpeedBitsPerSecond: link.LinkSpeedBitsPerSecond,
        GatewayAddress: link.GatewayAddress);

    recorder = EvidenceRecorder.Start(paths, engine, start);

    // Traces run off the sampling path and land in the chain when they finish.
    tracer.TraceCompleted += recorder.RecordTrace;
    tracer.Attach(engine);
}

using var stopSignal = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    // Take the shutdown into our own hands so the session is closed cleanly and the
    // evidence collected so far is summarised rather than thrown away.
    eventArgs.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("  Zaustavljanje... zapisi do sada ostaju sačuvani.");
    stopSignal.Cancel();
};

reporter.PrintHeader(link, recorder?.Paths);

try
{
    await engine.RunAsync(settings.Duration, stopSignal.Token);
}
finally
{
    // Closing the session is what writes the totals and the integrity check, so it has to
    // happen even if the run ended badly.
    if (recorder is not null)
    {
        var sessionPaths = recorder.Paths;
        var verification = recorder.Complete(engine.Statistics, DateTimeOffset.UtcNow);

        // Release the database before the report reads it back.
        recorder.Dispose();

        reporter.PrintSummary();
        ConsoleReporter.PrintEvidenceSummary(sessionPaths, verification);
        ReportCommand.BuildAndPrint(sessionPaths);
    }
    else
    {
        reporter.PrintSummary();
    }
}

return 0;
