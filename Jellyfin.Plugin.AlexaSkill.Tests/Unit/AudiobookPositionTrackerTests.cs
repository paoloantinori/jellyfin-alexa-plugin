using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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
        _tempDir = Path.Combine(Path.GetTempPath(), "abpos-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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
