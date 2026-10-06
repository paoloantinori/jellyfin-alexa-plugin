using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// Tests for DeviceQueueManager: per-device queue management with persistence.
/// Covers creation, advancement, multi-device isolation, persistence, and cleanup.
/// </summary>
public class DeviceQueueManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DeviceQueueManager _manager;
    private readonly ILogger<DeviceQueueManager> _logger;

    public DeviceQueueManagerTests()
    {
        _tempDir = TestHelpers.CreateRegisteredTempDir("dq-test");
        _logger = LoggerFactory.Create(b => { }).CreateLogger<DeviceQueueManager>();
        _manager = new DeviceQueueManager(_tempDir, _logger);
    }

    public void Dispose()
    {
        _manager.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Cleanup best effort
        }

        GC.SuppressFinalize(this);
    }

    // =====================================================================
    // GetOrCreateQueue
    // =====================================================================

    [Fact]
    public void GetOrCreateQueue_CreatesNewForUnknownDevice()
    {
        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");

        Assert.NotNull(queue);
        Assert.Empty(queue.ItemIds);
        Assert.Equal(-1, queue.CurrentIndex);
    }

    // =====================================================================
    // ShuffleRemaining / RestoreOrder (issue #10 follow-up: playlist shuffle)
    // =====================================================================

    [Fact]
    public void ShuffleRemaining_KeepsCurrentFirst_RandomizesTail_StoresOriginal()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        _manager.SetQueue("dev", ids, currentIndex: 0);
        _manager.ShuffleRemaining("dev", currentItemId: "0");
        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.NotNull(q.OriginalItemIds);
        Assert.Equal(ids, q.OriginalItemIds);       // original preserved for un-shuffle
        Assert.Equal("0", q.ItemIds[0]);            // currently-playing stays first
        Assert.Equal(ids.Count, q.ItemIds.Count);   // no items lost or duplicated
        Assert.NotEqual(ids, q.ItemIds);            // tail was reordered
    }

    [Fact]
    public void ShuffleRemaining_NoOp_WhenQueueTooShort()
    {
        _manager.SetQueue("dev", new List<string> { "a", "b" }, 0);
        _manager.ShuffleRemaining("dev", "a");
        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Null(q.OriginalItemIds);             // not shuffled
        Assert.Equal("Default", q.PlaybackOrder);
    }

    [Fact]
    public void ShuffleRemaining_NoOp_WhenCurrentItemIsLast()
    {
        List<string> ids = Enumerable.Range(0, 5).Select(i => i.ToString()).ToList();
        _manager.SetQueue("dev", ids, currentIndex: 4);
        _manager.ShuffleRemaining("dev", "4");
        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Null(q.OriginalItemIds);             // nothing after current to shuffle
    }

    [Fact]
    public void RestoreOrder_RevertsToOriginal_WhenShuffled()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        _manager.SetQueue("dev", ids, 0);
        _manager.ShuffleRemaining("dev", "0");
        _manager.RestoreOrder("dev");
        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal("Default", q.PlaybackOrder);
        Assert.Null(q.OriginalItemIds);
        Assert.Equal(ids, q.ItemIds);               // back to original sequence
    }

    [Fact]
    public void RestoreOrder_NoOp_WhenNotShuffled()
    {
        List<string> ids = Enumerable.Range(0, 5).Select(i => i.ToString()).ToList();
        _manager.SetQueue("dev", ids, 0);
        _manager.RestoreOrder("dev");
        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal(ids, q.ItemIds);
        Assert.Equal("Default", q.PlaybackOrder);
    }


    [Fact]
    public void GetOrCreateQueue_ReturnsSameInstanceForSameDevice()
    {
        DeviceQueue queue1 = _manager.GetOrCreateQueue("device-1");
        DeviceQueue queue2 = _manager.GetOrCreateQueue("device-1");

        Assert.Same(queue1, queue2);
    }

    // =====================================================================
    // SetQueue
    // =====================================================================

    [Fact]
    public void SetQueue_StoresItemsCorrectly()
    {
        var items = new List<string> { "item1", "item2", "item3" };
        _manager.SetQueue("device-1", items, 0);

        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");
        Assert.Equal(3, queue.ItemIds.Count);
        Assert.Equal("item1", queue.ItemIds[0]);
        Assert.Equal("item2", queue.ItemIds[1]);
        Assert.Equal("item3", queue.ItemIds[2]);
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void SetQueue_OverwritesExistingQueue()
    {
        _manager.SetQueue("device-1", new List<string> { "old1", "old2" }, 0);
        _manager.SetQueue("device-1", new List<string> { "new1", "new2", "new3" }, 1);

        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");
        Assert.Equal(3, queue.ItemIds.Count);
        Assert.Equal("new1", queue.ItemIds[0]);
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Fact]
    public void SetQueue_SetsRepeatAndShuffleState()
    {
        var items = new List<string> { "item1", "item2" };
        _manager.SetQueue("device-1", items, 0, repeatMode: "All", playbackOrder: "Shuffle");

        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");
        Assert.Equal("All", queue.RepeatMode);
        Assert.Equal("Shuffle", queue.PlaybackOrder);
    }

    /// <summary>
    /// JF-693 (code-review finding 1): the last-played record is a LAUNCH fact, not
    /// queue content, so a queue reset carries it. The PlayBook paths call SetQueue
    /// AFTER the launch builder (the refusal-before-phantom-state ordering); before
    /// this contract the reset wiped the record the builder had just written on every
    /// successful book launch, blinding the medium classifiers (SleepTimer /
    /// SetPlaybackSpeed JF-632 gates) and resume arbitration.
    /// JF-693 review (coordinator F1): ALL THREE fields assert, id, route AND the
    /// freshness stamp: a carried record without its stamp reads as a legacy entry in
    /// GetDeviceResumePointer's tie rule (either stamp null => queue pointer wins), so
    /// the just-launched item would lose resume arbitration to the old song's
    /// stop-event pointer on the displaced-stop shape.
    /// </summary>
    [Fact]
    public void SetQueue_PreservesTheLastPlayedRecord()
    {
        _manager.RecordLastPlayed("device-1", "launched-item", DeviceQueueManager.LaunchRoute.VideoApp);
        DateTime? writtenAt = _manager.GetQueue("device-1").LastPlayedWrittenAt;
        Assert.NotNull(writtenAt);

        _manager.SetQueue("device-1", new List<string> { "q1", "q2" }, 0);

        Assert.Equal("launched-item", _manager.GetLastPlayedItemId("device-1"));
        (string? itemId, DeviceQueueManager.LaunchRoute? route) = _manager.GetLastPlayedSnapshot("device-1");
        Assert.Equal("launched-item", itemId);
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, route);
        Assert.Equal(writtenAt, _manager.GetQueue("device-1").LastPlayedWrittenAt);
    }

    // =====================================================================
    // Advance
    // =====================================================================

    [Fact]
    public void Advance_MovesToNextItem()
    {
        var items = new List<string> { "item1", "item2", "item3" };
        _manager.SetQueue("device-1", items, 0);

        string? next = _manager.Advance("device-1");
        Assert.Equal("item2", next);
    }

    [Fact]
    public void Advance_ReturnsNullAtEndOfQueue()
    {
        var items = new List<string> { "item1", "item2" };
        _manager.SetQueue("device-1", items, 1);

        string? next = _manager.Advance("device-1");
        Assert.Null(next);
    }

    [Fact]
    public void Advance_RepeatAll_WrapsAround()
    {
        var items = new List<string> { "item1", "item2" };
        _manager.SetQueue("device-1", items, 1, repeatMode: "All");

        string? next = _manager.Advance("device-1");
        Assert.Equal("item1", next);
    }

    [Fact]
    public void Advance_RepeatOne_StaysOnSameTrack()
    {
        var items = new List<string> { "item1", "item2", "item3" };
        _manager.SetQueue("device-1", items, 1, repeatMode: "One");

        string? next = _manager.Advance("device-1");
        Assert.Equal("item2", next);
    }

    [Fact]
    public void Advance_ReturnsNullForUnknownDevice()
    {
        string? next = _manager.Advance("unknown-device");
        Assert.Null(next);
    }

    [Fact]
    public void Advance_ReturnsNullForEmptyQueue()
    {
        _manager.GetOrCreateQueue("device-1");
        string? next = _manager.Advance("device-1");
        Assert.Null(next);
    }

    [Fact]
    public void Advance_Sequential_AdvancesThroughAll()
    {
        var items = new List<string> { "track1", "track2", "track3" };
        _manager.SetQueue("device-1", items, 0);

        Assert.Equal("track2", _manager.Advance("device-1"));
        Assert.Equal("track3", _manager.Advance("device-1"));
        Assert.Null(_manager.Advance("device-1"));
    }

    // =====================================================================
    // Multi-device isolation
    // =====================================================================

    [Fact]
    public void MultipleDevices_HaveIndependentQueues()
    {
        var items1 = new List<string> { "device1-item1", "device1-item2" };
        var items2 = new List<string> { "device2-item1", "device2-item2", "device2-item3" };

        _manager.SetQueue("device-A", items1, 0);
        _manager.SetQueue("device-B", items2, 1);

        DeviceQueue queueA = _manager.GetOrCreateQueue("device-A");
        DeviceQueue queueB = _manager.GetOrCreateQueue("device-B");

        Assert.Equal(2, queueA.ItemIds.Count);
        Assert.Equal(3, queueB.ItemIds.Count);
        Assert.Equal(0, queueA.CurrentIndex);
        Assert.Equal(1, queueB.CurrentIndex);

        // Advance on device A should not affect device B
        _manager.Advance("device-A");
        Assert.Equal(1, queueA.CurrentIndex);
        Assert.Equal(1, queueB.CurrentIndex);
    }

    [Fact]
    public void ActiveQueueCount_ReflectsActiveDevices()
    {
        Assert.Equal(0, _manager.ActiveQueueCount);

        _manager.SetQueue("device-1", new List<string> { "item1" }, 0);
        Assert.Equal(1, _manager.ActiveQueueCount);

        _manager.SetQueue("device-2", new List<string> { "item1" }, 0);
        Assert.Equal(2, _manager.ActiveQueueCount);
    }

    // =====================================================================
    // MoveTo
    // =====================================================================

    [Fact]
    public void MoveTo_UpdatesCurrentIndex()
    {
        var items = new List<string> { "item1", "item2", "item3" };
        _manager.SetQueue("device-1", items, 0);

        bool result = _manager.MoveTo("device-1", "item3");
        Assert.True(result);

        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");
        Assert.Equal(2, queue.CurrentIndex);
    }

    [Fact]
    public void MoveTo_ReturnsFalseForMissingItem()
    {
        var items = new List<string> { "item1", "item2" };
        _manager.SetQueue("device-1", items, 0);

        bool result = _manager.MoveTo("device-1", "item999");
        Assert.False(result);
    }

    [Fact]
    public void MoveTo_ReturnsFalseForUnknownDevice()
    {
        bool result = _manager.MoveTo("unknown-device", "item1");
        Assert.False(result);
    }

    // =====================================================================
    // Clear
    // =====================================================================

    [Fact]
    public void Clear_RemovesDeviceQueue()
    {
        _manager.SetQueue("device-1", new List<string> { "item1", "item2" }, 0);
        Assert.Equal(1, _manager.ActiveQueueCount);

        _manager.Clear("device-1");
        Assert.Equal(0, _manager.ActiveQueueCount);

        // GetOrCreateQueue should return a fresh empty queue
        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");
        Assert.Empty(queue.ItemIds);
    }

    // =====================================================================
    // Persistence
    // =====================================================================

    [Fact]
    public void Persistence_QueueSurvivesManagerRecreation()
    {
        var items = new List<string> { "track1", "track2", "track3" };
        _manager.SetQueue("device-1", items, 1, repeatMode: "All", playbackOrder: "Shuffle");

        // Force persist to disk
        _manager.PersistAll();
        _manager.Dispose();

        // Create a new manager from the same directory
        using var manager2 = new DeviceQueueManager(_tempDir, _logger);
        DeviceQueue restored = manager2.GetOrCreateQueue("device-1");

        Assert.Equal(3, restored.ItemIds.Count);
        Assert.Equal("track1", restored.ItemIds[0]);
        Assert.Equal("track2", restored.ItemIds[1]);
        Assert.Equal("track3", restored.ItemIds[2]);
        Assert.Equal(1, restored.CurrentIndex);
        Assert.Equal("All", restored.RepeatMode);
        Assert.Equal("Shuffle", restored.PlaybackOrder);
    }

    [Fact]
    public void SchedulePersist_AfterDispose_DoesNotWriteFile()
    {
        _manager.Dispose();

        // Arm-after-dispose race (JF-429): a queue write arriving after
        // Dispose must not arm the debounce timer and persist after cleanup.
        // Deterministic proof without wall-clock sleeps (JF-449 test-speed
        // note): fire the maximally late straggler manually; a rejected arm
        // leaves the gate nothing to run.
        _manager.SetQueue("device-1", new List<string> { "item1" }, 0);
        _manager.FirePersistForTest("device-1");
        string file = Path.Combine(_tempDir, "queue_device-1.json");

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Persistence_ClearRemovesFile()
    {
        _manager.SetQueue("device-1", new List<string> { "item1" }, 0);
        _manager.PersistAll();

        string file = Path.Combine(_tempDir, "queue_device-1.json");
        Assert.True(File.Exists(file));

        _manager.Clear("device-1");
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Clear_WithInFlightPersistCallback_LeavesNoQueueFile()
    {
        // JF-449 interleaving (a), AC #1: the debounce persist callback has
        // ALREADY STARTED when Clear runs; its write must not resurrect the
        // deleted queue file. Forced deterministically: BeforeCallbackGate
        // parks a fired payload inside the debounce gate (started, holding the
        // gate), then Clear runs on another thread and must barrier on it.
        _manager.TestDebounce.Interval = TimeSpan.FromSeconds(30); // no natural fire
        _manager.SetQueue("device-1", new List<string> { "item1", "item2" }, 0);
        string file = Path.Combine(_tempDir, "queue_device-1.json");

        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        // The park bound is 60s, not 5s: the assembly parallel phase (JF-792) can starve
        // the test thread past 5s, and an expired park corrupts the ordering witness.
        _manager.TestDebounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); };

        Task callback = Task.Run(() => _manager.FirePersistForTest("device-1"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(2))); // callback in flight, parked before its write

        Task clear = Task.Run(() => _manager.Clear("device-1"));
        Assert.False(await TestHelpers.CompletedWithinAsync(clear, TimeSpan.FromMilliseconds(100)), "Clear completed while the persist callback was still in flight");
        Assert.False(File.Exists(file), "the parked callback cannot have written yet");

        release.Set(); // the callback completes its write now; Clear's barrier then deletes it
        await clear.WaitAsync(TimeSpan.FromSeconds(2));
        await callback.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(_manager.GetQueue("device-1"));
        Assert.False(File.Exists(file)); // no resurrect
    }

    [Fact]
    public void Clear_ThenLateStragglerPersistCallback_DoesNotResurrectFile()
    {
        // JF-449 interleaving (a), straggler arm: the debounce fires after the
        // Clear already returned (Timer.Dispose does not recall a queued
        // callback). Entry removal in Clear's Disarm is what invalidates the
        // stale pre-Clear payload.
        _manager.TestDebounce.Interval = TimeSpan.FromMilliseconds(25);
        _manager.SetQueue("device-1", new List<string> { "item1" }, 0);
        string file = Path.Combine(_tempDir, "queue_device-1.json");
        _manager.PersistAll();
        Assert.True(File.Exists(file));

        _manager.Clear("device-1"); // disarms well inside the 25ms delay
        Thread.Sleep(150); // the natural fire window passes: nothing may run
        _manager.FirePersistForTest("device-1"); // and the maximally late straggler is a no-op

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Dispose_TearsDownDebounce_BeforeFinalFlush()
    {
        // JF-449 interleaving (c) order pin: DQM.Dispose must tear the debounce
        // down BEFORE the final flush (the unified order, previously the DQM
        // ran PersistAll first). Witness: with a persist callback parked
        // mid-flight (holding the debounce gate), the flush is ordered after
        // the teardown that is blocked on that gate, so no queue file can
        // exist until the callback is released.
        _manager.TestDebounce.Interval = TimeSpan.FromSeconds(30); // no natural fire
        _manager.SetQueue("device-1", new List<string> { "item1" }, 0);
        string file1 = Path.Combine(_tempDir, "queue_device-1.json");

        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        // The park bound is 60s, not 5s: the assembly parallel phase (JF-792) can starve
        // the test thread past 5s, and an expired park corrupts the ordering witness.
        _manager.TestDebounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); };

        Task callback = Task.Run(() => _manager.FirePersistForTest("device-1"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

        Task dispose = Task.Run(() => _manager.Dispose());
        Assert.False(await TestHelpers.CompletedWithinAsync(dispose, TimeSpan.FromMilliseconds(150)), "Dispose completed while a persist callback was still in flight");
        Assert.False(File.Exists(file1), "final flush ran before the debounce teardown (old Dispose order)");

        release.Set(); // teardown drains the callback, then the final flush runs
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await callback.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(File.Exists(file1)); // the final flush wrote it after teardown
    }

    [Fact]
    public void Persistence_MultipleDevicesPersistIndependently()
    {
        _manager.SetQueue("device-A", new List<string> { "a1", "a2" }, 0);
        _manager.SetQueue("device-B", new List<string> { "b1", "b2", "b3" }, 1);
        _manager.PersistAll();
        _manager.Dispose();

        using var manager2 = new DeviceQueueManager(_tempDir, _logger);

        DeviceQueue queueA = manager2.GetOrCreateQueue("device-A");
        DeviceQueue queueB = manager2.GetOrCreateQueue("device-B");

        Assert.Equal(2, queueA.ItemIds.Count);
        Assert.Equal(3, queueB.ItemIds.Count);
        Assert.Equal(0, queueA.CurrentIndex);
        Assert.Equal(1, queueB.CurrentIndex);
    }

    // =====================================================================
    // SetRepeatMode / SetPlaybackOrder
    // =====================================================================

    [Fact]
    public void SetRepeatMode_UpdatesExistingQueue()
    {
        _manager.SetQueue("device-1", new List<string> { "item1", "item2" }, 0);
        _manager.SetRepeatMode("device-1", "One");

        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");
        Assert.Equal("One", queue.RepeatMode);
    }

    [Fact]
    public void SetPlaybackOrder_UpdatesExistingQueue()
    {
        _manager.SetQueue("device-1", new List<string> { "item1", "item2" }, 0);
        _manager.SetPlaybackOrder("device-1", "Shuffle");

        DeviceQueue queue = _manager.GetOrCreateQueue("device-1");
        Assert.Equal("Shuffle", queue.PlaybackOrder);
    }

    // =====================================================================
    // Enqueue (JF-578, the queue-membership writer for the non-play paths)
    // =====================================================================

    [Fact]
    public void Enqueue_End_AppendsItem_KeepsCurrentIndex()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var added = Guid.NewGuid();
        _manager.SetQueue("device-1", new List<string> { a.ToString(), b.ToString(), c.ToString() }, 1);

        _manager.Enqueue("device-1", added, DeviceQueueManager.QueueInsertPlacement.End);

        DeviceQueue queue = _manager.GetQueue("device-1")!;
        Assert.Equal(
            new[] { a, b, c, added }.Select(g => g.ToString()),
            queue.ItemIds);
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Fact]
    public void Enqueue_AfterCurrent_InsertsBehindCurrent_PointerUnchanged()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var added = Guid.NewGuid();
        _manager.SetQueue("device-1", new List<string> { a.ToString(), b.ToString(), c.ToString() }, 0);

        DeviceQueue pre = _manager.GetQueue("device-1")!;
        long? preTicks = pre.CurrentPositionTicks;
        string? preItemId = pre.CurrentItemId;

        _manager.Enqueue("device-1", added, DeviceQueueManager.QueueInsertPlacement.AfterCurrent, a);

        DeviceQueue queue = _manager.GetQueue("device-1")!;
        Assert.Equal(
            new[] { a, added, b, c }.Select(g => g.ToString()),
            queue.ItemIds);
        Assert.Equal(0, queue.CurrentIndex);
        // Review finding 3: the playback pointers are DELIBERATELY untouched by an
        // add (owned by the event writers); a future edit that "helpfully" updates
        // them would silently break the Resume/PlaybackStopped token guards.
        Assert.Equal(preItemId, queue.CurrentItemId);
        Assert.Equal(preTicks, queue.CurrentPositionTicks);
    }

    [Fact]
    public void Enqueue_AfterCurrent_InsertBeforePointer_AdvancesPointer()
    {
        // The pointer keeps naming the same PHYSICAL item: an insert at or before
        // it shifted that item right (here the resolved current [a] sits before
        // the pointer [c], the restart pointer-lag shape).
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var added = Guid.NewGuid();
        _manager.SetQueue("device-1", new List<string> { a.ToString(), b.ToString(), c.ToString() }, 2);

        _manager.Enqueue("device-1", added, DeviceQueueManager.QueueInsertPlacement.AfterCurrent, a);

        DeviceQueue queue = _manager.GetQueue("device-1")!;
        Assert.Equal(
            new[] { a, added, b, c }.Select(g => g.ToString()),
            queue.ItemIds);
        Assert.Equal(3, queue.CurrentIndex);
        Assert.Equal(c.ToString(), queue.ItemIds[queue.CurrentIndex]);
    }

    [Fact]
    public void Enqueue_AfterCurrent_NoCurrentItem_InsertsAtFront()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var added = Guid.NewGuid();
        _manager.SetQueue("device-1", new List<string> { a.ToString(), b.ToString() }, 1);

        _manager.Enqueue("device-1", added, DeviceQueueManager.QueueInsertPlacement.AfterCurrent);

        DeviceQueue queue = _manager.GetQueue("device-1")!;
        Assert.Equal(
            new[] { added, a, b }.Select(g => g.ToString()),
            queue.ItemIds);
        Assert.Equal(2, queue.CurrentIndex);
    }

    [Fact]
    public void Enqueue_MissingQueue_SeedsSingleItem_WithNoCurrentPointer()
    {
        var added = Guid.NewGuid();

        _manager.Enqueue("unknown-device", added, DeviceQueueManager.QueueInsertPlacement.AfterCurrent);

        DeviceQueue queue = _manager.GetQueue("unknown-device")!;
        Assert.Equal(new[] { added.ToString() }, queue.ItemIds);
        Assert.Equal(-1, queue.CurrentIndex);
    }

    [Fact]
    public void Enqueue_OnShuffledQueue_AppendsToOriginalSnapshot_RestoreOrderKeepsIt()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var added = Guid.NewGuid();
        _manager.CommitShuffledQueue("device-1", _manager.DeriveShuffledQueue(new List<string> { a.ToString(), b.ToString(), c.ToString() }, new Random(9)));

        _manager.Enqueue("device-1", added, DeviceQueueManager.QueueInsertPlacement.End);
        Assert.Contains(added.ToString(), _manager.GetQueue("device-1")!.OriginalItemIds!);

        // Shuffle-off restores the original order WITHOUT dropping the user's
        // added item (membership survives; its restored position is the end).
        _manager.RestoreOrder("device-1");
        DeviceQueue queue = _manager.GetQueue("device-1")!;
        Assert.Equal(added.ToString(), queue.ItemIds[^1]);
        Assert.Null(queue.OriginalItemIds);
    }

    [Fact]
    public void Enqueue_SurvivesManagerRecreation()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var added = Guid.NewGuid();
        _manager.SetQueue("device-1", new List<string> { a.ToString(), b.ToString() }, 0);

        _manager.Enqueue("device-1", added, DeviceQueueManager.QueueInsertPlacement.AfterCurrent, a);
        _manager.FirePersistForTest("device-1");

        using var manager2 = new DeviceQueueManager(_tempDir, _logger);
        DeviceQueue restored = manager2.GetQueue("device-1")!;
        Assert.Equal(
            new[] { a, added, b }.Select(g => g.ToString()),
            restored.ItemIds);
        Assert.Equal(0, restored.CurrentIndex);
    }

    // =====================================================================
    // Edge cases
    // =====================================================================

    [Fact]
    public void DeviceIdWithSpecialCharacters_SanitizedInFilename()
    {
        _manager.SetQueue("device:special/chars", new List<string> { "item1" }, 0);
        _manager.PersistAll();

        string file = Path.Combine(_tempDir, "queue_device_special_chars.json");
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void EmptyDataDirectory_StartsWithNoQueues()
    {
        Assert.Equal(0, _manager.ActiveQueueCount);
    }

    // =====================================================================
    // Shuffle-at-start (JF-305; JF-713 split the write into
    // DeriveShuffledQueue + CommitShuffledQueue around the launch build)
    // =====================================================================

    /// <summary>
    /// Non-shuffle playlist-play baseline (JF-305 Chunk 2 regression).
    /// This is the counterpart to <see cref="DeriveAndCommit_ShufflesAllItems_StoresOriginal_SetsShuffleState"/>:
    /// the non-shuffle arm of the playlist play flow (<c>AlbumPlayService.BuildPlaylistPlayResponseAsync</c>)
    /// (shuffle: false) persists the queue via <see cref="DeviceQueueManager.SetQueue"/>
    /// and serves the first ordered track. The persisted <see cref="DeviceQueue"/> MUST
    /// be in <c>Default</c> order with no stored original (pre-shuffle) id list; that is
    /// the distinguishable state that makes Chunk 3's <c>shuffle:true</c> caller safe to add.
    /// </summary>
    /// <remarks>
    /// The full handler path (<c>PlayPlaylistIntentHandler → BuildPlaylistPlayResponseAsync</c>)
    /// cannot be exercised here: <c>Playlist.GetManageableItems()</c> is non-virtual and
    /// delegates to <c>BaseItem.GetLinkedChildrenInfos()</c>, which requires the static
    /// <c>BaseItem.LibraryManager</c> set up during server startup (DB-coupled, returns
    /// empty in a unit-test host). The persistence contract — the part of the non-shuffle
    /// arm whose state could silently regress — is therefore asserted at the
    /// <see cref="DeviceQueueManager"/> level, mirroring the exact call the arm makes
    /// (<c>SetQueue(deviceId, idList, 0)</c>).
    /// </remarks>
    [Fact]
    public void NonShufflePlaylistPlay_SetQueue_YieldsOrderedFirst_DefaultOrder_NoOriginal()
    {
        // Ordered playlist track ids, as BuildPlaylistPlayResponseAsync builds them from
        // PlaylistTrackResolver.GetAudioTracks(...).Take(initialFetchSize) — the ordering
        // contract PlaylistTrackResolverTests.Preserves_order guards upstream.
        List<string> playlistTrackIds = new() { "track-A", "track-B", "track-C", "track-D" };

        // Mirrors the non-shuffle arm exactly: queueManager?.SetQueue(deviceId, idList, 0)
        _manager.SetQueue("device-playlist", playlistTrackIds, currentIndex: 0);

        DeviceQueue q = _manager.GetOrCreateQueue("device-playlist");

        // (a) ordered-first: the served/persisted first item is the playlist's first track
        Assert.Equal("track-A", q.ItemIds[0]);
        Assert.Equal(playlistTrackIds, q.ItemIds);          // full original order preserved

        // (b) non-shuffled state: Default order, no stored original id list
        Assert.Equal("Default", q.PlaybackOrder);
        Assert.Null(q.OriginalItemIds);
    }

    [Fact]
    public void DeriveAndCommit_ShufflesAllItems_StoresOriginal_SetsShuffleState()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        _manager.CommitShuffledQueue("dev", _manager.DeriveShuffledQueue(ids, new Random(42)));

        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.Equal(0, q.CurrentIndex);
        Assert.NotNull(q.OriginalItemIds);
        Assert.Equal(ids, q.OriginalItemIds);                                   // pre-shuffle order preserved
        Assert.Equal(ids.Count, q.ItemIds.Count);                              // no loss/duplication
        Assert.Equal(new HashSet<string>(ids), new HashSet<string>(q.ItemIds));    // same set of ids
        Assert.NotEqual(ids, q.ItemIds);                                       // order changed (full list, incl pos 0)
    }

    [Fact]
    public void DeriveAndCommit_MatchesSeededFisherYates()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        List<string> expected = ExpectedSeededShuffle(ids);

        _manager.CommitShuffledQueue("dev", _manager.DeriveShuffledQueue(ids, new Random(42)));
        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal(expected, q.ItemIds);
        Assert.Equal(ids, q.OriginalItemIds);
        Assert.NotEqual(ids[0], q.ItemIds[0]);   // position 0 changed, the FR's core requirement
    }

    [Fact]
    public void DeriveAndCommit_SmallQueue_StillSetsState_PreservesItems()
    {
        var ids = new List<string> { "a", "b" };
        _manager.CommitShuffledQueue("dev", _manager.DeriveShuffledQueue(ids, new Random(1)));

        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.NotNull(q.OriginalItemIds);
        Assert.Equal(ids, q.OriginalItemIds);
        Assert.Equal(2, q.ItemIds.Count);
    }

    [Fact]
    public void DeriveAndCommit_PreservesItemPositionStateAcrossReset()
    {
        _manager.SetQueue("dev", new List<string> { "a", "b", "c" }, 0);
        _manager.GetOrCreateQueue("dev").ItemPositionState["a"] = 1234L;

        _manager.CommitShuffledQueue("dev", _manager.DeriveShuffledQueue(new List<string> { "a", "b", "c" }, new Random(9)));

        DeviceQueue q = _manager.GetOrCreateQueue("dev");
        Assert.Equal(1234L, q.ItemPositionState["a"]);
    }

    // =====================================================================
    // DeriveShuffledQueue / CommitShuffledQueue split pins (JF-713
    // derive-then-commit; the tests above drive the two as a pair)
    // =====================================================================

    /// <summary>
    /// JF-713: the derive step produces the seeded Fisher-Yates order, stores
    /// NOTHING (the device-queue dictionary stays empty), and hands the caller
    /// both the shuffled order and the pre-shuffle snapshot.
    /// </summary>
    [Fact]
    public void DeriveShuffledQueue_MatchesSeededFisherYates_NothingStored()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        List<string> expected = ExpectedSeededShuffle(ids);

        PendingShuffledQueue pending = _manager.DeriveShuffledQueue(ids, new Random(42));

        Assert.Equal(expected, pending.ShuffledItemIds);
        Assert.Equal(expected[0], pending.FirstItemId);
        Assert.Equal(ids, pending.OriginalItemIds);   // pre-shuffle order snapshotted
        Assert.NotEqual(ids[0], pending.ShuffledItemIds[0]);   // position 0 changed (random first track)

        // Derivation alone writes no queue: the playlist shuffle arm derives BEFORE
        // the launch build and commits only on success (JF-713).
        Assert.Equal(0, _manager.ActiveQueueCount);
    }

    /// <summary>
    /// JF-713: the commit step stores the snapshot VERBATIM. Committing the same
    /// snapshot twice yields identical orders on both devices: the snapshot
    /// answer to the old in-code objection (shuffling AGAIN at commit would
    /// re-shuffle and disagree with the stored order). The stored queues own
    /// DEFENSIVE COPIES (the code-review aliasing finding): a second commit and
    /// any later in-place queue mutation cannot reach through the snapshot.
    /// </summary>
    [Fact]
    public void CommitShuffledQueue_StoresSnapshotVerbatim_NeverReshuffles()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        PendingShuffledQueue pending = _manager.DeriveShuffledQueue(ids, new Random(42));
        List<string> snapshotBefore = pending.ShuffledItemIds.ToList();

        DeviceQueue q1 = _manager.CommitShuffledQueue("dev-a", pending);
        DeviceQueue q2 = _manager.CommitShuffledQueue("dev-b", pending);

        Assert.Equal("Shuffle", q1.PlaybackOrder);
        Assert.Equal(0, q1.CurrentIndex);
        Assert.Equal(snapshotBefore, q1.ItemIds);            // the snapshot's order, not a re-derivation
        Assert.Equal(ids, q1.OriginalItemIds);
        Assert.Equal(q1.ItemIds, q2.ItemIds);                // a second commit of the same snapshot cannot re-shuffle

        // No aliasing (the code-review failure scenario): an AddToQueue on dev-a
        // mutates only dev-a's copy, never dev-b's queue...
        _manager.Enqueue("dev-a", Guid.NewGuid(), DeviceQueueManager.QueueInsertPlacement.End);
        Assert.Equal(snapshotBefore, q2.ItemIds);
        // ...and the commit does not mutate the snapshot it stored from.
        Assert.Equal(snapshotBefore, pending.ShuffledItemIds);
        Assert.Equal(ids, pending.OriginalItemIds);
    }

    /// <summary>
    /// JF-713 (the JF-712 PendingContinuation structural-guard idiom): a snapshot
    /// with an empty shuffled list is a derive-site bug; <see cref="PendingShuffledQueue.FirstItemId"/>
    /// fails with the contract named instead of a distant index error.
    /// </summary>
    [Fact]
    public void PendingShuffledQueue_EmptyList_FirstItemIdThrowsContract()
    {
        var pending = new PendingShuffledQueue(new List<string>(), new List<string>());

        var ex = Record.Exception(() => pending.FirstItemId);

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("JF-713", ex.Message, StringComparison.Ordinal);
    }

    // =====================================================================
    // Launch-scope trim pins (JF-723: the derive-to-commit window's fresh entry
    // is never its own trim's evictee; MaxLaunchBaseEntries cap pressure)
    // =====================================================================

    /// <summary>
    /// JF-723 pin: the entry <see cref="DeviceQueueManager.RecordLaunchBase"/> just
    /// wrote is NEVER the evictee of that same call's trim, even at full cap
    /// pressure. Seeded shape (<see cref="SeedQueuedScopeResidents"/> with "N"
    /// membership, which matches the map keys per the documented
    /// <c>TrimPositionMap</c> contract): every resident is queued, so the only
    /// evictable key at this cap pressure is the fresh one, which is precisely the
    /// eviction the guard forbids. The fresh launch carries the nonzero base and
    /// non-identity rate whose silent loss JF-723 files (reads base 0 / rate
    /// identity at every reader). The window narrative lives on
    /// <c>TrimLaunchBaseIfNeeded</c>'s doc.
    /// </summary>
    [Fact]
    public void RecordLaunchBase_NonZeroBaseEntry_SurvivesOwnTrimAtQueuedCapPressure()
    {
        SeedQueuedScopeResidents("dev-jf723", DeviceQueueManager.MaxLaunchBaseEntries, "N");

        string freshId = Guid.NewGuid().ToString();
        long baseMs = MinutesToMs(20);
        _manager.RecordLaunchBase("dev-jf723", freshId, baseMs, enqueued: false, ratePerMille: 1500);

        (long? recordedBase, int? recordedRate) = _manager.GetActiveLaunchScope("dev-jf723", freshId);
        Assert.Equal(baseMs, recordedBase);
        Assert.Equal(1500, recordedRate);
    }

    /// <summary>
    /// JF-723 guard scope: the exemption belongs to the entry recorded in the SAME
    /// call only. The previous launch's entry (protected while it was fresh) is an
    /// ordinary aged entry at the NEXT launch's trim: with the map pinned over cap
    /// by queued residents, the next fresh launch evicts it. The trim keeps
    /// trimming; the guard never disables it. JF-739: "aged" now means past the
    /// freshness window too, and the pin proves that expiry with the rule ON (a
    /// fake clock advanced past the window before the next launch), rather than
    /// disabling the mechanism; the sibling pins below prove the within-window
    /// half.
    /// </summary>
    [Fact]
    public void RecordLaunchBase_PreviousFreshEntry_IsEvictedByTheNextLaunchAtCapPressure()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _manager.SetTimeForTest(fake);
        SeedQueuedScopeResidents("dev-jf723-aging", DeviceQueueManager.MaxLaunchBaseEntries, "N");

        string firstFreshId = Guid.NewGuid().ToString();
        long firstBaseMs = MinutesToMs(5);
        _manager.RecordLaunchBase("dev-jf723-aging", firstFreshId, firstBaseMs, enqueued: false);

        // While fresh: present (the JF-723 guard's own claim, re-asserted on the aging path).
        Assert.Equal(firstBaseMs, _manager.GetActiveLaunchScope("dev-jf723-aging", firstFreshId).BaseMs);

        // The clock crosses the freshness window: the first entry's stamp expires,
        // rule ON, so the next launch's trim may evict it again.
        fake.Advance(DeviceQueueManager.RecentRecordFreshnessWindow.Add(TimeSpan.FromSeconds(1)));

        string secondFreshId = Guid.NewGuid().ToString();
        _manager.RecordLaunchBase("dev-jf723-aging", secondFreshId, 0, enqueued: false);

        Assert.Null(_manager.GetActiveLaunchScope("dev-jf723-aging", firstFreshId).BaseMs);
        Assert.Equal(0, _manager.GetActiveLaunchScope("dev-jf723-aging", secondFreshId).BaseMs);
    }

    /// <summary>
    /// JF-738 pin (the FLIPPED characterization: green-on-arrival evidence of the
    /// format mismatch until the JF-738 fix, red the moment the membership set
    /// went through NormalizeToMapKeyFormat, now pinning the RESTORED contract):
    /// production <see cref="DeviceQueueManager.SetQueue"/> callers store DASHED
    /// <c>Guid.ToString()</c> ids while the four launch-scope maps are keyed "N",
    /// and the trim's membership set re-keys the queue ids to "N", so a QUEUED
    /// resident's entry is protected at cap pressure. Signature asserted: at cap
    /// pressure (every resident queued) a non-queued launch evicts nothing
    /// queued, the fresh non-queued entry survives its own trim via the JF-723
    /// freshness guard, and the NEXT launch ages it into the evictee (the trim
    /// still trims; membership never disables it).
    /// DETERMINISM: order-independent, unlike the characterization it replaced
    /// (residents are excluded from eviction by membership, not enumeration
    /// order, and the aging step's only evictable candidate is the single aged
    /// non-queued entry). JF-739: the aging half proves expiry with the rule ON
    /// (a fake clock advanced past the freshness window before the second
    /// launch), rather than disabling the mechanism; the within-window sibling
    /// half is pinned separately below.
    /// </summary>
    [Fact]
    public void RecordLaunchBase_DashedQueueMembership_QueuedSeedsSurviveAndAgedNonQueuedEntryIsEvicted_JF738()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _manager.SetTimeForTest(fake);
        List<string> queuedIds = SeedQueuedScopeResidents("dev-jf738", DeviceQueueManager.MaxLaunchBaseEntries, "D");

        // The first non-queued launch pushes the map over cap: every resident is
        // membership-protected (the dashed queue ids match the "N" map keys through
        // the normalization) and the fresh entry carries the JF-723 guard.
        string firstVictimId = Guid.NewGuid().ToString();
        _manager.RecordLaunchBase("dev-jf738", firstVictimId, 0, enqueued: false);

        // The FLIP (pre-fix exactly inverted: the first QUEUED seed was the
        // evictee and the non-queued 201st record survived): every seeded
        // resident keeps its entry, and the fresh non-queued entry survives its
        // own trim (the JF-723 guard, not membership).
        Assert.All(queuedIds, id => Assert.True(_manager.GetActiveLaunchScope("dev-jf738", id).BaseMs.HasValue));
        Assert.NotNull(_manager.GetActiveLaunchScope("dev-jf738", firstVictimId).BaseMs);

        // The clock crosses the freshness window: the first victim's stamp
        // expires (rule ON), so the next launch ages it: no longer fresh, still
        // non-queued, the trim's evictee.
        fake.Advance(DeviceQueueManager.RecentRecordFreshnessWindow.Add(TimeSpan.FromSeconds(1)));

        string secondVictimId = Guid.NewGuid().ToString();
        _manager.RecordLaunchBase("dev-jf738", secondVictimId, 0, enqueued: false);

        Assert.Null(_manager.GetActiveLaunchScope("dev-jf738", firstVictimId).BaseMs);
        Assert.NotNull(_manager.GetActiveLaunchScope("dev-jf738", secondVictimId).BaseMs);
        Assert.All(queuedIds, id => Assert.True(_manager.GetActiveLaunchScope("dev-jf738", id).BaseMs.HasValue));
    }

    /// <summary>
    /// JF-738 helper pin: <see cref="DeviceQueueManager.NormalizeToMapKeyFormat(IEnumerable{string})"/>
    /// re-keys every parseable GUID (the dashed production ItemIds format, "N",
    /// brace formats) into the bounded maps' "N" key format and passes non-GUID
    /// entries through raw, so a non-GUID map key still compares equal to itself.
    /// </summary>
    [Fact]
    public void NormalizeToMapKeyFormat_RekeysAnyGuidFormatToN_PassesNonGuidRaw()
    {
        Guid id = Guid.NewGuid();

        List<string> normalized = DeviceQueueManager.NormalizeToMapKeyFormat(
            new[] { id.ToString(), id.ToString("N"), id.ToString("B"), "not-a-guid" }).ToList();

        Assert.Equal(
            new[] { id.ToString("N"), id.ToString("N"), id.ToString("N"), "not-a-guid" },
            normalized);
    }

    /// <summary>
    /// JF-741 structural pin, DeviceQueueManager side (the tracker twin lives in
    /// AudiobookPositionTrackerTests): the enumerable overload delegates per
    /// element to the single-string core and carries no parse of its own, so
    /// re-inlining the parse-or-raw expression here (what a future /simplify
    /// pass would write for a one-line wrapper) reds both legs and the two-copy
    /// state JF-741 closed cannot return silently on THIS side either. The type's
    /// OTHER Guid.TryParse sites (RecordLaunchBase and friends) are
    /// parse-and-reject gates feeding Guid-typed ToString("N") writes, a
    /// different rule, and are deliberately outside this pin's scope. The
    /// overload is an ITERATOR: its own body only allocates the compiler
    /// generated state machine, and both the delegation call and any re-inlined
    /// parse compile onto that machine's MoveNext, so the scan attributes the
    /// machine to the overload by the scanner's own naming convention
    /// (<c>&lt;NormalizeToMapKeyFormat&gt;d__N</c>).
    /// </summary>
    [Fact]
    public void NormalizeToMapKeyFormat_EnumerableDelegatesToCore_NoInlineParseCopy_JF741()
    {
        MethodInfo? enumerable = typeof(DeviceQueueManager).GetMethod(
            "NormalizeToMapKeyFormat",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(IEnumerable<string>) },
            modifiers: null);
        Assert.True(
            enumerable != null,
            "NormalizeToMapKeyFormat(IEnumerable<string>) must still exist (this pin's lookup target)");

        Module module = typeof(DeviceQueueManager).Module;
        var overloadBodies = new List<MethodBase> { enumerable };
        foreach (Type nested in typeof(DeviceQueueManager).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (IlCallScanner.ExtractCompilerGeneratedOwner(nested.Name) == "NormalizeToMapKeyFormat")
            {
                overloadBodies.AddRange(IlCallScanner.DeclaredCallableMethods(nested));
            }
        }

        foreach (MethodBase body in overloadBodies)
        {
            Assert.False(
                IlCallScanner.CallsNamedMethod(body, module, "TryParse", typeof(Guid)),
                $"{body.Name} must not call Guid.TryParse: the enumerable overload delegates per element to NormalizeToMapKeyFormat(string) (JF-741)");
        }

        Assert.True(
            overloadBodies.Any(body => IlCallScanner.CallsNamedMethod(body, module, "NormalizeToMapKeyFormat", typeof(DeviceQueueManager))),
            "The enumerable overload (its body or its iterator state machine) must call the single-string core NormalizeToMapKeyFormat(string)");
    }

    /// <summary>
    /// JF-723 pin, ENQUEUED arm: the guard protects a freshly-written PENDING pair
    /// (WritePendingLaunchScope) exactly like the active one; a regression that
    /// drops the exemption from only the pending-map trims reds here. The residents
    /// are seeded as PENDING entries so the pending maps sit at the cap and the
    /// fresh enqueued record actually crosses it (the trim's count gate is an OR
    /// across the four maps). The pending entry is observed through its promotion
    /// (the only public read path): after
    /// <see cref="DeviceQueueManager.PromotePendingLaunchBase"/> the ACTIVE scope
    /// must carry the recorded base and rate. Without the guard, the fresh pending
    /// pair is the only evictable key at this cap pressure and dies mid-window, so
    /// the promotion no-ops and the scope reads empty.
    /// </summary>
    [Fact]
    public void RecordLaunchBase_EnqueuedFreshPair_SurvivesOwnTrimAndPromotesAtQueuedCapPressure()
    {
        SeedQueuedScopeResidents("dev-jf723-pending", DeviceQueueManager.MaxLaunchBaseEntries, "N", enqueued: true);

        string freshId = Guid.NewGuid().ToString();
        long baseMs = MinutesToMs(9);
        _manager.RecordLaunchBase("dev-jf723-pending", freshId, baseMs, enqueued: true, ratePerMille: 750);

        _manager.PromotePendingLaunchBase("dev-jf723-pending", freshId);

        (long? recordedBase, int? recordedRate) = _manager.GetActiveLaunchScope("dev-jf723-pending", freshId);
        Assert.Equal(baseMs, recordedBase);
        Assert.Equal(750, recordedRate);
    }

    /// <summary>
    /// JF-723 pin (the saturated-churn shape; seeded under the JF-738 mismatch and
    /// kept verbatim after the JF-738 fix, where the same seeding still exercises
    /// the guard through the disjoint-membership route): with NO map key in the
    /// queue's membership, a saturated map churns by slot order, and a fresh
    /// insert that reuses the just-freed lowest slot is evicted by its OWN
    /// record's trim (the 201st record frees the oldest slot; the next insert lands
    /// there and becomes the oldest occupied entry). That self-evicted entry is the
    /// launch-scope record of the track the device is about to play; the guard must
    /// keep it observable. DETERMINISM NOTE: this pin rests on .NET Dictionary
    /// enumeration and free-list slot reuse (implementation details the production
    /// trim already relies on for its "oldest" semantics); a BCL change there
    /// degrades this pin to vacuously green rather than red, and the
    /// format-independent anchors for the guard are the two queued-cap-pressure
    /// pins above (active and enqueued). JF-739: the fake clock advances past the
    /// freshness window between records, so the mid-loop trims keep evicting
    /// (slot churn, rule ON) and the pin keeps exercising the freshKey GUARD
    /// under slot reuse rather than vacuously riding the freshness stamps.
    /// </summary>
    [Fact]
    public void RecordLaunchBase_SaturatedMapFreshInsert_SurvivesOwnTrimUnderDashedMembership()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _manager.SetTimeForTest(fake);

        // Old-queue membership (dashed, production format), no scope entries tied to it.
        _manager.SetQueue("dev-jf723-sat", Enumerable.Range(0, DeviceQueueManager.MaxLaunchBaseEntries).Select(_ => Guid.NewGuid().ToString()).ToList(), currentIndex: 0);

        // Cap-filling distinct previously-launched items (no trim fires at count <= cap);
        // each step crosses the freshness window so the NEXT record's trim sees every
        // earlier entry as expired.
        TimeSpan windowPlus = DeviceQueueManager.RecentRecordFreshnessWindow.Add(TimeSpan.FromSeconds(1));
        foreach (int i in Enumerable.Range(0, DeviceQueueManager.MaxLaunchBaseEntries))
        {
            _manager.RecordLaunchBase("dev-jf723-sat", Guid.NewGuid().ToString(), 0, enqueued: false);
            fake.Advance(windowPlus);
        }

        // The 201st distinct launch: pushes the map over cap, its trim frees the oldest slot.
        _manager.RecordLaunchBase("dev-jf723-sat", Guid.NewGuid().ToString(), 0, enqueued: false);

        // The fresh nonzero-base launch lands in the freed slot; its own trim must not evict it.
        string freshId = Guid.NewGuid().ToString();
        long baseMs = MinutesToMs(20);
        _manager.RecordLaunchBase("dev-jf723-sat", freshId, baseMs, enqueued: false, ratePerMille: 1500);

        (long? recordedBase, int? recordedRate) = _manager.GetActiveLaunchScope("dev-jf723-sat", freshId);
        Assert.Equal(baseMs, recordedBase);
        Assert.Equal(1500, recordedRate);
    }

    /// <summary>
    /// JF-739 pin (the launch-scope arm, the derive-to-commit sibling): seeded
    /// queued-cap-pressure on the OLD queue (dashed ids, the production format),
    /// the play path's launch record F (nonzero base + non-identity rate, the
    /// JF-723 silent-loss shape) survives its OWN trim, then a SIBLING
    /// <see cref="DeviceQueueManager.RecordLaunchBase"/> interleaves BEFORE the
    /// queue commit (a PlaybackNearlyFinished enqueue or a queue-editing launch
    /// on another thread; F's item is still absent from the STORED queue, so
    /// membership does not reach it, and the sibling's trim exempts only ITS
    /// key). Pre-fix a DETERMINISTIC eviction: F was the only non-queued key and
    /// the sibling trim needed one removal. The freshness stamp must keep F
    /// observable through the sibling's trim AND the commit that follows
    /// (SetQueue of the queue that finally contains F's item). The fake clock
    /// stays FROZEN: everything the pin drives happens inside the window, so no
    /// stamp may expire under it.
    /// </summary>
    [Fact]
    public void RecordLaunchBase_SiblingRecordInDeriveToCommitWindow_FreshEntrySurvivesToCommit_JF739()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _manager.SetTimeForTest(fake);
        SeedQueuedScopeResidents("dev-jf739", DeviceQueueManager.MaxLaunchBaseEntries, "D");

        string freshId = Guid.NewGuid().ToString();
        long baseMs = MinutesToMs(20);
        _manager.RecordLaunchBase("dev-jf739", freshId, baseMs, enqueued: false, ratePerMille: 1500);

        // F survives its own trim (the JF-723 identity guard).
        Assert.Equal(baseMs, _manager.GetActiveLaunchScope("dev-jf739", freshId).BaseMs);

        // The sibling, inside the window, before any commit.
        string siblingId = Guid.NewGuid().ToString();
        _manager.RecordLaunchBase("dev-jf739", siblingId, 0, enqueued: false);

        Assert.Equal(baseMs, _manager.GetActiveLaunchScope("dev-jf739", freshId).BaseMs);
        Assert.Equal(1500, _manager.GetActiveLaunchScope("dev-jf739", freshId).RatePerMille);
        Assert.NotNull(_manager.GetActiveLaunchScope("dev-jf739", siblingId).BaseMs);

        // The commit lands (the play path's SetQueue/CommitShuffledQueue): F's
        // item finally queued, and the scope it kept through the window is the
        // one the playback events compose against.
        _manager.SetQueue("dev-jf739", new List<string> { freshId, siblingId }, 0);
        Assert.Equal(baseMs, _manager.GetActiveLaunchScope("dev-jf739", freshId).BaseMs);
        Assert.Equal(1500, _manager.GetActiveLaunchScope("dev-jf739", freshId).RatePerMille);
    }

    /// <summary>
    /// JF-739 pin (the ENQUEUED arm): the sibling trim's pressure can come from
    /// the OTHER map family (the trim's count gate is an OR across the four
    /// maps), so a fresh PENDING pair must survive a sibling ACTIVE record in
    /// the window too. Seeded pending residents pin the pending maps at cap;
    /// F's enqueued record crosses them; the sibling's ACTIVE record trips the
    /// shared gate, and its trim would evict F's pending pair pre-fix (F
    /// non-queued, only the sibling's key exempt). Observed through the
    /// promotion (the only public read path; the JF-723 enqueued pin's route).
    /// The fake clock stays FROZEN (within-window determinism). The seeded
    /// pending residents are asserted too (GM-F2): the sibling's trim must not
    /// have bought F's survival by evicting a resident.
    /// </summary>
    [Fact]
    public void RecordLaunchBase_EnqueuedFreshPair_SurvivesSiblingActiveTrimInWindow_JF739()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _manager.SetTimeForTest(fake);
        List<string> queuedIds = SeedQueuedScopeResidents("dev-jf739-pend", DeviceQueueManager.MaxLaunchBaseEntries, "D", enqueued: true);

        string freshId = Guid.NewGuid().ToString();
        long baseMs = MinutesToMs(9);
        _manager.RecordLaunchBase("dev-jf739-pend", freshId, baseMs, enqueued: true, ratePerMille: 750);

        // A sibling launch on the ACTIVE family; the shared OR gate fires its trim.
        string siblingId = Guid.NewGuid().ToString();
        _manager.RecordLaunchBase("dev-jf739-pend", siblingId, 0, enqueued: false);

        // Every seeded pending resident survived the sibling's trim (GM-F2; the
        // pending maps have no public reader, so the pin reads the queue store
        // directly, the EventHandlerTests JF-738 idiom).
        DeviceQueue queue = _manager.GetQueue("dev-jf739-pend")!;
        Assert.All(queuedIds, id => Assert.True(queue.PendingLaunchBaseMs.ContainsKey(Guid.Parse(id).ToString("N"))));

        _manager.PromotePendingLaunchBase("dev-jf739-pend", freshId);

        (long? recordedBase, int? recordedRate) = _manager.GetActiveLaunchScope("dev-jf739-pend", freshId);
        Assert.Equal(baseMs, recordedBase);
        Assert.Equal(750, recordedRate);
    }

    /// <summary>
    /// JF-739 pin (gate-marker GM-F1, the replace-then-promote arm): the
    /// promotion is the launch family's THIRD bounded-map write, and it fires
    /// LONG after the enqueue's stamp expired. Seeded queued-cap-pressure, E
    /// enqueued (stamped), the clock crosses the window, the queue is REPLACED
    /// with the same residents (E's pending pair carried by
    /// CopySurvivingStores, E's id absent from it), PlaybackStarted promotes
    /// E, and a sibling launch trims at cap pressure. Without the promote's
    /// stamp the just-promoted ACTIVE scope is non-queued and the sibling
    /// deterministically evicts it (the JF-723 silent-loss class: base/rate
    /// gone while the stream plays). Asserted: E's promoted scope survives
    /// with its base and rate, every resident survives, the sibling is
    /// recorded.
    /// </summary>
    [Fact]
    public void PromotePendingLaunchBase_AfterQueueReplace_SurvivesSiblingTrimInWindow_JF739()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _manager.SetTimeForTest(fake);
        List<string> queuedIds = SeedQueuedScopeResidents("dev-jf739-promo", DeviceQueueManager.MaxLaunchBaseEntries, "D");

        string enqueuedId = Guid.NewGuid().ToString();
        long baseMs = MinutesToMs(11);
        _manager.RecordLaunchBase("dev-jf739-promo", enqueuedId, baseMs, enqueued: true, ratePerMille: 750);

        // The enqueue-then-start gap: the enqueue's stamp expires, then the
        // play path's commit replaces the queue WITHOUT the enqueued item.
        fake.Advance(DeviceQueueManager.RecentRecordFreshnessWindow.Add(TimeSpan.FromSeconds(1)));
        _manager.SetQueue("dev-jf739-promo", queuedIds, 0);

        // PlaybackStarted promotes the carried-over pending pair to active.
        _manager.PromotePendingLaunchBase("dev-jf739-promo", enqueuedId);

        // The sibling launch, inside the promote's window.
        string siblingId = Guid.NewGuid().ToString();
        _manager.RecordLaunchBase("dev-jf739-promo", siblingId, 0, enqueued: false);

        (long? promotedBase, int? promotedRate) = _manager.GetActiveLaunchScope("dev-jf739-promo", enqueuedId);
        Assert.Equal(baseMs, promotedBase);
        Assert.Equal(750, promotedRate);
        Assert.NotNull(_manager.GetActiveLaunchScope("dev-jf739-promo", siblingId).BaseMs);
        Assert.All(queuedIds, id => Assert.True(_manager.GetActiveLaunchScope("dev-jf739-promo", id).BaseMs.HasValue));
    }

    /// <summary>
    /// JF-739 pin (the position-store arm, DoD #2): the same sibling shape on
    /// ItemPositionState. Two stops on one device; at queued cap pressure the
    /// first stop's fresh entry is the only non-queued key, and the sibling
    /// stop's trim evicts it pre-fix, losing the JF-581 resume seed the store
    /// exists to hold. The stop-path write-then-trim is
    /// <see cref="DeviceQueueManager.RecordStoppedPositionAndTrim"/> (the
    /// JF-738 fresh-entry guard covers only the entry's OWN trim); the
    /// freshness stamp must keep the first entry through the sibling's trim.
    /// Seeding is direct (the shared SeedStoredPosition helper and the
    /// EventHandlerTests JF-738 pin idiom) so the seeds themselves carry no
    /// stamps, and the fake clock stays FROZEN (within-window determinism).
    /// </summary>
    [Fact]
    public void RecordStoppedPositionAndTrim_SiblingStopInWindow_FreshPositionSurvives_JF739()
    {
        var fake = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _manager.SetTimeForTest(fake);
        DeviceQueue queue = _manager.GetOrCreateQueue("dev-jf739-pos");
        List<Guid> residentIds = Enumerable.Range(0, DeviceQueueManager.MaxItemPositionStateEntries)
            .Select(_ => Guid.NewGuid()).ToList();
        queue.ItemIds = residentIds.Select(g => g.ToString()).ToList();
        foreach (Guid id in residentIds)
        {
            SeedStoredPosition("dev-jf739-pos", id, 1234);
        }

        Guid firstId = Guid.NewGuid();
        long firstTicks = TimeSpan.FromMinutes(3).Ticks;
        _manager.RecordStoppedPositionAndTrim("dev-jf739-pos", queue, firstId, firstTicks);

        // Survives its own trim (the JF-738 fresh-entry guard).
        Assert.Equal(firstTicks, _manager.GetStoredPositionTicks("dev-jf739-pos", firstId.ToString()));

        // The sibling stop, inside the window.
        Guid siblingId = Guid.NewGuid();
        long siblingTicks = TimeSpan.FromMinutes(4).Ticks;
        _manager.RecordStoppedPositionAndTrim("dev-jf739-pos", queue, siblingId, siblingTicks);

        Assert.Equal(firstTicks, _manager.GetStoredPositionTicks("dev-jf739-pos", firstId.ToString()));
        Assert.Equal(siblingTicks, _manager.GetStoredPositionTicks("dev-jf739-pos", siblingId.ToString()));
        Assert.All(residentIds, id => Assert.Equal((long?)1234, _manager.GetStoredPositionTicks("dev-jf739-pos", id.ToString())));
    }

    /// <summary>
    /// Seeds the JF-723/JF-738 cap-pressure shape: a queue of
    /// <paramref name="count"/> ids in the given GUID format, each with a
    /// launch-scope entry (active or pending per <paramref name="enqueued"/>), so
    /// that map family sits exactly at the trim cap with every resident queued
    /// ("N" matches the map keys directly; "D" is the production dashed format,
    /// which matched NOTHING pre-JF-738 and matches through
    /// NormalizeToMapKeyFormat since). Returns the seeded ids.
    /// </summary>
    private List<string> SeedQueuedScopeResidents(string deviceId, int count, string guidFormat, bool enqueued = false)
    {
        List<string> ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString(guidFormat)).ToList();
        _manager.SetQueue(deviceId, ids, currentIndex: 0);
        foreach (string id in ids)
        {
            _manager.RecordLaunchBase(deviceId, id, 0, enqueued);
        }

        return ids;
    }

    /// <summary>The minutes-to-milliseconds idiom shared by the launch-scope pins
    /// (the same per-file helper shape as PlaybackPositionProvenanceTests).</summary>
    private static long MinutesToMs(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    // =====================================================================
    // ResolveResumeTicks (JF-581 seed resolution, shared by the JF-565 slice)
    // =====================================================================

    /// <summary>
    /// UserData-first: a healthy UserData position wins over anything the plugin
    /// store holds.
    /// </summary>
    [Fact]
    public void ResolveResumeTicks_UserDataPositive_WinsOverStore()
    {
        var itemId = Guid.NewGuid();
        long userDataTicks = TimeSpan.FromMinutes(10).Ticks;
        SeedStoredPosition("dev", itemId, TimeSpan.FromMinutes(99).Ticks);

        long resolved = DeviceQueueManager.ResolveResumeTicks(_manager, "dev", itemId.ToString(), userDataTicks, userDataPlayed: false);

        Assert.Equal(userDataTicks, resolved);
    }

    /// <summary>
    /// The JF-581 Played guard: a completed item legitimately reads UserData 0, and
    /// the stale stored mid-listen position must not resurrect over the finished
    /// listen.
    /// </summary>
    [Fact]
    public void ResolveResumeTicks_PlayedItem_DoesNotResurrectStoredPosition()
    {
        var itemId = Guid.NewGuid();
        long staleTicks = TimeSpan.FromMinutes(60).Ticks;
        SeedStoredPosition("dev", itemId, staleTicks);

        long resolved = DeviceQueueManager.ResolveResumeTicks(_manager, "dev", itemId.ToString(), 0, userDataPlayed: true);

        Assert.Equal(0, resolved);
    }

    /// <summary>
    /// The live write-loss shape: UserData reads 0 while the plugin-owned store holds
    /// the real stop position; the store seeds the resume.
    /// </summary>
    [Fact]
    public void ResolveResumeTicks_UserDataZero_SeedsFromStore()
    {
        var itemId = Guid.NewGuid();
        long storedTicks = 3641180000; // the JF-581 incident's real stop position
        SeedStoredPosition("dev", itemId, storedTicks);

        long resolved = DeviceQueueManager.ResolveResumeTicks(_manager, "dev", itemId.ToString(), 0, userDataPlayed: false);

        Assert.Equal(storedTicks, resolved);
    }

    /// <summary>
    /// Cold-manager and cold-store shapes resolve 0 (no manager reference, unknown
    /// device, or nothing recorded): callers treat 0 as "play from the start".
    /// </summary>
    [Fact]
    public void ResolveResumeTicks_NoManagerOrNoStoredPosition_Zero()
    {
        var itemId = Guid.NewGuid();

        Assert.Equal(0, DeviceQueueManager.ResolveResumeTicks(null, "dev", itemId.ToString(), 0, userDataPlayed: false));
        Assert.Equal(0, DeviceQueueManager.ResolveResumeTicks(_manager, "dev", itemId.ToString(), 0, userDataPlayed: false));
        Assert.Equal(0, DeviceQueueManager.ResolveResumeTicks(_manager, null, itemId.ToString(), 0, userDataPlayed: false));
    }

    private void SeedStoredPosition(string deviceId, Guid itemId, long ticks)
        => _manager.GetOrCreateQueue(deviceId).ItemPositionState[itemId.ToString("N")] = ticks;

    /// <summary>
    /// The seeded Fisher-Yates oracle shared by the shuffle tests: an independent
    /// in-place shuffle of a copy with <c>new Random(42)</c>, so the derive/commit
    /// order is checked against the algorithm recomputed here, never against the
    /// production <see cref="DeviceQueueManager"/> code under test.
    /// </summary>
    private static List<string> ExpectedSeededShuffle(List<string> ids)
    {
        List<string> expected = new(ids);
        var rngExpected = new Random(42);
        for (int i = expected.Count - 1; i > 0; i--)
        {
            int j = rngExpected.Next(i + 1);
            (expected[i], expected[j]) = (expected[j], expected[i]);
        }

        return expected;
    }

    // =====================================================================
    // Last-played launch route (JF-568)
    // =====================================================================

    /// <summary>
    /// JF-568: the route recorded beside the last-played item survives the
    /// persist/reload cycle (Dispose flushes, a fresh manager on the same dir
    /// reloads), so the classification reads it after a restart too.
    /// </summary>
    [Fact]
    public void RecordLastPlayed_RouteRoundTripsThroughDisk()
    {
        var itemId = Guid.NewGuid();
        _manager.RecordLastPlayed("dev", itemId.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        _manager.Dispose();

        using var reloaded = new DeviceQueueManager(_tempDir, _logger);
        Assert.Equal(itemId.ToString(), reloaded.GetLastPlayedItemId("dev"));
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, reloaded.GetLastPlayedSnapshot("dev").Route);
    }

    /// <summary>
    /// JF-568 short-circuit pin: the unchanged-item short-circuit must compare the
    /// ROUTE too. The same item re-launched on the other route (the
    /// BuildAudioPlayerResponse native-controls delegation re-records inside
    /// BuildVideoAppAudioResponse, a screenless-degrade play followed by a VideoApp
    /// play of the same item) must flip the stored route, not keep the first one.
    /// </summary>
    [Fact]
    public void RecordLastPlayed_SameItemDifferentRoute_UpdatesRoute()
    {
        var itemId = Guid.NewGuid();
        _manager.RecordLastPlayed("dev", itemId.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        _manager.RecordLastPlayed("dev", itemId.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);

        Assert.Equal(itemId.ToString(), _manager.GetLastPlayedItemId("dev"));
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, _manager.GetLastPlayedSnapshot("dev").Route);
    }

    // JF-619: the ONE device resume truth-source. The scenario that motivated it:
    // an AudioPlayer stop writes the queue pointer (X), a later VideoApp play writes
    // the last-played record (M); both resume entry points must name the SAME item.

    [Fact]
    public void GetDeviceResumePointer_LastPlayedNewer_WinsLastPlayed()
    {
        var stopped = Guid.NewGuid();
        var watched = Guid.NewGuid();
        var queue = _manager.GetOrCreateQueue("dev");
        queue.CurrentItemId = stopped.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        queue.CurrentItemWrittenAt = DateTime.UtcNow.AddMinutes(-10);
        _manager.RecordLastPlayed("dev", watched.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);

        var (itemId, source) = _manager.GetDeviceResumePointer("dev");

        Assert.Equal(watched.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.DeviceResumeSource.LastPlayed, source);
    }

    [Fact]
    public void GetDeviceResumePointer_QueuePointerNewer_WinsQueuePointer()
    {
        var stopped = Guid.NewGuid();
        var watched = Guid.NewGuid();
        _manager.RecordLastPlayed("dev", watched.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var queue = _manager.GetOrCreateQueue("dev");
        queue.CurrentItemId = stopped.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        // Beyond the delayed-stop grace window, so the stop genuinely outranks the launch.
        queue.LastPlayedWrittenAt = DateTime.UtcNow.AddSeconds(-45);
        queue.CurrentItemWrittenAt = DateTime.UtcNow;

        var (itemId, source) = _manager.GetDeviceResumePointer("dev");

        Assert.Equal(stopped.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.DeviceResumeSource.QueuePointer, source);
    }

    [Fact]
    public void GetDeviceResumePointer_StopJustAfterLaunch_LaunchWinsGrace()
    {
        // JF-619 review K4: the voice request pauses the song, the episode launch
        // lands, THEN the paused song's delayed PlaybackStopped stamps the pointer a
        // few seconds later. The launch-time record must keep winning: delayed stops
        // are bookkeeping, launches are intent.
        var song = Guid.NewGuid();
        var episode = Guid.NewGuid();
        _manager.RecordLastPlayed("dev", episode.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var queue = _manager.GetOrCreateQueue("dev");
        queue.CurrentItemId = song.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        queue.CurrentItemWrittenAt = DateTime.UtcNow.AddSeconds(5);
        queue.LastPlayedWrittenAt = DateTime.UtcNow;

        var (itemId, source) = _manager.GetDeviceResumePointer("dev");

        Assert.Equal(episode.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.DeviceResumeSource.LastPlayed, source);
    }

    [Fact]
    public void GetDeviceResumePointer_NullStamps_QueuePointerWins()
    {
        // Pre-JF-619 persisted files carry no stamps: the audio-biased queue pointer
        // must keep winning (the "riprendi after an interrupted song" semantics).
        var stopped = Guid.NewGuid();
        var watched = Guid.NewGuid();
        var queue = _manager.GetOrCreateQueue("dev");
        queue.CurrentItemId = stopped.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        queue.LastPlayedItemId = watched.ToString();

        var (itemId, source) = _manager.GetDeviceResumePointer("dev");

        Assert.Equal(stopped.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.DeviceResumeSource.QueuePointer, source);
    }

    [Fact]
    public void GetDeviceResumePointer_OnlyOneStoreRecorded_WinsIt()
    {
        var only = Guid.NewGuid();
        var queue = _manager.GetOrCreateQueue("dev2");
        queue.LastPlayedItemId = only.ToString();

        var (itemId, source) = _manager.GetDeviceResumePointer("dev2");
        Assert.Equal(only.ToString(), itemId);
        Assert.Equal(DeviceQueueManager.DeviceResumeSource.LastPlayed, source);

        var (none, _) = _manager.GetDeviceResumePointer("unknown-device");
        Assert.Null(none);
    }

    /// <summary>
    /// JF-568 legacy shape: a queue file persisted by a pre-JF-568 plugin carries
    /// no route member, so the read yields null (readers keep the kind-based
    /// classification) and the unknown-member JSON still deserializes.
    /// </summary>
    [Fact]
    public void GetLastPlayedSnapshot_LegacyFileWithoutRoute_NullRoute()
    {
        var itemId = Guid.NewGuid();
        string dir = TestHelpers.CreateRegisteredTempDir("dq-legacy");
        File.WriteAllText(
            Path.Combine(dir, "queue_legacy.json"),
            $"{{\"itemIds\":[],\"currentIndex\":-1,\"repeatMode\":\"None\",\"playbackOrder\":\"Default\","
                + $"\"lastModifiedUtc\":\"2026-09-01T00:00:00Z\",\"currentPositionTicks\":0,"
                + $"\"itemPositionState\":{{}},\"activeLaunchBaseMs\":{{}},\"pendingLaunchBaseMs\":{{}},"
                + $"\"lastPlayedItemId\":\"{itemId}\"}}");

        using var manager = new DeviceQueueManager(dir, _logger);
        Assert.Equal(itemId.ToString(), manager.GetLastPlayedItemId("legacy"));
        Assert.Null(manager.GetLastPlayedSnapshot("legacy").Route);
    }

    /// <summary>
    /// Forward-compat shape (JF-568 constraint): a queue file a NEWER plugin wrote
    /// carries a route name this plugin does not know; the read degrades to null
    /// (legacy semantics) instead of throwing.
    /// </summary>
    [Fact]
    public void GetLastPlayedSnapshot_UnknownRouteName_NullRoute()
    {
        _manager.GetOrCreateQueue("dev").LastPlayedItemId = Guid.NewGuid().ToString();
        _manager.GetOrCreateQueue("dev").LastPlayedLaunchRoute = "Hologram";

        Assert.Null(_manager.GetLastPlayedSnapshot("dev").Route);
    }
}
