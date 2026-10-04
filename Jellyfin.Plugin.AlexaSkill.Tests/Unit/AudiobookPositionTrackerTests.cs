using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for AudiobookPositionTracker: high-water-mark Math.Max, conservative (−1 segment)
/// read, zero-when-empty, and Clear. Pure unit test — no Plugin.Instance.
/// </summary>
public class AudiobookPositionTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AudiobookPositionTracker _tracker;

    private const long TicksPerSegment = 10 * TimeSpan.TicksPerSecond; // 10s segments

    public AudiobookPositionTrackerTests()
    {
        _tempDir = TestHelpers.CreateRegisteredTempDir("abpos-test");
        _tracker = new AudiobookPositionTracker(_tempDir, LoggerFactory.Create(b => { }).CreateLogger<AudiobookPositionTracker>());
    }

    public void Dispose()
    {
        _tracker.Dispose();
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, true); } } catch { }
    }

    [Fact]
    public void GetPositionTicks_ReturnsZero_WhenNoData()
    {
        Assert.Equal(0, _tracker.GetPositionTicks("book1"));
    }

    [Fact]
    public void GetPositionTicks_ReturnsZero_WhenEmptyId()
    {
        _tracker.RecordSegment("book1", 5);
        Assert.Equal(0, _tracker.GetPositionTicks(""));
    }

    [Fact]
    public void RecordSegment_KeepsHighWaterMark_OnLowerSegment()
    {
        _tracker.RecordSegment("book1", 5);
        _tracker.RecordSegment("book1", 2); // went back (seek) — must not lower the mark

        // Conservative read: (5 - 1) * 10s
        Assert.Equal(4 * TicksPerSegment, _tracker.GetPositionTicks("book1"));
    }

    [Fact]
    public void GetPositionTicks_IsConservative_OffByOneSegment()
    {
        _tracker.RecordSegment("book1", 1);
        Assert.Equal(0, _tracker.GetPositionTicks("book1")); // (1-1)*10s = 0

        _tracker.RecordSegment("book1", 3);
        Assert.Equal(2 * TicksPerSegment, _tracker.GetPositionTicks("book1")); // (3-1)*10s
    }

    [Fact]
    public void RecordSegment_TracksAcrossBooksIndependently()
    {
        _tracker.RecordSegment("bookA", 10);
        _tracker.RecordSegment("bookB", 3);

        Assert.Equal(9 * TicksPerSegment, _tracker.GetPositionTicks("bookA"));
        Assert.Equal(2 * TicksPerSegment, _tracker.GetPositionTicks("bookB"));
    }

    [Fact]
    public void Clear_RemovesPosition()
    {
        _tracker.RecordSegment("book1", 5);
        Assert.True(_tracker.GetPositionTicks("book1") > 0);

        _tracker.Clear("book1");
        Assert.Equal(0, _tracker.GetPositionTicks("book1"));
    }

    [Fact]
    public void RecordSegment_IgnoresNegativeAndEmpty()
    {
        _tracker.RecordSegment("", 5);
        _tracker.RecordSegment("book1", -1);
        Assert.Equal(0, _tracker.GetPositionTicks("book1"));
    }

    /// <summary>
    /// JF-741 equivalence pin: the tracker's key canonicalization goes through the
    /// ONE shared parse-or-raw rule (DeviceQueueManager.NormalizeToMapKeyFormat),
    /// so the record path's raw URL itemId (dashed) and the resume path's "N" book
    /// key land on ONE entry, every GUID format of the same book is the SAME entry
    /// (the high-water mark advances, never a second book), the shared rule's own
    /// output is a valid READ key for an entry recorded through the record path
    /// (the fold's invariant), Clear removes through any format, and non-GUID ids
    /// keep their raw identity under the Ordinal comparer. Coverage note: before
    /// JF-741 this suite never exercised the GUID re-key arm at all (the "book1"
    /// fixtures are all non-GUID passthrough), so a shared-rule change on GUID
    /// inputs had no tracker-side signal.
    /// </summary>
    [Fact]
    public void RecordReadKeyAgreement_GoesThroughSharedGuidToNRule_JF741()
    {
        Guid book = Guid.NewGuid();

        // Record arm: the raw URL itemId shape (dashed, the GetSegment record path).
        _tracker.RecordSegment(book.ToString(), 5);

        // Read arm: the "N" book key every resume mint site resolves.
        Assert.Equal(4 * TicksPerSegment, _tracker.GetPositionTicks(book.ToString("N")));

        // The fold's invariant: the shared rule's output IS the entry's key.
        Assert.Equal(
            4 * TicksPerSegment,
            _tracker.GetPositionTicks(DeviceQueueManager.NormalizeToMapKeyFormat(book.ToString())));

        // A different GUID format of the SAME book is the SAME entry: the mark
        // advances rather than forking a second book.
        _tracker.RecordSegment(book.ToString("B"), 7);
        Assert.Equal(6 * TicksPerSegment, _tracker.GetPositionTicks(book.ToString("N")));

        // Clear through any format removes the one entry.
        _tracker.Clear(book.ToString("D"));
        Assert.Equal(0, _tracker.GetPositionTicks(book.ToString("N")));

        // Non-GUID ids pass through raw and stay distinct (Ordinal comparer).
        _tracker.RecordSegment("book-raw", 3);
        Assert.Equal(2 * TicksPerSegment, _tracker.GetPositionTicks("book-raw"));
        Assert.Equal(0, _tracker.GetPositionTicks("BOOK-RAW"));
    }

    /// <summary>
    /// JF-741 structural pin (the JF-737 inline-revert-loud precedent): the
    /// parse-or-raw rule must not be re-implemented inside the tracker. Post-fold
    /// the tracker type contains ZERO Guid.TryParse call instructions (its only
    /// pre-fold copy lived in NormalizeKey), and NormalizeKey still exists (the
    /// filing keeps both names) and calls the shared rule
    /// DeviceQueueManager.NormalizeToMapKeyFormat. The behavioral pin above
    /// catches every HARMFUL divergence (a shared-rule change, or a different
    /// rule re-inlined); this pin holds the consolidation itself: a
    /// behaviorally identical re-inline, exactly what a future /simplify pass
    /// would write for a one-line delegation, reds BOTH legs, so undoing the
    /// fold is a conscious pin-widening decision rather than silent drift back
    /// to two copies.
    /// </summary>
    [Fact]
    public void NormalizeKey_DelegatesToSharedRule_NoInlineParseCopy_JF741()
    {
        Module module = typeof(AudiobookPositionTracker).Module;

        // Leg 1: no method anywhere on the tracker type (nested compiler-generated
        // types included, so a lambda-shaped copy cannot hide on a display class)
        // parses a GUID itself.
        foreach (Type type in IlCallScanner.NestedTypeClosure(typeof(AudiobookPositionTracker)))
        {
            foreach (MethodBase method in IlCallScanner.DeclaredCallableMethods(type))
            {
                Assert.False(
                    IlCallScanner.CallsNamedMethod(method, module, "TryParse", typeof(Guid)),
                    $"{type.Name}.{method.Name} must not call Guid.TryParse: the parse-or-raw rule lives in DeviceQueueManager.NormalizeToMapKeyFormat (JF-741)");
            }
        }

        // Leg 2: NormalizeKey delegates to the shared single-string rule.
        MethodInfo? normalizeKey = typeof(AudiobookPositionTracker).GetMethod(
            "NormalizeKey", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(normalizeKey != null, "AudiobookPositionTracker.NormalizeKey must still exist (the JF-741 filing keeps both names)");
        Assert.True(
            IlCallScanner.CallsNamedMethod(normalizeKey, module, "NormalizeToMapKeyFormat", typeof(DeviceQueueManager)),
            "NormalizeKey must call the shared rule DeviceQueueManager.NormalizeToMapKeyFormat");
    }

    [Fact]
    public void Dispose_WritesValidJson_NoTmpRemains()
    {
        _tracker.RecordSegment("book1", 5);
        _tracker.RecordSegment("book2", 3);
        _tracker.Dispose();

        string dataFile = Path.Combine(_tempDir, "audiobook-positions.json");
        Assert.True(File.Exists(dataFile));

        // Reload from a fresh tracker — positions must round-trip
        var reloaded = new AudiobookPositionTracker(_tempDir, LoggerFactory.Create(b => { }).CreateLogger<AudiobookPositionTracker>());
        try
        {
            Assert.Equal(4 * TicksPerSegment, reloaded.GetPositionTicks("book1"));
            Assert.Equal(2 * TicksPerSegment, reloaded.GetPositionTicks("book2"));
        }
        finally
        {
            reloaded.Dispose();
        }

        // No stale .tmp must remain after Dispose
        Assert.False(File.Exists(dataFile + ".tmp"));
    }

    [Fact]
    public void RecordSegment_AfterDispose_DoesNotRewriteFile()
    {
        _tracker.RecordSegment("book1", 5);
        _tracker.Dispose();

        string dataFile = Path.Combine(_tempDir, "audiobook-positions.json");
        Assert.True(File.Exists(dataFile)); // Dispose flushed
        File.Delete(dataFile);

        // Arm-after-dispose race (JF-429): a segment request arriving after
        // Dispose must not re-arm the persist timer and re-write the file
        // after cleanup. Deterministic proof without wall-clock sleeps
        // (JF-449 test-speed note): fire the maximally late straggler
        // manually; a rejected arm leaves the gate nothing to run.
        _tracker.RecordSegment("book2", 7);
        _tracker.FirePersistForTest();

        Assert.False(File.Exists(dataFile));
    }

    [Fact]
    public async Task Dispose_WithInFlightDebounceCallback_FinalFlushWins()
    {
        // JF-449 interleaving (b), AC #2: an in-flight debounce callback shares
        // the .tmp path with the Dispose final flush; a collision's write
        // failure used to be swallowed by the catch, silently persisting the
        // previous on-disk content. Forced deterministically: park the persist
        // payload inside the debounce gate (already started, holding the
        // gate), then Dispose on another thread. The teardown barrier must
        // drain the callback BEFORE the final flush, making the flush the last
        // writer: no swallowed failure, and the flushed state is what persists.
        _tracker.TestDebounce.Interval = TimeSpan.FromSeconds(30); // no natural fire
        _tracker.RecordSegment("book1", 5);

        var started = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        _tracker.TestDebounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); };

        Task callback = Task.Run(() => _tracker.FirePersistForTest());
        Assert.True(started.Wait(TimeSpan.FromSeconds(2))); // callback in flight

        Task dispose = Task.Run(() => _tracker.Dispose());
        Assert.False(await TestHelpers.CompletedWithinAsync(dispose, TimeSpan.FromMilliseconds(150)), "Dispose completed while the debounce callback was still in flight");

        release.Set(); // the callback's write completes; teardown drains it; then the flush runs
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        await callback.WaitAsync(TimeSpan.FromSeconds(2));

        string dataFile = Path.Combine(_tempDir, "audiobook-positions.json");
        Assert.True(File.Exists(dataFile));
        Assert.False(File.Exists(dataFile + ".tmp"));

        // Persisted content is the flushed state, not a stale pre-collision copy.
        var reloaded = new AudiobookPositionTracker(_tempDir, LoggerFactory.Create(b => { }).CreateLogger<AudiobookPositionTracker>());
        try
        {
            Assert.Equal(4 * TicksPerSegment, reloaded.GetPositionTicks("book1"));
        }
        finally
        {
            reloaded.Dispose();
        }
    }

    [Fact]
    public void LoadFromDisk_CleansStaleTmpFile()
    {
        // Create a stale .tmp before construction
        string tmpFile = Path.Combine(_tempDir, "audiobook-positions.json.tmp");
        File.WriteAllText(tmpFile, "{}");

        // Constructor calls LoadFromDisk, which should clean the .tmp
        var freshTracker = new AudiobookPositionTracker(_tempDir, LoggerFactory.Create(b => { }).CreateLogger<AudiobookPositionTracker>());
        try
        {
            Assert.False(File.Exists(tmpFile));
        }
        finally
        {
            freshTracker.Dispose();
        }
    }
}
