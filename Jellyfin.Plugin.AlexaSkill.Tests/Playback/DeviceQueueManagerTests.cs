using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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
        _tempDir = Path.Combine(Path.GetTempPath(), $"dq-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
}
