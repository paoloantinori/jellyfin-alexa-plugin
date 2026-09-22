using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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
        _manager.TestDebounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); };

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
        _manager.TestDebounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); };

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
        _manager.SetShuffledQueue("device-1", new List<string> { a.ToString(), b.ToString(), c.ToString() }, new Random(9));

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
    // SetShuffledQueue (JF-305: shuffle-at-start playlist qualifier)
    // =====================================================================

    /// <summary>
    /// Non-shuffle playlist-play baseline (JF-305 Chunk 2 regression).
    /// This is the counterpart to <see cref="SetShuffledQueue_ShufflesAllItems_StoresOriginal_SetsShuffleState"/>:
    /// the non-shuffle arm of the playlist play flow (<c>AlbumPlayService.BuildPlaylistPlayResponseAsync</c>)
    /// (shuffle: false) persists the queue via <see cref="DeviceQueueManager.SetQueue"/>
    /// and serves the first ordered track. The persisted <see cref="DeviceQueue"/> MUST
    /// be in <c>Default</c> order with no stored original (pre-shuffle) id list — the
    /// distinguishable state that makes Chunk 3's <c>shuffle:true</c> caller safe to add.
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
    public void SetShuffledQueue_ShufflesAllItems_StoresOriginal_SetsShuffleState()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        _manager.SetShuffledQueue("dev", ids, new Random(42));

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
    public void SetShuffledQueue_MatchesSeededFisherYates()
    {
        List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        List<string> expected = new(ids);
        var rngExpected = new Random(42);
        for (int i = expected.Count - 1; i > 0; i--)
        {
            int j = rngExpected.Next(i + 1);
            (expected[i], expected[j]) = (expected[j], expected[i]);
        }

        _manager.SetShuffledQueue("dev", ids, new Random(42));
        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal(expected, q.ItemIds);
        Assert.Equal(ids, q.OriginalItemIds);
        Assert.NotEqual(ids[0], q.ItemIds[0]);   // position 0 changed — the FR's core requirement
    }

    [Fact]
    public void SetShuffledQueue_SmallQueue_StillSetsState_PreservesItems()
    {
        var ids = new List<string> { "a", "b" };
        _manager.SetShuffledQueue("dev", ids, new Random(1));

        DeviceQueue q = _manager.GetOrCreateQueue("dev");

        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.NotNull(q.OriginalItemIds);
        Assert.Equal(ids, q.OriginalItemIds);
        Assert.Equal(2, q.ItemIds.Count);
    }

    [Fact]
    public void SetShuffledQueue_PreservesItemPositionStateAcrossReset()
    {
        _manager.SetQueue("dev", new List<string> { "a", "b", "c" }, 0);
        _manager.GetOrCreateQueue("dev").ItemPositionState["a"] = 1234L;

        _manager.SetShuffledQueue("dev", new List<string> { "a", "b", "c" }, new Random(9));

        DeviceQueue q = _manager.GetOrCreateQueue("dev");
        Assert.Equal(1234L, q.ItemPositionState["a"]);
    }

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
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, reloaded.GetLastPlayedLaunchRoute("dev"));
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
        Assert.Equal(DeviceQueueManager.LaunchRoute.VideoApp, _manager.GetLastPlayedLaunchRoute("dev"));
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
    public void GetLastPlayedLaunchRoute_LegacyFileWithoutRoute_Null()
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
        Assert.Null(manager.GetLastPlayedLaunchRoute("legacy"));
    }

    /// <summary>
    /// Forward-compat shape (JF-568 constraint): a queue file a NEWER plugin wrote
    /// carries a route name this plugin does not know; the read degrades to null
    /// (legacy semantics) instead of throwing.
    /// </summary>
    [Fact]
    public void GetLastPlayedLaunchRoute_UnknownRouteName_Null()
    {
        _manager.GetOrCreateQueue("dev").LastPlayedItemId = Guid.NewGuid().ToString();
        _manager.GetOrCreateQueue("dev").LastPlayedLaunchRoute = "Hologram";

        Assert.Null(_manager.GetLastPlayedLaunchRoute("dev"));
    }
}
