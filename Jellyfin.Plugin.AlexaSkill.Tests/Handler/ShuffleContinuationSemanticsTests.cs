#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-928 pins: the mid-playback ShuffleOn/Off channel must update the STORED
/// continuation's per-batch shuffle semantics for the live queue. The defect
/// (issue #44's symptom via a different entry point): an ORDERED windowed play
/// mints a continuation with Shuffle unset; ShuffleOn physically reshuffles only
/// the live 5-track window, and when that window drains
/// <see cref="PlaybackNearlyFinishedEventHandler.TryFetchContinuationBatch"/>
/// appends the rest of the source in source order, so a shuffled handful is
/// followed by the unshuffled tail. The fix: both shuffle handlers re-mint the
/// live continuation through
/// <see cref="QueueContinuationStore.TryUpdateShuffleForLiveQueue"/> (the store's
/// JF-674 identity gate decides WHEN), aligning them with the per-batch shuffle
/// channel the artist path already mints and the fetcher already honors.
/// RED PROOF: with the handlers' store calls removed, the flip assertions fail
/// (the entry keeps Shuffle=false) and the end-to-end pin fails on the appended
/// batch arriving in source order.
/// The end-to-end pin asserts the appended batch's ORDER differs from source
/// order over 12 distinct ids appended in one batch (identity-shuffle
/// probability 1/12! = 1/479M): the same "deterministic in practice" precedent
/// the JF-927 launch-time pin set with seed 42 over 12 ids.
/// </summary>
[Collection("Plugin")]
public class ShuffleContinuationSemanticsTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new("http://localhost:8096");
    private readonly string _deviceId = "jf928-shuffle-cont-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        QueueContinuationStore.Remove(Guid.Empty, _deviceId);
        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();
        return session;
    }

    private static Context ContextWithToken(string token, string deviceId)
    {
        Context c = TestHelpers.CreateTestContext(deviceId);
        c.AudioPlayer = new PlaybackState { Token = token, OffsetInMilliseconds = 0 };
        return c;
    }

    private static IntentRequest ShuffleIntent(string name) => new()
    {
        Type = "IntentRequest",
        Intent = new Intent { Name = name },
        Locale = "en-US",
        RequestId = "jf928"
    };

    /// <summary>
    /// The ordered-window fixture: 17 source tracks, the first 5 installed as the
    /// live window (session queue + device queue), and the continuation an ordered
    /// playlist launch mints (StartIndex at the window edge, Shuffle unset, the
    /// full source cached, the minted page = the window). Current item = window
    /// index 2: the exact geometry where ShuffleRemaining still runs (current &lt;
    /// count - 2) AND the fetch fires (remaining == threshold).
    /// </summary>
    private (SessionInfo Session, List<Audio> Tracks, List<Audio> Window, DeviceQueueManager QueueManager) SetupOrderedWindow()
    {
        List<Audio> tracks = Enumerable.Range(0, 17)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();
        List<Audio> window = tracks.Take(5).ToList();

        SessionInfo session = CreateSession();
        session.FullNowPlayingItem = window[2];
        session.NowPlayingQueue = window.Select(t => new QueueItem { Id = t.Id }).ToList();

        DeviceQueueManager queueManager = TestHelpers.CreateDeviceQueueManager("jf928-shuffle-cont");
        queueManager.SetQueue(_deviceId, window.Select(t => t.Id.ToString()).ToList(), 0);

        _fx.SessionManager.Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        return (session, tracks, window, queueManager);
    }

    private QueueContinuation MintOrderedContinuation(SessionInfo session, List<Audio> tracks, List<Audio> window)
    {
        var continuation = new QueueContinuation
        {
            SourceType = "Playlist",
            StartIndex = window.Count,
            TotalCount = tracks.Count,
            UserId = session.UserId,
            BatchSize = 12,
            CachedTracks = tracks,
            // JF-674: the identity an ordered launch captures (the window page).
            MintedQueueItemIds = QueueContinuation.QueueIdsOf(
                window.Select(t => new QueueItem { Id = t.Id })),
            Shuffle = false
        };
        QueueContinuationStore.Set(session.UserId, _deviceId, continuation);
        return continuation;
    }

    // =====================================================================
    // AC #1: the defect pin
    // =====================================================================

    /// <summary>
    /// THE JF-928 defect pin, end-to-end: an ordered windowed play, then
    /// ShuffleOn, then the window draining into PlaybackNearlyFinished. The
    /// stored continuation must flip to shuffled semantics at the ShuffleOn, and
    /// the appended post-window batch must NOT arrive in source order. Pre-fix,
    /// the entry stayed Shuffle=false and tracks 6..12 appended in source order.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_MidOrderedWindowPlay_FlipsStoredContinuation_AndAppendedBatchIsShuffled()
    {
        (SessionInfo session, List<Audio> tracks, List<Audio> window, DeviceQueueManager queueManager) = SetupOrderedWindow();
        MintOrderedContinuation(session, tracks, window);
        try
        {
            var shuffleOn = new ShuffleOnIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);
            await shuffleOn.HandleAsync(
                ShuffleIntent(IntentNames.AmazonShuffleOn),
                ContextWithToken(window[2].Id.ToString(), _deviceId),
                TestHelpers.CreateTestUser(), session, default);

            // The store entry's shuffle semantics flipped (the deterministic half
            // of the pin; the batch order below is its end-to-end consequence).
            QueueContinuation? updated = QueueContinuationStore.Get(session.UserId, _deviceId);
            Assert.NotNull(updated);
            Assert.True(updated!.Shuffle, "ShuffleOn mid-playback must flip the stored continuation to shuffled batches (the JF-928 defect: the entry stayed ordered and the tail appended in source order)");

            // The re-mint carried every other field verbatim: identity, offsets,
            // cached source. A flip that wipes the entry would strand the tail.
            Assert.Equal(5, updated.StartIndex);
            Assert.Equal(17, updated.TotalCount);
            Assert.Equal("Playlist", updated.SourceType);
            Assert.Equal(session.UserId, updated.UserId);
            Assert.Equal(window.Select(t => t.Id), updated.MintedQueueItemIds);
            Assert.Same(tracks, updated.CachedTracks);

            // End-to-end: fire NearlyFinished at the window edge (index 2 of the
            // 5-track window, remaining == threshold 2) so the fetch runs. The
            // 12 remaining source tracks append SHUFFLED in one batch (BatchSize
            // 12), never in source order: identity-shuffle probability 1/12! =
            // 1/479M, the same "deterministic in practice" bar as the JF-927
            // launch-time pin.
            var nearly = new PlaybackNearlyFinishedEventHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.LoggerFactory, queueManager);
            foreach (Audio t in window)
            {
                _fx.LibraryManager.Setup(l => l.GetItemById(t.Id)).Returns(t);
            }

            await nearly.HandleAsync(
                TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", window[2].Id.ToString()),
                ContextWithToken(window[2].Id.ToString(), _deviceId),
                TestHelpers.CreateTestUser(), session, default);

            Assert.Equal(17, session.NowPlayingQueue.Count);
            List<Guid> appended = session.NowPlayingQueue.Skip(5).Select(q => q.Id).ToList();
            List<Guid> expectedTail = tracks.Skip(5).Select(t => t.Id).ToList();
            Assert.Equal(expectedTail.Count, appended.Count);
            Assert.Equal(new HashSet<Guid>(expectedTail), new HashSet<Guid>(appended));
            Assert.NotEqual(expectedTail, appended);
        }
        finally
        {
            queueManager.Dispose();
        }
    }

    // =====================================================================
    // AC #2 + #4: shuffle-off reverts to ordered batches (from both entry
    // shapes: after a mid-playback shuffle-on, and on an artist-shaped entry
    // minted Shuffle=true by the config)
    // =====================================================================

    /// <summary>
    /// ShuffleOff after a mid-playback ShuffleOn restores ORDERED continuation
    /// batches (the store flip), AND still restores the pre-shuffle queue order
    /// through RestoreOrder (AC #3: the queue restore path untouched).
    /// </summary>
    [Fact]
    public async Task ShuffleOff_AfterMidPlayShuffleOn_RestoresOrderedBatches_AndQueueOrder()
    {
        (SessionInfo session, List<Audio> tracks, List<Audio> window, DeviceQueueManager queueManager) = SetupOrderedWindow();
        MintOrderedContinuation(session, tracks, window);
        try
        {
            var shuffleOn = new ShuffleOnIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);
            var shuffleOff = new ShuffleOffIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);
            var context = ContextWithToken(window[2].Id.ToString(), _deviceId);
            var user = TestHelpers.CreateTestUser();

            await shuffleOn.HandleAsync(ShuffleIntent(IntentNames.AmazonShuffleOn), context, user, session, default);
            Assert.True(QueueContinuationStore.Get(session.UserId, _deviceId)!.Shuffle);

            await shuffleOff.HandleAsync(ShuffleIntent(IntentNames.AmazonShuffleOff), context, user, session, default);

            QueueContinuation? reverted = QueueContinuationStore.Get(session.UserId, _deviceId);
            Assert.NotNull(reverted);
            Assert.False(reverted!.Shuffle, "ShuffleOff must restore ordered continuation batches");
            Assert.Equal(5, reverted.StartIndex);
            Assert.Equal(17, reverted.TotalCount);
            Assert.Equal(window.Select(t => t.Id), reverted.MintedQueueItemIds);
            Assert.Same(tracks, reverted.CachedTracks);

            // AC #3: the queue restore path is intact (original window order back,
            // snapshot consumed, flag cleared).
            DeviceQueue q = queueManager.GetQueue(_deviceId)!;
            Assert.Equal(window.Select(t => t.Id.ToString()).ToList(), q.ItemIds);
            Assert.Null(q.OriginalItemIds);
            Assert.Equal("Default", q.PlaybackOrder);
        }
        finally
        {
            queueManager.Dispose();
        }
    }

    /// <summary>
    /// The off twin on the ARTIST-shaped entry: a play minted with
    /// Shuffle=true (the PlayArtistSongsIntentHandler/_config.ShuffleArtistSongs
    /// channel) followed by an explicit shuffle-off must stop shuffling appended
    /// batches too, not let the config default reassert at every fetch.
    /// </summary>
    [Fact]
    public async Task ShuffleOff_ArtistShuffledEntry_RestoresOrderedBatches()
    {
        SessionInfo session = CreateSession();
        List<Audio> tracks = Enumerable.Range(0, 10)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Song {i + 1}" })
            .ToList();
        List<Audio> window = tracks.Take(5).ToList();
        session.FullNowPlayingItem = window[0];
        session.NowPlayingQueue = window.Select(t => new QueueItem { Id = t.Id }).ToList();

        DeviceQueueManager queueManager = TestHelpers.CreateDeviceQueueManager("jf928-artist-off");
        queueManager.SetQueue(_deviceId, window.Select(t => t.Id.ToString()).ToList(), 0);
        _fx.SessionManager.Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = SearchService.UnknownTotal,
            UserId = session.UserId,
            SortOrder = new[] { (Jellyfin.Data.Enums.ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) },
            Shuffle = true,
            MintedQueueItemIds = window.Select(t => t.Id).ToList()
        };
        QueueContinuationStore.Set(session.UserId, _deviceId, continuation);
        try
        {
            var shuffleOff = new ShuffleOffIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);
            await shuffleOff.HandleAsync(
                ShuffleIntent(IntentNames.AmazonShuffleOff),
                ContextWithToken(window[0].Id.ToString(), _deviceId),
                TestHelpers.CreateTestUser(), session, default);

            QueueContinuation? reverted = QueueContinuationStore.Get(session.UserId, _deviceId);
            Assert.NotNull(reverted);
            Assert.False(reverted!.Shuffle);
            Assert.Equal(SearchService.UnknownTotal, reverted.TotalCount);
            Assert.Equal(continuation.MintedQueueItemIds, reverted.MintedQueueItemIds);
        }
        finally
        {
            queueManager.Dispose();
        }
    }

    // =====================================================================
    // AC #4: the no-continuation (short queue) shape
    // =====================================================================

    /// <summary>
    /// The twins on a short queue with NO stored continuation: the handlers keep
    /// working (the shuffle confirmation Tell) and the store stays empty (the
    /// update is a no-op, never a synthetic entry).
    /// </summary>
    [Theory]
    [InlineData(IntentNames.AmazonShuffleOn)]
    [InlineData(IntentNames.AmazonShuffleOff)]
    public async Task ShuffleTwins_ShortQueue_NoContinuation_NoStoreWrite(string intentName)
    {
        SessionInfo session = CreateSession();
        List<Guid> guids = new() { Guid.NewGuid(), Guid.NewGuid() };
        session.FullNowPlayingItem = new Audio { Id = guids[0], Name = "Song" };
        session.NowPlayingQueue = guids.Select(g => new QueueItem { Id = g }).ToList();

        DeviceQueueManager queueManager = TestHelpers.CreateDeviceQueueManager("jf928-no-cont");
        queueManager.SetQueue(_deviceId, guids.Select(g => g.ToString()).ToList(), 0);
        _fx.SessionManager.Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        try
        {
            BaseHandler handler = intentName == IntentNames.AmazonShuffleOn
                ? new ShuffleOnIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager)
                : new ShuffleOffIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);

            SkillResponse response = await handler.HandleAsync(
                ShuffleIntent(intentName),
                ContextWithToken(guids[0].ToString(), _deviceId),
                TestHelpers.CreateTestUser(), session, default);

            Assert.NotNull(response);
            Assert.Null(QueueContinuationStore.Get(session.UserId, _deviceId));
        }
        finally
        {
            queueManager.Dispose();
        }
    }

    // =====================================================================
    // The JF-674 gate: a foreign continuation is never mutated
    // =====================================================================

    /// <summary>
    /// A stored continuation whose minted page is no longer a subset of the live
    /// queue (it belongs to an earlier playback) must survive a shuffle-on
    /// UNCHANGED: the store's identity gate refuses the re-mint, and the
    /// fetch-time guard owns the discard. The Same() reference pin proves no
    /// re-mint happened (the flip path necessarily builds a new instance).
    /// </summary>
    [Fact]
    public async Task ShuffleOn_ForeignContinuation_IsNeverMutated()
    {
        (SessionInfo session, List<Audio> tracks, List<Audio> window, DeviceQueueManager queueManager) = SetupOrderedWindow();
        var foreign = new QueueContinuation
        {
            SourceType = "Playlist",
            StartIndex = 5,
            TotalCount = 12,
            UserId = session.UserId,
            CachedTracks = tracks,
            // The foreign mark: an id the live queue does not contain.
            MintedQueueItemIds = window.Select(t => t.Id).Append(Guid.NewGuid()).ToList(),
            Shuffle = false
        };
        QueueContinuationStore.Set(session.UserId, _deviceId, foreign);
        try
        {
            var shuffleOn = new ShuffleOnIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);
            await shuffleOn.HandleAsync(
                ShuffleIntent(IntentNames.AmazonShuffleOn),
                ContextWithToken(window[2].Id.ToString(), _deviceId),
                TestHelpers.CreateTestUser(), session, default);

            QueueContinuation? stored = QueueContinuationStore.Get(session.UserId, _deviceId);
            Assert.NotNull(stored);
            Assert.Same(foreign, stored);
            Assert.False(stored!.Shuffle);
        }
        finally
        {
            queueManager.Dispose();
        }
    }

    // =====================================================================
    // The store API's own contract (the handlers' single entry point)
    // =====================================================================

    /// <summary>
    /// The store API's return-value contract: false with no entry, true plus the
    /// flip on a live one. The handlers' tests above pin the wiring; this pins
    /// the API itself.
    /// </summary>
    [Fact]
    public void TryUpdateShuffleForLiveQueue_NoEntry_False_LiveEntry_TrueAndFlip()
    {
        SessionInfo session = CreateSession();
        List<Guid> window = new() { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        session.NowPlayingQueue = window.Select(g => new QueueItem { Id = g }).ToList();

        // No entry: false, nothing created.
        Assert.False(QueueContinuationStore.TryUpdateShuffleForLiveQueue(session.UserId, _deviceId, session, shuffle: true));
        Assert.Null(QueueContinuationStore.Get(session.UserId, _deviceId));

        // Live entry: true, flip to shuffled, fields carried.
        var entry = new QueueContinuation
        {
            SourceType = "Playlist",
            StartIndex = 3,
            TotalCount = 9,
            UserId = session.UserId,
            MintedQueueItemIds = window
        };
        QueueContinuationStore.Set(session.UserId, _deviceId, entry);

        Assert.True(QueueContinuationStore.TryUpdateShuffleForLiveQueue(session.UserId, _deviceId, session, shuffle: true));
        QueueContinuation? shuffled = QueueContinuationStore.Get(session.UserId, _deviceId);
        Assert.NotNull(shuffled);
        Assert.True(shuffled!.Shuffle);
        Assert.Equal(3, shuffled.StartIndex);
        Assert.Equal(9, shuffled.TotalCount);
        Assert.Equal(window, shuffled.MintedQueueItemIds);

        // And back.
        Assert.True(QueueContinuationStore.TryUpdateShuffleForLiveQueue(session.UserId, _deviceId, session, shuffle: false));
        Assert.False(QueueContinuationStore.Get(session.UserId, _deviceId)!.Shuffle);

        QueueContinuationStore.Remove(session.UserId, _deviceId);
    }

    /// <summary>
    /// The music-source gate (merge-review round): the flip applies to the three
    /// playlist-shaped MUSIC sources; every flip below is a live entry the store
    /// re-mints. A future fourth source type must add itself to the store gate
    /// explicitly rather than inherit shuffle semantics silently.
    /// </summary>
    [Theory]
    [InlineData("Playlist")]
    [InlineData("Album")]
    [InlineData("Artist")]
    public void TryUpdateShuffleForLiveQueue_MusicSourcesFlip(string sourceType)
    {
        SessionInfo session = CreateSession();
        List<Guid> window = new() { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        session.NowPlayingQueue = window.Select(g => new QueueItem { Id = g }).ToList();

        var entry = new QueueContinuation
        {
            SourceType = sourceType,
            StartIndex = 2,
            TotalCount = 8,
            UserId = session.UserId,
            MintedQueueItemIds = window
        };
        QueueContinuationStore.Set(session.UserId, _deviceId, entry);

        Assert.True(QueueContinuationStore.TryUpdateShuffleForLiveQueue(session.UserId, _deviceId, session, shuffle: true));
        Assert.True(QueueContinuationStore.Get(session.UserId, _deviceId)!.Shuffle);
        QueueContinuationStore.Remove(session.UserId, _deviceId);
    }

    /// <summary>
    /// THE audiobook-exclusion pin (merge-review BLOCKING finding): a live
    /// Audiobook continuation (the screenless chapter-resume shape the JF-674 doc
    /// keeps on the AudioPlayer path) must survive a mid-book shuffle-on
    /// UNCHANGED. The fetcher's ShuffleCopy gates on nothing but the flag, so a
    /// flip here would scatter the remaining chapters; ordered chapters are
    /// never shuffled. Same() proves no re-mint happened.
    /// </summary>
    [Fact]
    public async Task ShuffleOn_AudiobookContinuation_IsNeverScattered()
    {
        SessionInfo session = CreateSession();
        List<Guid> chapters = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        session.FullNowPlayingItem = new Audio { Id = chapters[0], Name = "Chapter 1" };
        session.NowPlayingQueue = chapters.Select(g => new QueueItem { Id = g }).ToList();

        DeviceQueueManager queueManager = TestHelpers.CreateDeviceQueueManager("jf928-audiobook");
        queueManager.SetQueue(_deviceId, chapters.Select(g => g.ToString()).ToList(), 0);
        _fx.SessionManager.Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        var book = new QueueContinuation
        {
            SourceType = "Audiobook",
            ParentId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 14,
            UserId = session.UserId,
            MintedQueueItemIds = chapters
        };
        QueueContinuationStore.Set(session.UserId, _deviceId, book);
        try
        {
            var shuffleOn = new ShuffleOnIntentHandler(_fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.LoggerFactory, queueManager);
            await shuffleOn.HandleAsync(
                ShuffleIntent(IntentNames.AmazonShuffleOn),
                ContextWithToken(chapters[0].ToString(), _deviceId),
                TestHelpers.CreateTestUser(), session, default);

            QueueContinuation? stored = QueueContinuationStore.Get(session.UserId, _deviceId);
            Assert.NotNull(stored);
            Assert.Same(book, stored);
            Assert.False(stored!.Shuffle);
        }
        finally
        {
            queueManager.Dispose();
            QueueContinuationStore.Remove(session.UserId, _deviceId);
        }
    }

    // =====================================================================
    // F4 (code-review round): the clone-completeness structural guard
    // =====================================================================

    /// <summary>
    /// WithShuffleSemantics is a hand-enumerated field copy the compiler cannot
    /// keep complete, and the identity roster scans init-assign sites, not clone
    /// completeness, so a property added to QueueContinuation later would be
    /// silently reset to its default by the first mid-playback flip. This pin
    /// walks every public instance property and fails when the re-mint drops or
    /// alters one; Shuffle is the deliberate exception (the flip). A new
    /// QueueContinuation property goes red here until the clone carries it.
    /// </summary>
    [Fact]
    public void WithShuffleSemantics_CarriesEveryProperty_ExceptTheFlippedOne()
    {
        var original = new QueueContinuation
        {
            SourceType = "Playlist",
            ParentId = Guid.NewGuid(),
            ArtistId = Guid.NewGuid(),
            PlaylistId = Guid.NewGuid(),
            StartIndex = 7,
            TotalCount = 99,
            BatchSize = 4,
            UserId = Guid.NewGuid(),
            SortOrder = new[] { (Jellyfin.Data.Enums.ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending) },
            Shuffle = false,
            CachedTracks = new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "T" } },
            MintedQueueItemIds = new[] { Guid.NewGuid() }
        };

        QueueContinuation clone = original.WithShuffleSemantics(shuffle: true);
        Assert.True(clone.Shuffle);

        foreach (System.Reflection.PropertyInfo prop in typeof(QueueContinuation)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.Name == nameof(QueueContinuation.Shuffle))
            {
                continue;
            }

            object? expected = prop.GetValue(original);
            object? actual = prop.GetValue(clone);
            if (expected is System.Collections.IEnumerable sequence and not string)
            {
                // Sequence equality, not reference equality: a future CORRECT
                // defensive copy of a collection-typed property must not false-red
                // the guard (the pin catches DROPPED fields, not copied-by-value).
                Assert.True(
                    actual is System.Collections.IEnumerable,
                    $"WithShuffleSemantics dropped collection property {prop.Name}; the re-mint clone must carry every QueueContinuation field.");
                Assert.Equal(
                    sequence.Cast<object?>().ToList(),
                    ((System.Collections.IEnumerable)actual!).Cast<object?>().ToList());
                continue;
            }

            Assert.True(
                Equals(expected, actual),
                $"WithShuffleSemantics dropped or altered property {prop.Name} (expected {expected}, got {actual}); " +
                "the re-mint clone must carry every QueueContinuation field verbatim.");
        }
    }
}
