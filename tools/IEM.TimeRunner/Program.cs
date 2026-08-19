using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Time;
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
                Console.Out.Flush();
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
                Console.Out.Flush();
            }

            if (mode == "suspend-observe")
            {
                var provider = new LinuxTimeObservationProvider();

                // 1. Capture Pre-Suspend Baseline
                var bobsPre = provider.CaptureBootObservation();
                var samplePre = provider.CaptureClockSample(bobsPre.BootInstanceId);

                var transport = new TmdsLogindSignalTransport();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

                bool sleepTrueReceived = false;
                DateTimeOffset? sleepTrueUtc = null;
                bool sleepFalseReceived = false;
                DateTimeOffset? sleepFalseUtc = null;

                var readyTcs = new TaskCompletionSource<bool>();
                var resumeTcs = new TaskCompletionSource<bool>();

                var observeTask = transport.ObservePrepareForSleepAsync(
                    prepare =>
                    {
                        if (prepare)
                        {
                            sleepTrueReceived = true;
                            sleepTrueUtc = DateTimeOffset.UtcNow;
                            Console.Error.WriteLine("[IEM.TimeRunner] Received PrepareForSleep(true)");
                        }
                        else
                        {
                            sleepFalseReceived = true;
                            sleepFalseUtc = DateTimeOffset.UtcNow;
                            Console.Error.WriteLine("[IEM.TimeRunner] Received PrepareForSleep(false)");
                            resumeTcs.TrySetResult(true);
                        }
                        return ValueTask.CompletedTask;
                    },
                    onReady: () =>
                    {
                        readyTcs.TrySetResult(true);
                        var status = ReadProcStatus();
                        var readyInfo = new
                        {
                            pid = Environment.ProcessId,
                            uid = status.Uid,
                            gid = status.Gid,
                            groups = status.Groups,
                            capEff = status.CapEff,
                            capAmb = status.CapAmb
                        };
                        Console.WriteLine("IEM_SUSPEND_LISTENER_READY=true");
                        Console.WriteLine("IEM_SUSPEND_READY_JSON=" + JsonSerializer.Serialize(readyInfo));
                        Console.Out.Flush();
                    },
                    cancellationToken: cts.Token);

                // Wait for D-Bus listener to be ready
                var readyCompleted = await Task.WhenAny(readyTcs.Task, Task.Delay(5000, cts.Token));
                if (readyCompleted != readyTcs.Task || !readyTcs.Task.Result)
                {
                    Console.Error.WriteLine("FATAL: Logind signal transport failed to reach ready state within 5s");
                    return 2;
                }

                // Wait for resume signal (PrepareForSleep(false)) or timeout
                var resumeCompleted = await Task.WhenAny(resumeTcs.Task, Task.Delay(45000, cts.Token));
                if (resumeCompleted != resumeTcs.Task)
                {
                    Console.Error.WriteLine("FATAL: Timeout waiting for PrepareForSleep(false) after suspend");
                }

                // 2. Capture Post-Suspend Baseline
                var bobsPost = provider.CaptureBootObservation();
                var samplePost = provider.CaptureClockSample(bobsPost.BootInstanceId);

                // 3. Core Continuity Evaluations
                var policy = new TimeContinuityPolicy { SuspendDetectionTolerance = TimeSpan.FromSeconds(1) };
                var transResult = TimeContinuityEvaluator.EvaluateTransition(samplePre, samplePost, policy);
                var bootResult = TimeContinuityEvaluator.EvaluateBoot(bobsPre, bobsPost, policy);

                bool success = bootResult.State == BootContinuityState.Continued &&
                               transResult.State == ClockContinuityState.SuspendIntervalObserved &&
                               sleepTrueReceived &&
                               sleepFalseReceived;

                var finalStatus = ReadProcStatus();
                var output = new
                {
                    success,
                    processEvidence = new
                    {
                        pid = Environment.ProcessId,
                        uid = finalStatus.Uid,
                        gid = finalStatus.Gid,
                        groups = finalStatus.Groups,
                        capEff = finalStatus.CapEff,
                        capAmb = finalStatus.CapAmb
                    },
                    sleepTrueReceived,
                    sleepTrueUtc,
                    sleepFalseReceived,
                    sleepFalseUtc,
                    bootInstanceIdPre = bobsPre.BootInstanceId,
                    bootInstanceIdPost = bobsPost.BootInstanceId,
                    bootContinuityState = bootResult.State.ToString(),
                    clockContinuityState = transResult.State.ToString(),
                    suspendDurationSeconds = transResult.SuspendDuration.TotalSeconds,
                    monotonicElapsedSeconds = transResult.MonotonicDelta.TotalSeconds,
                    wallClockElapsedSeconds = transResult.WallClockDelta.TotalSeconds,
                    bootElapsedDeltaSeconds = transResult.BootElapsedDelta.TotalSeconds,
                    activeElapsedDeltaSeconds = transResult.ActiveElapsedDelta.TotalSeconds,
                    capturedUtcPre = samplePre.CapturedUtc,
                    capturedUtcPost = samplePost.CapturedUtc
                };

                Console.WriteLine("IEM_SUSPEND_ACCEPTANCE_JSON=" + JsonSerializer.Serialize(output));
                Console.Out.Flush();

                cts.Cancel();
                await transport.DisposeAsync();

                return success ? 0 : 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: " + ex);
            return 1;
        }
    }

    private static (string Uid, string Gid, string Groups, string CapEff, string CapAmb) ReadProcStatus()
    {
        string uid = "", gid = "", groups = "", capEff = "0000000000000000", capAmb = "0000000000000000";
        try
        {
            if (File.Exists("/proc/self/status"))
            {
                foreach (var line in File.ReadAllLines("/proc/self/status"))
                {
                    if (line.StartsWith("Uid:", StringComparison.OrdinalIgnoreCase))
                        uid = line.Substring(4).Trim();
                    else if (line.StartsWith("Gid:", StringComparison.OrdinalIgnoreCase))
                        gid = line.Substring(4).Trim();
                    else if (line.StartsWith("Groups:", StringComparison.OrdinalIgnoreCase))
                        groups = line.Substring(7).Trim();
                    else if (line.StartsWith("CapEff:", StringComparison.OrdinalIgnoreCase))
                        capEff = line.Substring(7).Trim();
                    else if (line.StartsWith("CapAmb:", StringComparison.OrdinalIgnoreCase))
                        capAmb = line.Substring(7).Trim();
                }
            }
        }
        catch { }
        return (uid, gid, groups, capEff, capAmb);
    }
}
