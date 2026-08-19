using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IEM.Core.Hosting;
using IEM.Service.Linux.Lifecycle;
using IEM.Service.Linux.Lifecycle.Logind;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IEM.Core.Tests;

/// <summary>
/// Deterministic tests for Phase 3.1-6B-R1 LinuxLogindPowerSource.
/// Verifies D-Bus signal mapping, subscriber isolation, backoff delay progression, onReady reset, and lifecycle cleanup.
/// </summary>
public sealed class LinuxLogindPowerSourceTests
{
    [Fact]
    public async Task Signal_true_invokes_only_suspend_subscribers_exactly_once()
    {
        var fakeTransport = new FakeLogindSignalTransport();
        using var source = new LinuxLogindPowerSource(
            NullLogger<LinuxLogindPowerSource>.Instance,
            () => fakeTransport);

        var suspendCount = 0;
        var resumeCount = 0;

        using var sSub = source.OnSuspending(() => suspendCount++);
        using var rSub = source.OnResumed(() => resumeCount++);

        using var cts = new CancellationTokenSource();
        var runTask = source.StartAsync(cts.Token);

        await fakeTransport.WaitForStartAsync();
        await fakeTransport.EmitPrepareForSleepAsync(true);

        Assert.Equal(1, suspendCount);
        Assert.Equal(0, resumeCount);

        await source.StopAsync(CancellationToken.None);
        await runTask;
    }

    [Fact]
    public async Task Signal_false_invokes_only_resume_subscribers_exactly_once()
    {
        var fakeTransport = new FakeLogindSignalTransport();
        using var source = new LinuxLogindPowerSource(
            NullLogger<LinuxLogindPowerSource>.Instance,
            () => fakeTransport);

        var suspendCount = 0;
        var resumeCount = 0;

        using var sSub = source.OnSuspending(() => suspendCount++);
        using var rSub = source.OnResumed(() => resumeCount++);

        using var cts = new CancellationTokenSource();
        var runTask = source.StartAsync(cts.Token);

        await fakeTransport.WaitForStartAsync();
        await fakeTransport.EmitPrepareForSleepAsync(false);

        Assert.Equal(0, suspendCount);
        Assert.Equal(1, resumeCount);

        await source.StopAsync(CancellationToken.None);
        await runTask;
    }

    [Fact]
    public async Task Multiple_subscribers_and_unsubscribing()
    {
        var fakeTransport = new FakeLogindSignalTransport();
        using var source = new LinuxLogindPowerSource(
            NullLogger<LinuxLogindPowerSource>.Instance,
            () => fakeTransport);

        var suspend1 = 0;
        var suspend2 = 0;
        var resume1 = 0;
        var resume2 = 0;

        var s1 = source.OnSuspending(() => suspend1++);
        var s2 = source.OnSuspending(() => suspend2++);
        var r1 = source.OnResumed(() => resume1++);
        var r2 = source.OnResumed(() => resume2++);

        using var cts = new CancellationTokenSource();
        var runTask = source.StartAsync(cts.Token);

        await fakeTransport.WaitForStartAsync();

        // Emit suspend
        await fakeTransport.EmitPrepareForSleepAsync(true);
        Assert.Equal(1, suspend1);
        Assert.Equal(1, suspend2);
        Assert.Equal(0, resume1);
        Assert.Equal(0, resume2);

        // Unsubscribe s1 and r1
        s1.Dispose();
        r1.Dispose();

        // Emit resume
        await fakeTransport.EmitPrepareForSleepAsync(false);
        Assert.Equal(1, suspend1); // Unchanged
        Assert.Equal(1, suspend2); // Unchanged
        Assert.Equal(0, resume1); // Was disposed
        Assert.Equal(1, resume2); // Received event

        s2.Dispose();
        r2.Dispose();

        await source.StopAsync(CancellationToken.None);
        await runTask;
    }

