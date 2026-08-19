using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IEM.Linux.Time;
using IEM.Service.Linux.Lifecycle.Logind;

namespace IEM.TimeRunner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var mode = args.Length > 0 ? args[0] : "all";

            if (mode == "time" || mode == "all")
            {
                // 1. Production provider instantiation
                var provider = new LinuxTimeObservationProvider();

                // 2. Authoritative boot observation
                var bobs = provider.CaptureBootObservation();

                // 3. Clock sample
                var sample = provider.CaptureClockSample(bobs.BootInstanceId);

                // 4. Time sync provenance via adjtimex
                var provenance = provider.CaptureTimeSyncProvenance();

                // 5. Test Modes=0 enforcement with caller write flag
                var timex = new LinuxTimex { Modes = 0x0001 }; // Caller sets ADJ_OFFSET
                var adj = new LinuxAdjtimex();
                var queryRes = adj.Query(ref timex);

                var timeOutput = new
                {
                    bootInstanceId = bobs.BootInstanceId,
                    bootIdentityBasis = bobs.BootIdentityBasis,
                    capturedUtc = bobs.CapturedUtc,
                    monotonicTimestamp = sample.MonotonicTimestamp,
                    monotonicFrequency = sample.MonotonicFrequency,
                    bootElapsed = sample.BootElapsedIncludingSuspend.TotalSeconds,
                    activeElapsed = sample.ActiveElapsedExcludingSuspend.TotalSeconds,
                    adjtimexAvailable = provenance.Available,
                    rawKernelState = provenance.RawKernelState,
                    rawStatusFlags = provenance.RawStatusFlags,
                    unsynchronized = provenance.Unsynchronized,
                    modesEnforcedZero = timex.Modes == 0,
                    modesQuerySuccess = queryRes >= 0,
                    queryResult = queryRes,
                    frequencyPpm = provenance.FrequencyPpm,
                    taiOffset = provenance.TaiOffsetSeconds
                };

                Console.WriteLine("IEM_TIME_PROVENANCE_JSON=" + JsonSerializer.Serialize(timeOutput));
            }

            if (mode == "logind" || mode == "all")
            {
                bool logindSuccess = false;
                string? logindError = null;

                try
                {
                    var transport = new TmdsLogindSignalTransport();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var readyTcs = new TaskCompletionSource<bool>();

                    var observeTask = transport.ObservePrepareForSleepAsync(
                        _ => ValueTask.CompletedTask,
                        onReady: () => readyTcs.TrySetResult(true),
                        cancellationToken: cts.Token);

                    var completed = await Task.WhenAny(readyTcs.Task, observeTask);
                    if (completed == readyTcs.Task && readyTcs.Task.Result)
                    {
                        logindSuccess = true;
                    }
                    cts.Cancel();
                    await transport.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logindError = ex.Message;
                }

                var logindOutput = new
                {
                    logindAvailable = logindSuccess,
                    error = logindError
                };

                Console.WriteLine("IEM_LOGIND_JSON=" + JsonSerializer.Serialize(logindOutput));
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return 1;
        }
    }
}
