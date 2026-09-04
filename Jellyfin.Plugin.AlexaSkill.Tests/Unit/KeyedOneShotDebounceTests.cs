using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for <see cref="KeyedOneShotDebounce"/>, the shared keyed one-shot
/// debounce map extracted by JF-449: firing, payload replacement on re-arm,
/// entry-removal straggler invalidation, and the Disarm/DisposeAll barrier
/// that closes the callback-vs-teardown interleavings for both playback
/// persistence owners.
/// </summary>
public class KeyedOneShotDebounceTests : IDisposable
{
    // Constructed with a long interval so no timer fires naturally; tests that
    // want a natural fire shrink Interval before arming.
    private readonly KeyedOneShotDebounce _debounce = new(TimeSpan.FromSeconds(30));

    public void Dispose() => _debounce.Dispose();

    [Fact]
    public void Arm_FiresCallbackOnce_AfterInterval()
    {
        int count = 0;
        var fired = new ManualResetEventSlim(false);
        _debounce.Interval = TimeSpan.FromMilliseconds(25);
        Assert.True(_debounce.Arm("k", () => { Interlocked.Increment(ref count); fired.Set(); }));

        Assert.True(fired.Wait(TimeSpan.FromSeconds(2)), "callback did not fire after the interval");

        // One-shot: without a re-arm there is no second fire.
        Thread.Sleep(75);
        Assert.Equal(1, count);
    }

    [Fact]
    public void ReArm_ReplacesPayload_TheLatestCallbackIsWhatRuns()
    {
        string? ran = null;
        var fired = new ManualResetEventSlim(false);
        _debounce.Interval = TimeSpan.FromMilliseconds(200);
        _debounce.Arm("k", () => ran = "first");
        _debounce.Arm("k", () => { ran = "second"; fired.Set(); });

        Assert.True(fired.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal("second", ran);
    }

    [Fact]
    public void Disarm_PreventsFire_AndStragglerFireNowNoOps()
    {
        int count = 0;
        Assert.True(_debounce.Arm("k", () => Interlocked.Increment(ref count)));
        _debounce.Disarm("k");

        // Entry removal is the straggler invalidation: a maximally late fire
        // carries nothing to run.
        _debounce.FireNow("k");
        Assert.Equal(0, count);
    }

    [Fact]
    public void FireNow_RunsArmedCallback_Synchronously()
    {
        var ran = new ManualResetEventSlim(false);
        _debounce.Arm("k", ran.Set);

        _debounce.FireNow("k");

        Assert.True(ran.IsSet);
    }

    [Fact]
    public async Task Disarm_IsBarrier_WaitsForInFlightCallback()
    {
        int ran = 0;
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        _debounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        _debounce.Arm("k", () => Interlocked.Increment(ref ran));

        // Park a callback inside the gate: it has started and holds the gate.
        Task callback = Task.Run(() => _debounce.FireNow("k"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

        Task disarm = Task.Run(() => _debounce.Disarm("k"));
        Assert.False(await TestHelpers.CompletedWithinAsync(disarm, TimeSpan.FromMilliseconds(100)), "Disarm returned while the callback was still in flight");

        release.Set();
        await disarm.WaitAsync(TimeSpan.FromSeconds(2));
        await callback.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, ran);

        // After the barrier the key is dead: no straggler can run.
        _debounce.FireNow("k");
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task DisposeAll_IsBarrier_DrainsInFlight_AndRejectsLaterArms()
    {
        int ran = 0;
        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        _debounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        _debounce.Arm("k", () => Interlocked.Increment(ref ran));

        Task callback = Task.Run(() => _debounce.FireNow("k"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

        Task teardown = Task.Run(() => _debounce.DisposeAll());
        Assert.False(await TestHelpers.CompletedWithinAsync(teardown, TimeSpan.FromMilliseconds(100)), "DisposeAll returned while the callback was still in flight");

        release.Set();
        await teardown.WaitAsync(TimeSpan.FromSeconds(2));
        await callback.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, ran);

        // Permanent stop: later arms are rejected and straggler fires no-op.
        Assert.False(_debounce.Arm("k2", () => Interlocked.Increment(ref ran)));
        _debounce.FireNow("k2");
        Assert.Equal(1, ran);
    }
}
