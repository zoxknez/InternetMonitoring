using IEM.Core.Hosting;
using IEM.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace IEM.App.Tests;

/// <summary>
/// Deterministic tests for Phase 3.1-6A Power Contract (Suspend + Resume).
/// Tests IPowerEventSource dual-channel subscription, fault isolation, and lifetime management.
/// </summary>
public sealed class PowerEventBrokerTests
{
    [Fact]
    public void RaiseSuspending_invokes_only_suspend_subscribers_exactly_once()
    {
        using var broker = new PowerEventBroker(NullLogger<PowerEventBroker>.Instance);
        var suspendCount = 0;
        var resumeCount = 0;

        using var subSuspend = broker.OnSuspending(() => suspendCount++);
        using var subResume = broker.OnResumed(() => resumeCount++);

        broker.RaiseSuspending();

        Assert.Equal(1, suspendCount);
        Assert.Equal(0, resumeCount);
    }

    [Fact]
    public void RaiseResumed_invokes_only_resume_subscribers_exactly_once()
    {
        using var broker = new PowerEventBroker(NullLogger<PowerEventBroker>.Instance);
        var suspendCount = 0;
        var resumeCount = 0;

        using var subSuspend = broker.OnSuspending(() => suspendCount++);
        using var subResume = broker.OnResumed(() => resumeCount++);

        broker.RaiseResumed();

        Assert.Equal(0, suspendCount);
        Assert.Equal(1, resumeCount);
    }

    [Fact]
    public void Multiple_subscribers_all_receive_notifications()
    {
        using var broker = new PowerEventBroker(NullLogger<PowerEventBroker>.Instance);
        var suspend1 = false;
        var suspend2 = false;
        var resume1 = false;
        var resume2 = false;

        using var s1 = broker.OnSuspending(() => suspend1 = true);
        using var s2 = broker.OnSuspending(() => suspend2 = true);
        using var r1 = broker.OnResumed(() => resume1 = true);
        using var r2 = broker.OnResumed(() => resume2 = true);

        broker.RaiseSuspending();
        Assert.True(suspend1);
        Assert.True(suspend2);
        Assert.False(resume1);
        Assert.False(resume2);

        broker.RaiseResumed();
        Assert.True(resume1);
        Assert.True(resume2);
    }

    [Fact]
    public void Throwing_subscriber_does_not_block_subsequent_subscribers()
    {
        using var broker = new PowerEventBroker(NullLogger<PowerEventBroker>.Instance);
        var secondSuspendCalled = false;
        var secondResumeCalled = false;

        using var s1 = broker.OnSuspending(() => throw new InvalidOperationException("Suspend failure"));
        using var s2 = broker.OnSuspending(() => secondSuspendCalled = true);

        using var r1 = broker.OnResumed(() => throw new InvalidOperationException("Resume failure"));
        using var r2 = broker.OnResumed(() => secondResumeCalled = true);

        // Neither raise should throw
        broker.RaiseSuspending();
        Assert.True(secondSuspendCalled);

        broker.RaiseResumed();
        Assert.True(secondResumeCalled);
    }

    [Fact]
    public void Disposing_subscription_removes_callback()
    {
        using var broker = new PowerEventBroker(NullLogger<PowerEventBroker>.Instance);
        var suspendCount = 0;
        var resumeCount = 0;

        var subSuspend = broker.OnSuspending(() => suspendCount++);
        var subResume = broker.OnResumed(() => resumeCount++);

        broker.RaiseSuspending();
        broker.RaiseResumed();

        Assert.Equal(1, suspendCount);
        Assert.Equal(1, resumeCount);

        subSuspend.Dispose();
        subResume.Dispose();

        broker.RaiseSuspending();
        broker.RaiseResumed();

        Assert.Equal(1, suspendCount);
        Assert.Equal(1, resumeCount);
    }

    [Fact]
    public void Disposing_broker_clears_all_callbacks()
    {
        var broker = new PowerEventBroker(NullLogger<PowerEventBroker>.Instance);
        var suspendCount = 0;
        var resumeCount = 0;

        broker.OnSuspending(() => suspendCount++);
        broker.OnResumed(() => resumeCount++);

        broker.Dispose();

        broker.RaiseSuspending();
        broker.RaiseResumed();

        Assert.Equal(0, suspendCount);
        Assert.Equal(0, resumeCount);
    }
}
