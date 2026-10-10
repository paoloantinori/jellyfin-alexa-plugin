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
[Collection("TimingSolo")]
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
        // The park bound is 60s, not 5s: the assembly parallel phase (JF-792) can starve
        // the test thread past 5s, and an expired park corrupts the ordering witness.
        _tracker.TestDebounce.BeforeCallbackGate = () => { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); };

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

    // ---- JF-787: the timeline-identity axis ----

    /// <summary>
    /// The JF-787 tracker-level proof: a position seeded under one timeline
    /// identity READS AS ABSENT for the serve on another timeline (the
    /// resolver answers 0, so the serve drops the slice), while the same
    /// timeline's serve resolves the mark and the identity-agnostic mint read
    /// (the raw read) keeps returning the mark. Pre-fix there was no identity
    /// axis at all: every timeline read the stale offset (the red controller
    /// pin, StreamHlsAudiobook_ResumeSlice_TrackerPositionFromForeignTimeline_IsDropped,
    /// proves the served slice end to end on the sabotaged tree).
    /// </summary>
    [Fact]
    public void TimelineIdentity_ForeignTimeline_ReadsAsAbsent_SameTimeline_ResolvesMark()
    {
        long timelineA = TimeSpan.FromHours(8).Ticks;
        long timelineB = TimeSpan.FromHours(6).Ticks;

        // Seeded under timeline A (45 chapters / 8h), mark 300 reads 49:50.
        _tracker.RecordSegment("book-tl", 300, new TimelineIdentity(45, timelineA));

        // Foreign timeline B's serve: the position reads as ABSENT (0) for it.
        Assert.Equal(0, _tracker.ResolveServeSliceTicks("book-tl", 30, timelineB, 20 * TicksPerSegment));

        // Same timeline A's serve: resolves to the mark (the minted value was
        // byte-identical in the common case).
        Assert.Equal(299 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-tl", 45, timelineA, 299 * TicksPerSegment));

        // The raw mint read keeps today's behavior: the best-known mark.
        Assert.Equal(299 * TicksPerSegment, _tracker.GetPositionTicks("book-tl"));

        // A cold key passes the minted slice through (the Yes-side resume
        // fallback's cold-tracker row).
        Assert.Equal(20 * TicksPerSegment, _tracker.ResolveServeSliceTicks("never-recorded", 30, timelineB, 20 * TicksPerSegment));
    }

    /// <summary>
    /// The JF-787 duration axis: an equal chapter COUNT with a different runtime
    /// sum is a different timeline (the JF-784 verdict's same discipline).
    /// </summary>
    [Fact]
    public void TimelineIdentity_CountEqualDurationDifferent_IsForeign()
    {
        long eightHours = TimeSpan.FromHours(8).Ticks;
        _tracker.RecordSegment("book-dur", 100, new TimelineIdentity(45, eightHours));

        Assert.Equal(0, _tracker.ResolveServeSliceTicks("book-dur", 45, eightHours / 2, 99 * TicksPerSegment));
        Assert.Equal(99 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-dur", 45, eightHours, 99 * TicksPerSegment));
    }

    /// <summary>
    /// The JF-787 legacy decision (the JF-812 kindless-legacy precedent): an
    /// IDENTITY-LESS entry (a pre-upgrade persisted position, or a write from a
    /// sidecar-less cache) is ambiguous between same-timeline and foreign, and
    /// the conservative choice keeps TODAY's behavior for it: the minted slice
    /// passes through under every timeline. Dropping it would regress the
    /// legit same-timeline resumes that are the common case.
    /// </summary>
    [Fact]
    public void TimelineIdentity_LegacyIdentitylessEntry_PassesMintedSliceThrough()
    {
        _tracker.RecordSegment("book-legacy", 50); // no identity stamped

        Assert.Equal(49 * TicksPerSegment, _tracker.GetPositionTicks("book-legacy"));
        Assert.Equal(49 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-legacy", 1, 0, 49 * TicksPerSegment));
        Assert.Equal(49 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-legacy", 999, 999L, 49 * TicksPerSegment));

        // Identity-carrying writes advance their OWN slots alongside; the legacy
        // slot is untouched and still passes its slice through.
        _tracker.RecordSegment("book-legacy", 60, new TimelineIdentity(5, 500));
        Assert.Equal(59 * TicksPerSegment, _tracker.GetPositionTicks("book-legacy"));
        Assert.Equal(49 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-legacy", 999, 999L, 49 * TicksPerSegment));
        Assert.Equal(59 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-legacy", 5, 500, 59 * TicksPerSegment));
    }

    /// <summary>
    /// The JF-787 review F1/F5 independence pin: slots are keyed by timeline,
    /// so a foreign-timeline write (a fetch served another scope's running
    /// encode, below OR above the listener's own mark) can neither destroy,
    /// overwrite, nor floor the listener's same-timeline slot. The
    /// pre-redesign single-slot reset rule destroyed the mark on any
    /// identity-bearing foreign write; the slot design has no cross-slot
    /// interaction at all.
    /// </summary>
    [Fact]
    public void TimelineIdentity_ForeignWritesNeverTouchTheOwnTimelineSlot()
    {
        long a = 1000, b = 2000;

        // The listener's own timeline B holds mark 450; scope A's device fetches
        // seg_0003 (below) and later seg_0500 (above) during A-encoded serves.
        _tracker.RecordSegment("book-slots", 450, new TimelineIdentity(30, b));
        _tracker.RecordSegment("book-slots", 3, new TimelineIdentity(45, a));
        _tracker.RecordSegment("book-slots", 500, new TimelineIdentity(45, a));

        // B's serve resolves to B's OWN 450 mark (not A's 500, not 0).
        Assert.Equal(449 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-slots", 30, b, 449 * TicksPerSegment));

        // The raw mint read answers the best-known mark (A's 500): the announce
        // may overstate; the serve's resolver corrects it (the documented
        // bounded residual).
        Assert.Equal(499 * TicksPerSegment, _tracker.GetPositionTicks("book-slots"));
    }

    /// <summary>
    /// The JF-787 suppression pin: the new timeline's own progress accumulates
    /// from its first fetch even while a HIGHER foreign slot exists (the
    /// pre-JF-787 single high-water mark floored B's writes below A's mark, so
    /// B could never accumulate a position of its own).
    /// </summary>
    [Fact]
    public void TimelineIdentity_NewTimelineAccumulates_BelowAHigherForeignSlot()
    {
        long a = 1000, b = 2000;

        // A's foreign mark at segment 300; B's own playback reaches only 5.
        _tracker.RecordSegment("book-new", 300, new TimelineIdentity(45, a));
        _tracker.RecordSegment("book-new", 5, new TimelineIdentity(30, b));

        // B's serve resolves to B's own (5-1)*10s mark, not the foreign 300;
        // A's own mark survives in its own slot (every timeline keeps its own
        // truth) and still serves A's resume.
        Assert.Equal(4 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-new", 30, b, 4 * TicksPerSegment));
        Assert.Equal(299 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-new", 45, a, 299 * TicksPerSegment));

        // Same timeline: backward fetches stay ignored (monotonic per slot).
        _tracker.RecordSegment("book-new", 2, new TimelineIdentity(30, b));
        Assert.Equal(4 * TicksPerSegment, _tracker.ResolveServeSliceTicks("book-new", 30, b, 4 * TicksPerSegment));
    }

    /// <summary>
    /// The JF-787 review F3 pin: a segment-0 fetch creates NO slot (its
    /// conservative read is 0 anyway), so a fresh play-from-0 never arms the
    /// gate against a legitimately minted non-tracker slice.
    /// </summary>
    [Fact]
    public void TimelineIdentity_SegmentZeroWrite_CreatesNoSlot()
    {
        _tracker.RecordSegment("book-zero", 0, new TimelineIdentity(12, 3000));

        Assert.Equal(0, _tracker.GetPositionTicks("book-zero"));
        Assert.Equal(
            12 * TicksPerSegment,
            _tracker.ResolveServeSliceTicks("book-zero", 30, 9999L, 12 * TicksPerSegment));
    }

    /// <summary>
    /// The JF-787 persistence round trip: the slots (marks + identities)
    /// survive Dispose/reload, and the persisted shape loads BOTH ways for
    /// pre-upgrade files (the bare-number legacy entries load identity-less
    /// and keep today's honored read; array entries load with their
    /// identities).
    /// </summary>
    [Fact]
    public void TimelineIdentity_PersistsAcrossReload_LegacyNumbersStillLoad()
    {
        _tracker.RecordSegment("book-keep", 100, new TimelineIdentity(12, 3000));
        _tracker.RecordSegment("book-keep", 40, new TimelineIdentity(15, 5000));
        _tracker.Dispose();

        string dataFile = Path.Combine(_tempDir, "audiobook-positions.json");
        Assert.True(File.Exists(dataFile));

        // Graft a pre-upgrade bare-number entry next to the new-shape entry
        // (splice before the dict's single closing brace).
        string json = File.ReadAllText(dataFile);
        File.WriteAllText(dataFile, json[..^1] + ",\"book-old\":42}");

        var reloaded = new AudiobookPositionTracker(_tempDir, LoggerFactory.Create(b => { }).CreateLogger<AudiobookPositionTracker>());
        try
        {
            // New shape: both slots' identities intact, the resolver still
            // discriminates.
            Assert.Equal(99 * TicksPerSegment, reloaded.ResolveServeSliceTicks("book-keep", 12, 3000, 99 * TicksPerSegment));
            Assert.Equal(39 * TicksPerSegment, reloaded.ResolveServeSliceTicks("book-keep", 15, 5000, 39 * TicksPerSegment));
            Assert.Equal(0, reloaded.ResolveServeSliceTicks("book-keep", 13, 3000, 99 * TicksPerSegment));

            // Legacy number: identity-less, honored under every timeline.
            Assert.Equal(41 * TicksPerSegment, reloaded.GetPositionTicks("book-old"));
            Assert.Equal(
                41 * TicksPerSegment,
                reloaded.ResolveServeSliceTicks("book-old", 77, 7777L, 41 * TicksPerSegment));
        }
        finally
        {
            reloaded.Dispose();
        }
    }
}