    [Fact]
    public async Task Throwing_callback_does_not_kill_broker_or_loop()
    {
        var fakeTransport = new FakeLogindSignalTransport();
        using var source = new LinuxLogindPowerSource(
            NullLogger<LinuxLogindPowerSource>.Instance,
            () => fakeTransport);

        var secondSuspendCalled = false;
        var secondResumeCalled = false;

        using var s1 = source.OnSuspending(() => throw new InvalidOperationException("Suspend fault"));
        using var s2 = source.OnSuspending(() => secondSuspendCalled = true);
        using var r1 = source.OnResumed(() => throw new InvalidOperationException("Resume fault"));
        using var r2 = source.OnResumed(() => secondResumeCalled = true);

        using var cts = new CancellationTokenSource();
        var runTask = source.StartAsync(cts.Token);

        await fakeTransport.WaitForStartAsync();

        await fakeTransport.EmitPrepareForSleepAsync(true);
        Assert.True(secondSuspendCalled);

        await fakeTransport.EmitPrepareForSleepAsync(false);
        Assert.True(secondResumeCalled);

        await source.StopAsync(CancellationToken.None);
        await runTask;
    }

    [Fact]
    public async Task Exponential_backoff_delays_grow_on_consecutive_failures_before_ready()
    {
        var observedDelays = new List<TimeSpan>();
        var failCount = 0;
        using var cts = new CancellationTokenSource();

        using var source = new LinuxLogindPowerSource(
            NullLogger<LinuxLogindPowerSource>.Instance,
            () => new FailingLogindSignalTransport(),
            retryDelays: [
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30),
            ],
            delayFunc: (delay, token) =>
            {
                observedDelays.Add(delay);
                if (++failCount >= 5)
                {
                    cts.Cancel();
                }
                return Task.CompletedTask;
            });

        try
        {
            await source.StartAsync(cts.Token);
            await Task.Delay(100);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(5, observedDelays.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), observedDelays[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), observedDelays[1]);
        Assert.Equal(TimeSpan.FromSeconds(5), observedDelays[2]);
        Assert.Equal(TimeSpan.FromSeconds(10), observedDelays[3]);
        Assert.Equal(TimeSpan.FromSeconds(30), observedDelays[4]);
    }

    [Fact]
    public async Task Transport_reconnect_recovers_after_exception_without_duplication()
    {
        var transport1 = new FakeLogindSignalTransport();
        var transport2 = new FakeLogindSignalTransport();
        var factoryCallCount = 0;

        using var source = new LinuxLogindPowerSource(
            NullLogger<LinuxLogindPowerSource>.Instance,
            () =>
            {
                var idx = Interlocked.Increment(ref factoryCallCount);
                return idx == 1 ? transport1 : transport2;
            },
            delayFunc: (_, _) => Task.CompletedTask);

        var resumeCount = 0;
        using var rSub = source.OnResumed(() => resumeCount++);

        using var cts = new CancellationTokenSource();
        var runTask = source.StartAsync(cts.Token);

        await transport1.WaitForStartAsync();

        // Simulate transport 1 failure/disconnect
        transport1.Fail(new InvalidOperationException("D-Bus daemon disconnected"));

        // Wait for transport 2 to connect via retry loop
        await transport2.WaitForStartAsync();

        // Emit signal on new transport
        await transport2.EmitPrepareForSleepAsync(false);

        Assert.Equal(1, resumeCount);

        await source.StopAsync(CancellationToken.None);
        await runTask;
    }

    private sealed class FailingLogindSignalTransport : ILogindSignalTransport
    {
        public Task ObservePrepareForSleepAsync(Func<bool, ValueTask> handler, Action onReady, CancellationToken cancellationToken)
        {
            // Fails before onReady
            throw new InvalidOperationException("Failed to connect to D-Bus socket");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLogindSignalTransport : ILogindSignalTransport
    {
        private readonly TaskCompletionSource<bool> _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _completedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<bool, ValueTask>? _activeHandler;

        public Task WaitForStartAsync() => _startedTcs.Task;

        public async Task ObservePrepareForSleepAsync(Func<bool, ValueTask> handler, Action onReady, CancellationToken cancellationToken)
        {
            _activeHandler = handler;
            _startedTcs.TrySetResult(true);
            onReady();

            using var reg = cancellationToken.Register(() => _completedTcs.TrySetCanceled(cancellationToken));
            await _completedTcs.Task.ConfigureAwait(false);
        }

        public async Task EmitPrepareForSleepAsync(bool isSuspending)
        {
            if (_activeHandler is not null)
            {
                await _activeHandler(isSuspending).ConfigureAwait(false);
            }
        }

        public void Fail(Exception ex)
        {
            _completedTcs.TrySetException(ex);
        }

        public ValueTask DisposeAsync()
        {
            _completedTcs.TrySetResult(true);
            return ValueTask.CompletedTask;
        }
    }
}
