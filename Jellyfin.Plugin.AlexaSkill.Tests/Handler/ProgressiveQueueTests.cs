using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using BaseItem = MediaBrowser.Controller.Entities.BaseItem;
using InternalItemsQuery = MediaBrowser.Controller.Entities.InternalItemsQuery;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for progressive queue building (JF-124).
/// Verifies that bulk-play handlers fetch only an initial page of items,
/// store continuation state, and that PlaybackNearlyFinished fetches more
/// items on demand.
/// </summary>
[Collection("Plugin")]
public class ProgressiveQueueTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096", configure: c => c.AsrCompoundWordFixEnabled = false);

    private static readonly string DeviceId = "test-device";

    public ProgressiveQueueTests()
    {
        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
    }

    public void Dispose()
    {
        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
        GC.SuppressFinalize(this);
    }

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();
        return session;
    }

    private static Context CreateContext(string? token = null)
    {
        var context = TestHelpers.CreateTestContext();
        if (token != null)
        {
            context.AudioPlayer = new PlaybackState { Token = token, OffsetInMilliseconds = 0 };
        }

        return context;
    }

    private static IntentRequest CreateAlbumIntent(string album, string? musician = null)
    {
        var slots = new Dictionary<string, Slot>();
        if (album != null)
        {
            slots["album"] = new Slot { Value = album };
        }

        if (musician != null)
        {
            slots["musician"] = new Slot { Value = musician };
        }

        return new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent
            {
                Name = IntentNames.PlayAlbum,
                Slots = slots
            }
        };
    }

    private static IntentRequest CreateArtistSongsIntent(string musician)
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent
            {
                Name = IntentNames.PlayArtistSongs,
                Slots = new Dictionary<string, Slot>
                {
                    ["musician"] = new Slot { Value = musician }
                }
            }
        };
    }

    private static IntentRequest CreatePlaylistIntent(string playlist)
    {
        return new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent
            {
                Name = IntentNames.PlayPlaylist,
                Slots = new Dictionary<string, Slot>
                {
                    ["playlist"] = new Slot { Value = playlist }
                }
            }
        };
    }

    private static AudioPlayerRequest CreateNearlyFinishedRequest(string? token = null)
        => TestHelpers.CreateAudioPlayerEventRequest("AudioPlayer.PlaybackNearlyFinished", token);

    private PlaybackNearlyFinishedEventHandler CreatePlaybackHandler(ILoggerFactory? loggerFactory = null)
    {
        return new PlaybackNearlyFinishedEventHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            loggerFactory ?? _fx.LoggerFactory);
    }

    // =====================================================================
    // QueueContinuationStore tests
    // =====================================================================

    [Fact]
    public void QueueContinuationStore_SetAndGet_RoundTrips()
    {
        var userId = Guid.NewGuid();
        string deviceId = "test-device";
        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 20,
            UserId = userId
        };

        QueueContinuationStore.Set(userId, deviceId, continuation);
        QueueContinuation? retrieved = QueueContinuationStore.Get(userId, deviceId);

        Assert.NotNull(retrieved);
        Assert.Equal("Album", retrieved.SourceType);
        Assert.Equal(continuation.ParentId, retrieved.ParentId);
        Assert.Equal(5, retrieved.StartIndex);
        Assert.Equal(20, retrieved.TotalCount);

        // Cleanup
        QueueContinuationStore.Remove(userId, deviceId);
    }

    [Fact]
    public void QueueContinuationStore_Get_NotFound_ReturnsNull()
    {
        QueueContinuation? result = QueueContinuationStore.Get(Guid.NewGuid(), "nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public void QueueContinuationStore_Remove_ClearsState()
    {
        var userId = Guid.NewGuid();
        string deviceId = "test-device-remove";
        var continuation = new QueueContinuation { SourceType = "Album", UserId = userId };

        QueueContinuationStore.Set(userId, deviceId, continuation);
        Assert.NotNull(QueueContinuationStore.Get(userId, deviceId));

        QueueContinuationStore.Remove(userId, deviceId);
        Assert.Null(QueueContinuationStore.Get(userId, deviceId));
    }

    [Fact]
    public void QueueContinuationStore_RemoveAllForUser_CleansUpAllDevices()
    {
        var userId = Guid.NewGuid();
        var continuation = new QueueContinuation { SourceType = "Artist", UserId = userId };

        QueueContinuationStore.Set(userId, "device1", continuation);
        QueueContinuationStore.Set(userId, "device2", continuation);

        QueueContinuationStore.RemoveAllForUser(userId);

        Assert.Null(QueueContinuationStore.Get(userId, "device1"));
        Assert.Null(QueueContinuationStore.Get(userId, "device2"));
    }

    // =====================================================================
    // QueueContinuation DTO tests
    // =====================================================================

    [Fact]
    public void QueueContinuation_DefaultBatchSize_MatchesConstant()
    {
        var continuation = new QueueContinuation();
        Assert.Equal(ProgressiveQueueConstants.GetContinuationBatchSize(), continuation.BatchSize);
    }

    // =====================================================================
    // PlaybackNearlyFinished - Progressive queue continuation
    // =====================================================================

    [Fact]
    public async Task PlaybackNearlyFinished_WithContinuation_FetchesMoreItems()
    {
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        // Create initial queue with 3 items (less than prefetch threshold + 2)
        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();
        var track4Id = Guid.NewGuid();
        var track5Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };

        const string deviceId = "test-device";

        // Set up continuation state
        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = Guid.NewGuid(),
            StartIndex = 3,
            TotalCount = 10,
            UserId = Guid.NewGuid(),
            BatchSize = 3
        };
        QueueContinuationStore.Set(session.UserId, deviceId, continuation);

        // Set up library to return next batch
        var track4 = new Audio { Id = track4Id, Name = "Track 4" };
        var track5 = new Audio { Id = track5Id, Name = "Track 5" };

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { track4, track5 },
                TotalRecordCount = 2
            });

        _fx.LibraryManager.Setup(l => l.GetItemById(track2Id))
            .Returns(new Audio { Id = track2Id, Name = "Track 2" });

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Verify queue was extended
        Assert.True(session.NowPlayingQueue.Count > 3, "Queue should have been extended with new items");

        // Verify the next track is returned for playback
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(PlayBehavior.Enqueue, directive.PlayBehavior);

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, deviceId);
    }

    // JF-666: the continuation fetch must run BEFORE the precompute cache-hit early
    // return. A cache-served NearlyFinished within the prefetch window still extends
    // the queue; with the fetch placed after the early return, the whole initial page
    // was served from the cache and the queue starved until its last track (live:
    // "musica di norah jones" stopped at 5 tracks and radio-switched away).
    [Fact]
    public async Task PlaybackNearlyFinished_CacheHitWithinPrefetchWindow_StillFetchesContinuation()
    {
        _fx.Config.PreEnqueueOnStart = true;
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };

        const string deviceId = "test-device";

        // Continuation within the prefetch window: playing index 0 of a 3-item queue
        // leaves 2 remaining, at the prefetch threshold boundary.
        var artistId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = artistId,
            StartIndex = 3,
            TotalCount = 13,
            UserId = Guid.NewGuid(),
            BatchSize = 5
        };
        QueueContinuationStore.Set(session.UserId, deviceId, continuation);

        // A VALID precompute entry (cached next == live queue successor), so the
        // handler serves track 2 straight from the cache. Track 2 is deliberately NOT
        // in the library: the full-resolution path cannot produce it, so a play
        // directive for track 2 with the stored URL proves the cache-hit branch ran.
        NextTrackPrecomputeCache.Store(
            deviceId,
            track1Id.ToString(),
            track2Id,
            new Audio { Id = track2Id, Name = "Track 2" },
            "https://stream/track2");
        _fx.LibraryManager.Setup(l => l.GetItemById(track2Id))
            .Returns((BaseItem?)null);

        var batch = Enumerable.Range(0, 5)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Song {i + 4}" })
            .ToList();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ArtistIds != null && q.ArtistIds.Contains(artistId))))
            .Returns(batch.Cast<BaseItem>().ToList());

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // The cache-hit branch served the precomputed next track...
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(track2Id.ToString(), directive.AudioItem.Stream.Token);
        Assert.Equal("https://stream/track2", directive.AudioItem.Stream.Url);

        // ...AND the continuation batch extended the queue BEFORE that early return.
        Assert.Equal(8, session.NowPlayingQueue.Count);

        // Cleanup
        NextTrackPrecomputeCache.Invalidate(deviceId);
        QueueContinuationStore.Remove(session.UserId, deviceId);
    }

    // JF-683: the PlaySong-fallback artist-queue continuation pin, driven through the
    // REAL fallback builder (CrossMediaFallback.BuildArtistSongsResponseAsync under the
    // "PlaySong fallback" label, the exact live path from the 2026-09-30 18:30 round)
    // and then a NearlyFinished at the prefetch boundary (track 3 of the 5-item page,
    // remaining == threshold) with a valid precompute entry. The live round's logs
    // PROVED this shape healthy (18:42:54: "Progressive queue: fetched 8 items for
    // Artist (offset 5/end-unknown)", queue position 5/13 at the next start); the
    // incident filing misread track 2's token as track 3 and blamed the guards. This
    // pin locks the healthy shape so a future regression in the Set gate, the key
    // (session.UserId + device), the threshold comparison, or the fetch-before-cache-hit
    // ordering fails here instead of on a device.
    [Fact]
    public async Task PlaybackNearlyFinished_PlaySongFallbackQueue_AtPrefetchBoundary_FetchesAndGrowsQueue()
    {
        _fx.Config.PreEnqueueOnStart = true;
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        // 13-track artist (the live Norah Jones shape): page 1 = tracks 1-5 (the
        // builder's Limit=InitialFetchSize query), page 2 = tracks 6-13 (the
        // continuation fetch: 8 items, fewer than BatchSize 10 -> the artist is
        // drained and the store entry removed).
        var allTracks = Enumerable.Range(0, 13)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Norah {i + 1}" })
            .ToList();
        List<BaseItem> initialPage = allTracks.Take(5).Cast<BaseItem>().ToList();
        List<BaseItem> nextBatch = allTracks.Skip(5).Cast<BaseItem>().ToList();

        _fx.LibraryManager.SetupSequence(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(initialPage)
            .Returns(nextBatch);

        var crossMedia = new CrossMediaFallback(
            _fx.Config,
            _fx.LoggerFactory.CreateLogger<CrossMediaFallback>(),
            TestHelpers.CreateLaunchBuilder(_fx.Config),
            requestTimeoutMs: 6000);

        await crossMedia.BuildArtistSongsResponseAsync(
            Guid.NewGuid(),
            "Norah Jones",
            TestHelpers.CreateJellyfinUser(),
            TestHelpers.CreateTestUser(),
            session,
            CreateContext(),
            "it-IT",
            _fx.LibraryManager.Object,
            _fx.UserDataManager.Object,
            queueManager: null,
            "PlaySong fallback",
            announcement: null,
            CancellationToken.None);

        // The builder left the live post-play state: a 5-item queue, the page's
        // first item now-playing, and a continuation for the remaining 8.
        Assert.Equal(5, session.NowPlayingQueue.Count);
        Assert.NotNull(QueueContinuationStore.Get(session.UserId, DeviceId));

        // Track 3 reaches its prefetch window (index 2 of 5, remaining 2 ==
        // threshold). Started(track 3) precomputed track 4 (hand-stored, the
        // PreEnqueueOnStartTests idiom); the session's now-playing item reflects the
        // landed start report (the healthy live state the 18:42:54 fetch ran under).
        Guid track3 = session.NowPlayingQueue[2].Id;
        Guid track4 = session.NowPlayingQueue[3].Id;
        session.FullNowPlayingItem = new Audio { Id = track3, Name = "Norah 3" };
        NextTrackPrecomputeCache.Store(
            DeviceId, track3.ToString(), track4, new Audio { Id = track4, Name = "Norah 4" }, "https://stream/track4");

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track3.ToString()),
            CreateContext(track3.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // The cache-hit branch served the precomputed successor...
        var directive = TestHelpers.GetPlayDirective(response);
        Assert.NotNull(directive);
        Assert.Equal(track4.ToString(), directive.AudioItem.Stream.Token);

        // ...AND the continuation batch grew the queue past the initial page (5+8=13)
        // with the whole artist drained (the 8-item short page marks exhaustion).
        Assert.Equal(13, session.NowPlayingQueue.Count);
        Assert.Null(QueueContinuationStore.Get(session.UserId, DeviceId));

        NextTrackPrecomputeCache.Invalidate(DeviceId);
    }

    // JF-683 observability: each of TryFetchContinuationBatch's three skip guards must
    // name itself and its values in a Debug line. The live 18:30 round was misdiagnosed
    // precisely because a legitimate threshold skip (track 2, remaining 3 > 2) was
    // indistinguishable in the logs from a guard bug. This pins the threshold-guard
    // line; the null-continuation and index guards share the same "ContinuationFetch:
    // skip" contract.
    [Fact]
    public async Task PlaybackNearlyFinished_ThresholdGuardSkip_LogsGuardNameAndValues()
    {
        var records = new List<(LogLevel Level, string Message)>();
        var handler = CreatePlaybackHandler(LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddProvider(TestCaptureLogger.Into(records));
        }));
        var session = CreateSession();
        _fx.SetupUserMock();

        var track1Id = Guid.NewGuid();
        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = Enumerable.Range(0, 5)
            .Select(i => new QueueItem { Id = i == 0 ? track1Id : Guid.NewGuid() })
            .ToList();

        QueueContinuationStore.Set(session.UserId, DeviceId, new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 13,
            UserId = Guid.NewGuid(),
            BatchSize = 5
        });

        _fx.LibraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns(new Audio { Id = Guid.NewGuid(), Name = "Track 2" });

        await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        string? skipLine = TestCaptureLogger.Snapshot(records)
            .FirstOrDefault(r => r.Message.Contains("ContinuationFetch: skip", StringComparison.Ordinal)).Message;
        Assert.NotNull(skipLine);
        Assert.Contains("skip, 4 items remain over threshold=2", skipLine, StringComparison.Ordinal);
        Assert.Contains("index=0 of 5", skipLine, StringComparison.Ordinal);

        QueueContinuationStore.Remove(session.UserId, DeviceId);
    }

    // JF-666 review finding 3: the fetch runs on the precompute cache-hit fast path,
    // so it must carry the shared request budget (RetryAsync): a transiently failing
    // batch query is retried inside the budget and the cached response is still
    // served; without the wrapper the first exception would fail the whole event.
    [Fact]
    public async Task PlaybackNearlyFinished_CacheHitTransientFetchFailure_RetriedWithinBudget()
    {
        _fx.Config.PreEnqueueOnStart = true;
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };

        const string deviceId = "test-device";

        var artistId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = artistId,
            StartIndex = 3,
            TotalCount = int.MaxValue,
            UserId = Guid.NewGuid(),
            BatchSize = 5
        };
        QueueContinuationStore.Set(session.UserId, deviceId, continuation);

        NextTrackPrecomputeCache.Store(
            deviceId,
            track1Id.ToString(),
            track2Id,
            new Audio { Id = track2Id, Name = "Track 2" },
            "https://stream/track2");
        _fx.LibraryManager.Setup(l => l.GetItemById(track2Id))
            .Returns((BaseItem?)null);

        var batch = Enumerable.Range(0, 5)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Song {i + 4}" })
            .ToList();

        // Two transient failures, then the batch: only the budgeted RetryAsync
        // wrapper gets past the first two attempts.
        int attempts = 0;
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ArtistIds != null && q.ArtistIds.Contains(artistId))))
            .Returns(() =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new TimeoutException($"simulated transient {attempts}");
                }

                return batch.Cast<BaseItem>().ToList();
            });

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // The cache-hit branch still served the precomputed next track...
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(track2Id.ToString(), directive.AudioItem.Stream.Token);

        // ...the query was retried to success (three attempts, inside the budget)...
        _fx.LibraryManager.Verify(
            l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.ArtistIds != null && q.ArtistIds.Contains(artistId))),
            Times.Exactly(3));

        // ...and the batch still extended the queue.
        Assert.Equal(8, session.NowPlayingQueue.Count);

        // Cleanup
        NextTrackPrecomputeCache.Invalidate(deviceId);
        QueueContinuationStore.Remove(session.UserId, deviceId);
    }

    [Fact]
    public async Task PlaybackNearlyFinished_NoContinuation_WorksWithoutExtension()
    {
        var handler = CreatePlaybackHandler();
        var session = CreateSession();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id }
        };

        _fx.LibraryManager.Setup(l => l.GetItemById(track2Id))
            .Returns(new Audio { Id = track2Id, Name = "Track 2" });

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(track2Id.ToString(), directive.AudioItem.Stream.Token);

        // Queue should remain unchanged
        Assert.Equal(2, session.NowPlayingQueue.Count);
    }

    [Fact]
    public async Task PlaybackNearlyFinished_QueueExhausted_CleansUpContinuation()
    {
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        var track1Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = track1Id } };

        const string deviceId = "test-device";

        // Set up continuation that's already exhausted
        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = Guid.NewGuid(),
            StartIndex = 10,
            TotalCount = 10, // Already at end
            UserId = Guid.NewGuid()
        };
        QueueContinuationStore.Set(session.UserId, deviceId, continuation);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // No next item - should return empty
        Assert.Empty(response.Response.Directives);

        // Continuation should be cleaned up
        Assert.Null(QueueContinuationStore.Get(session.UserId, deviceId));
    }

    [Fact]
    public async Task PlaybackNearlyFinished_FarFromEnd_DoesNotFetchContinuation()
    {
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        // Create a queue with many items remaining
        var tracks = Enumerable.Range(0, 10)
            .Select(_ => Guid.NewGuid())
            .ToList();

        session.FullNowPlayingItem = new Audio { Id = tracks[0], Name = "Track 1" };
        session.NowPlayingQueue = tracks.Select(id => new QueueItem { Id = id }).ToList();

        const string deviceId = "test-device";

        // Set up continuation (shouldn't be used since we're far from end)
        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = Guid.NewGuid(),
            StartIndex = 10,
            TotalCount = 20,
            UserId = Guid.NewGuid()
        };
        QueueContinuationStore.Set(session.UserId, deviceId, continuation);

        _fx.LibraryManager.Setup(l => l.GetItemById(tracks[1]))
            .Returns(new Audio { Id = tracks[1], Name = "Track 2" });

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(tracks[0].ToString()),
            CreateContext(tracks[0].ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Queue should NOT have been extended (we're far from end)
        Assert.Equal(10, session.NowPlayingQueue.Count);

        // GetItemsResult should NOT have been called
        _fx.LibraryManager.Verify(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);

        // Next track should be returned normally
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(tracks[1].ToString(), directive.AudioItem.Stream.Token);

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, deviceId);
    }

    // =====================================================================
    // PlayAlbumIntentHandler - Progressive fetching
    // =====================================================================

    [Fact]
    public async Task PlayAlbum_FetchesOnlyInitialPage()
    {
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

        var session = CreateSession();
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Id = albumId,
            Name = "Test Album"
        };

        // Create 20 tracks for the album
        var allTracks = Enumerable.Range(0, 20)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();

        // Mock: album search returns one result
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { album });

        // Mock: album track query returns first page (5 items) with total count 20
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new QueryResult<BaseItem>
            {
                Items = allTracks.Take(ProgressiveQueueConstants.GetInitialFetchSize()).Cast<BaseItem>().ToList(),
                TotalRecordCount = 20
            });

        var request = CreateAlbumIntent("Test Album");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Should have only initial items in queue
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);

        // Should have an AudioPlayer directive (playing first track)
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);

        // Continuation should be stored
        QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(continuation);
        Assert.Equal("Album", continuation.SourceType);
        Assert.Equal(albumId, continuation.ParentId);
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), continuation.StartIndex);
        Assert.Equal(20, continuation.TotalCount);

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
    }

    [Fact]
    public async Task PlayAlbum_SmallLibrary_NoContinuationStored()
    {
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

        var session = CreateSession();
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Id = albumId,
            Name = "Small Album"
        };

        // 3 tracks - all fit in initial fetch
        var tracks = Enumerable.Range(0, 3)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { album });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new QueryResult<BaseItem>
            {
                Items = tracks.Cast<MediaBrowser.Controller.Entities.BaseItem>().ToList(),
                TotalRecordCount = 3
            });

        var request = CreateAlbumIntent("Small Album");
        var context = CreateContext();

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // All tracks in queue (3 < InitialFetchSize)
        Assert.Equal(3, session.NowPlayingQueue.Count);

        // No continuation should be stored
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));
    }

    // =====================================================================
    // PlayArtistSongsIntentHandler - Progressive fetching
    // =====================================================================

    [Fact]
    public async Task PlayArtistSongs_FetchesOnlyInitialPage()
    {
        var handler = new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

        var session = CreateSession();
        _fx.SetupUserMock();

        var artistId = Guid.NewGuid();
        var artist = new MediaBrowser.Controller.Entities.Audio.MusicArtist
        {
            Id = artistId,
            Name = "Test Artist"
        };

        // 15 tracks for the artist
        var allTracks = Enumerable.Range(0, 15)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Song {i + 1}" })
            .ToList();

        // Mock: artist search
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { artist });

        // Mock: artist songs query with limit (uses GetItemList to avoid Jellyfin NRE)
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Contains(artistId))))
            .Returns(allTracks.Take(ProgressiveQueueConstants.GetInitialFetchSize()).Cast<MediaBrowser.Controller.Entities.BaseItem>().ToList());

        // Mock: no favorites
        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<MediaBrowser.Controller.Entities.BaseItem>()))
            .Returns((UserItemData?)null);

        var request = CreateArtistSongsIntent("Test Artist");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Should have only initial items in queue
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);

        // Continuation should be stored (TotalCount is int.MaxValue since GetItemList has no count)
        QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(continuation);
        Assert.Equal("Artist", continuation.SourceType);
        Assert.Equal(artistId, continuation.ArtistId);
        Assert.Equal(int.MaxValue, continuation.TotalCount);

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
    }

    // =====================================================================
    // PlayArtistSongsIntentHandler - Multi-word artist name fallback
    // =====================================================================

    [Fact]
    public async Task PlayArtistSongs_FullPrefixFallback_MatchesMultiWordArtist()
    {
        var handler = new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

        var session = CreateSession();
        _fx.SetupUserMock();

        var artistId = Guid.NewGuid();
        var artist = new MediaBrowser.Controller.Entities.Audio.MusicArtist
        {
            Id = artistId,
            Name = "Kidz Bop Kids"
        };

        var tracks = new List<Audio>
        {
            new() { Id = Guid.NewGuid(), Name = "Kidz Bop Song 1" }
        };

        // Mock: SearchTerm query returns empty (exact match fails)
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.SearchTerm == "Kidz Bop" && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());

        // Mock: first-word prefix query ("Kidz") returns empty
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.NameStartsWith == "Kidz" && q.SearchTerm == null)))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());

        // Mock: full-prefix query ("Kidz Bop") returns the artist
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.NameStartsWith == "Kidz Bop" && q.SearchTerm == null)))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { artist });

        // Mock: artist songs query (uses GetItemList to avoid Jellyfin NRE)
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.ArtistIds != null && q.ArtistIds.Contains(artistId))))
            .Returns(tracks.Cast<MediaBrowser.Controller.Entities.BaseItem>().ToList());

        // Mock: no favorites
        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<MediaBrowser.Controller.Entities.BaseItem>()))
            .Returns((UserItemData?)null);

        var request = CreateArtistSongsIntent("Kidz Bop");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Should have found the artist via full-prefix fallback
        Assert.Single(session.NowPlayingQueue);

        // Should have an AudioPlayer directive
        var directive = response.Response.Directives.OfType<AudioPlayerPlayDirective>().FirstOrDefault();
        Assert.NotNull(directive);
        Assert.Equal(PlayBehavior.ReplaceAll, directive.PlayBehavior);

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
    }

    // =====================================================================
    // ProgressiveQueueConstants tests
    // =====================================================================

    [Fact]
    public void ProgressiveQueueConstants_InitialFetchSize_IsSmall()
    {
        Assert.True(ProgressiveQueueConstants.GetInitialFetchSize() <= 10,
            "Initial fetch size should be small for fast time-to-audio");
    }

    [Fact]
    public void ProgressiveQueueConstants_ContinuationBatchSize_LargerThanInitial()
    {
        Assert.True(ProgressiveQueueConstants.GetContinuationBatchSize() >= ProgressiveQueueConstants.GetInitialFetchSize(),
            "Continuation batch should be at least as large as initial fetch");
    }

    [Fact]
    public void ProgressiveQueueConstants_PrefetchThreshold_IsReasonable()
    {
        Assert.True(ProgressiveQueueConstants.GetPrefetchThreshold() >= 1,
            "Prefetch threshold should be at least 1 to avoid last-minute fetches");
        Assert.True(ProgressiveQueueConstants.GetPrefetchThreshold() <= ProgressiveQueueConstants.GetInitialFetchSize(),
            "Prefetch threshold should not exceed initial fetch size");
    }

    // =====================================================================
    // Shuffle continuation tests
    // =====================================================================

    [Fact]
    public async Task PlaybackNearlyFinished_ShuffleContinuation_AppendsRandomizedOrder()
    {
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        // Initial queue: 3 items, playing track 1
        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };

        const string deviceId = "test-device";

        // Continuation with Shuffle=true
        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = Guid.NewGuid(),
            StartIndex = 3,
            TotalCount = int.MaxValue,
            UserId = Guid.NewGuid(),
            BatchSize = 20,
            Shuffle = true
        };
        QueueContinuationStore.Set(session.UserId, deviceId, continuation);

        // Library returns 20 tracks in a known order
        var continuationTracks = Enumerable.Range(0, 20)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Continuation {i}" })
            .ToList();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(continuationTracks.ToList<BaseItem>());

        _fx.LibraryManager.Setup(l => l.GetItemById(track2Id))
            .Returns(new Audio { Id = track2Id, Name = "Track 2" });

        await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Queue should have been extended
        Assert.Equal(23, session.NowPlayingQueue.Count); // 3 original + 20 new

        // Extract just the continuation items (after the original 3)
        var appendedIds = session.NowPlayingQueue.Skip(3).Select(q => q.Id).ToList();

        // All items present, no duplicates
        Assert.Equal(20, appendedIds.Distinct().Count());
        Assert.All(continuationTracks, t => Assert.Contains(t.Id, appendedIds));

        // Order should differ from DB order (probability of exact match = 1/20! ≈ 0)
        bool anyReordered = false;
        for (int i = 0; i < continuationTracks.Count; i++)
        {
            if (appendedIds[i] != continuationTracks[i].Id)
            {
                anyReordered = true;
                break;
            }
        }

        Assert.True(anyReordered, "Shuffle=true should reorder continuation batch");

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, deviceId);
    }

    [Fact]
    public async Task PlaybackNearlyFinished_ShuffleOffContinuation_PreservesDbOrder()
    {
        var handler = CreatePlaybackHandler();
        var session = CreateSession();
        _fx.SetupUserMock();

        var track1Id = Guid.NewGuid();
        var track2Id = Guid.NewGuid();
        var track3Id = Guid.NewGuid();

        session.FullNowPlayingItem = new Audio { Id = track1Id, Name = "Track 1" };
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = track1Id },
            new() { Id = track2Id },
            new() { Id = track3Id }
        };

        const string deviceId = "test-device";

        // Continuation with Shuffle=false (default)
        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = Guid.NewGuid(),
            StartIndex = 3,
            TotalCount = int.MaxValue,
            UserId = Guid.NewGuid(),
            BatchSize = 5,
            Shuffle = false
        };
        QueueContinuationStore.Set(session.UserId, deviceId, continuation);

        var contTracks = new List<Audio>
        {
            new() { Id = Guid.NewGuid(), Name = "A" },
            new() { Id = Guid.NewGuid(), Name = "B" },
            new() { Id = Guid.NewGuid(), Name = "C" },
            new() { Id = Guid.NewGuid(), Name = "D" },
            new() { Id = Guid.NewGuid(), Name = "E" }
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(contTracks.Cast<BaseItem>().ToList());

        _fx.LibraryManager.Setup(l => l.GetItemById(track2Id))
            .Returns(new Audio { Id = track2Id, Name = "Track 2" });

        await handler.HandleAsync(
            CreateNearlyFinishedRequest(track1Id.ToString()),
            CreateContext(track1Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var appendedIds = session.NowPlayingQueue.Skip(3).Select(q => q.Id).ToList();

        // Order should match DB order exactly
        for (int i = 0; i < contTracks.Count; i++)
        {
            Assert.Equal(contTracks[i].Id, appendedIds[i]);
        }

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, deviceId);
    }

    [Fact]
    public void QueueContinuationStore_ShuffleFlag_RoundTrips()
    {
        var userId = Guid.NewGuid();
        string deviceId = "shuffle-test";

        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = Guid.NewGuid(),
            UserId = userId,
            Shuffle = true
        };

        QueueContinuationStore.Set(userId, deviceId, continuation);
        QueueContinuation? retrieved = QueueContinuationStore.Get(userId, deviceId);

        Assert.NotNull(retrieved);
        Assert.True(retrieved.Shuffle);

        // Also verify default is false
        var noShuffle = new QueueContinuation { SourceType = "Artist", UserId = userId };
        Assert.False(noShuffle.Shuffle);

        QueueContinuationStore.Remove(userId, deviceId);
    }

    // =====================================================================
    // Playlist continuation caching (issue #10 efficiency follow-up)
    // =====================================================================

    [Fact]
    public void PlaylistContinuation_CachedTracks_SlicesAcrossBatches_WithoutReResolving()
    {
        // The handler caches the fully-resolved track list at first-play; the fetcher must
        // slice that cache per batch (NOT re-resolve via GetManageableItems), advancing
        // StartIndex, and return empty once StartIndex reaches TotalCount.
        _fx.SetupUserMock();

        List<BaseItem> tracks = Enumerable.Range(0, 10)
            .Select(i => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = $"Track {i}" })
            .ToList();
        List<Guid> ids = tracks.Select(t => t.Id).ToList();

        var continuation = new QueueContinuation
        {
            SourceType = "Playlist",
            PlaylistId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 0,
            TotalCount = tracks.Count,
            BatchSize = 4, // override the default for a clean 4/4/2 split
            CachedTracks = tracks
        };

        ILogger logger = _fx.LoggerFactory.CreateLogger("PlaylistContinuationTest");

        // Batch 1: first 4
        IReadOnlyList<BaseItem> batch1 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);
        Assert.Equal(ids.GetRange(0, 4), batch1.Select(b => b.Id).ToList());
        Assert.Equal(4, continuation.StartIndex);

        // Batch 2: next 4
        IReadOnlyList<BaseItem> batch2 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);
        Assert.Equal(ids.GetRange(4, 4), batch2.Select(b => b.Id).ToList());
        Assert.Equal(8, continuation.StartIndex);

        // Batch 3: remaining 2
        IReadOnlyList<BaseItem> batch3 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);
        Assert.Equal(ids.GetRange(8, 2), batch3.Select(b => b.Id).ToList());
        Assert.Equal(10, continuation.StartIndex);

        // Batch 4: exhausted -> empty (StartIndex >= TotalCount short-circuit)
        IReadOnlyList<BaseItem> batch4 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);
        Assert.Empty(batch4);

        // The cached path must NOT touch the library manager at all (no re-resolution).
        _fx.LibraryManager.Verify(
            lm => lm.GetItemById(It.IsAny<Guid>()),
            Times.Never,
            "cached playlist continuation must not re-resolve via the library manager");
    }

    // =====================================================================
    // Album disc/track ordering (JF-339 AC#3)
    // =====================================================================

    [Fact]
    public void QueueContinuation_AlbumFetch_AppliesDiscThenTrackOrder()
    {
        _fx.SetupUserMock();

        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 30, // exceeds one batch -> exercises the paginated path
            BatchSize = 10
        };

        InternalItemsQuery? captured = null;

        // Non-zero total so the fetcher uses the primary ParentId query (not the AlbumIds fallback).
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "t" } },
                TotalRecordCount = 30
            });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AlbumOrderTest");
        QueueContinuationFetcher.FetchNextBatch(continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        Assert.NotNull(captured);
        // Literal (not the constant) pins AlbumTrackOrder's value — a corrupted constant fails here.
        Assert.Equal(
            new[] { (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            captured!.OrderBy);
    }

    [Fact]
    public async Task PlayAlbum_TrackQuery_OrdersByDiscThenTrack()
    {
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

        var session = CreateSession();
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Id = albumId,
            Name = "Multi-Disc Album"
        };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        InternalItemsQuery? capturedTrackQuery = null;
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Callback<InternalItemsQuery>(q => capturedTrackQuery = q)
            .Returns(new QueryResult<BaseItem>
            {
                Items = Enumerable.Range(0, 5).Select(_ => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = "t" }).ToList(),
                TotalRecordCount = 25
            });

        var context = CreateContext();
        await handler.HandleAsync(CreateAlbumIntent("Multi-Disc Album"), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(capturedTrackQuery);
        Assert.Equal(
            QueueContinuationFetcher.AlbumTrackOrder,
            capturedTrackQuery!.OrderBy);

        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
    }

    [Fact]
    public void QueueContinuation_AlbumIdsFallback_AppliesDiscThenTrackOrder()
    {
        // AlbumIds fallback (primary returns 0) must also carry the disc/track OrderBy.
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = albumId,
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 30,
            BatchSize = 10
        };

        InternalItemsQuery? capturedFallback = null;

        // Primary ParentId query returns 0 -> triggers the AlbumIds fallback.
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.AlbumIds == null || q.AlbumIds.Length == 0)))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        // AlbumIds fallback returns tracks; capture its query.
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.AlbumIds != null && q.AlbumIds.Contains(albumId))))
            .Callback<InternalItemsQuery>(q => capturedFallback = q)
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "t" } },
                TotalRecordCount = 30
            });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AlbumIdsFallbackOrderTest");
        QueueContinuationFetcher.FetchNextBatch(continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        Assert.NotNull(capturedFallback);
        Assert.Equal(
            QueueContinuationFetcher.AlbumTrackOrder,
            capturedFallback!.OrderBy);
    }

    // JF-666: the artist continuation fetch must filter via IncludeItemTypes=Audio,
    // never MediaTypes=Audio (the JF-358 anti-pattern: MediaTypes does not constrain
    // an ArtistIds query; on the direct path it returned zero and silently exhausted
    // the continuation).
    [Fact]
    public void QueueContinuation_ArtistFetch_UsesIncludeItemTypesNotMediaTypes()
    {
        _fx.SetupUserMock();

        var artistId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = artistId,
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 13,
            BatchSize = 5
        };

        InternalItemsQuery? captured = null;
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "t" } });

        var records = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(records)));
        QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, loggerFactory.CreateLogger("QueueContinuation"));

        Assert.NotNull(captured);
        Assert.Contains(artistId, captured!.ArtistIds);
        Assert.NotNull(captured.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Audio, captured.IncludeItemTypes);
        // Jellyfin initializes MediaTypes to an empty array: the contract is "no
        // MediaTypes filter", not the field's exact null/empty shape.
        Assert.True(
            captured.MediaTypes == null || captured.MediaTypes.Length == 0,
            "artist query must not filter via MediaTypes (JF-358/JF-666)");

        // The success log names the offset the query RAN AT (a short page like this
        // 1-of-5 batch advances StartIndex to TotalCount before logging; the log must
        // not report that advanced value).
        string info = Assert.Single(
            TestCaptureLogger.Snapshot(records).Where(r => r.Level == LogLevel.Information)).Message;
        Assert.Contains("fetched 1 items for Artist (offset 5/13)", info);
    }

    // JF-666 review finding 1: continuation batches must run under the same per-user
    // library scope as the initial fetches; a restricted user's artist batch query
    // carries the allowed libraries as TopParentIds.
    [Fact]
    public void QueueContinuation_ArtistFetch_AppliesPluginUserLibraryScope()
    {
        _fx.SetupUserMock();

        var musicLibId = Guid.NewGuid();
        var pluginUser = new Entities.User
        {
            Id = Guid.NewGuid(),
            AllowedLibraryIds = new List<string> { musicLibId.ToString() }
        };

        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 13,
            BatchSize = 5
        };

        InternalItemsQuery? captured = null;
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "t" } });

        ILogger logger = _fx.LoggerFactory.CreateLogger("ArtistScopeTest");
        QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger, pluginUser);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.TopParentIds);
        Assert.Contains(musicLibId, captured.TopParentIds);
    }

    // JF-666: a zero-page fetch while StartIndex < TotalCount must log at WARN naming
    // the source type, source id, and offset; the silent exhaust forced the live
    // diagnosis to infer it from the store Remove alone. TotalCount=int.MaxValue is
    // the REAL artist-continuation shape (both creators set it; GetItemList has no
    // count), rendered as end-unknown instead of a meaningless 2147483647.
    [Fact]
    public void QueueContinuation_ZeroItemFetch_LogsWarningWithQueryOperands()
    {
        _fx.SetupUserMock();

        var artistId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Artist",
            ArtistId = artistId,
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = int.MaxValue,
            BatchSize = 5
        };

        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var records = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(records)));
        QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, loggerFactory.CreateLogger("QueueContinuation"));

        var warnings = TestCaptureLogger.Snapshot(records)
            .Where(r => r.Level == LogLevel.Warning)
            .ToList();
        string warning = Assert.Single(warnings).Message;
        Assert.Contains("fetched 0 items for Artist", warning);
        Assert.Contains($"artist {artistId}", warning);
        Assert.Contains("offset 5/end-unknown", warning);
    }

    // JF-670: the Audiobook continuation is no longer a dead letter. The fetch arm
    // mirrors PlayBookIntentHandler's initial page query: ParentId scoped on the book
    // folder, MediaTypes=Audio (the JF-358 IncludeItemTypes discipline governs
    // ArtistIds queries, which MediaTypes silently ignores; the initial page returns
    // its chapters through MediaTypes + ParentId, and the pages must share one query
    // shape so they concatenate in one order), and NO explicit order (the initial page
    // sets none, so its DB order is the book's chapter order).
    [Fact]
    public void QueueContinuation_AudiobookFetch_QueriesBookParentBeyondInitialPage()
    {
        _fx.SetupUserMock();

        var bookId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Audiobook",
            ParentId = bookId,
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 20,
            BatchSize = 10
        };

        InternalItemsQuery? captured = null;
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem>
                {
                    new Audio { Id = Guid.NewGuid(), Name = "Chapter 6" },
                    new Audio { Id = Guid.NewGuid(), Name = "Chapter 7" }
                },
                TotalRecordCount = 20
            });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AudiobookContinuationTest");
        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        Assert.NotNull(captured);
        Assert.Equal(bookId, captured!.ParentId);
        Assert.True(captured.Recursive);
        Assert.NotNull(captured.MediaTypes);
        Assert.Contains(MediaType.Audio, captured.MediaTypes);
        // The mirror contract: the initial book page carries NO kind filter and NO
        // explicit order. Jellyfin initializes both fields to empty arrays, so the
        // contract is "no filter", not the field's exact null/empty shape.
        Assert.True(
            captured.IncludeItemTypes == null || captured.IncludeItemTypes.Length == 0,
            "audiobook query must mirror the initial page: no IncludeItemTypes filter");
        Assert.True(
            captured.OrderBy == null || captured.OrderBy.Count == 0,
            "audiobook query must mirror the initial page: no explicit order");
        Assert.Equal(5, captured.StartIndex);
        Assert.Equal(10, captured.Limit);

        // The batch feeds the queue and the offset advances past it.
        Assert.Equal(2, batch.Count);
        Assert.Equal(7, continuation.StartIndex);
    }

    // JF-666 parity extended to the Audiobook arm (JF-670): a restricted user's book
    // batch query carries the allowed libraries as TopParentIds, same as the artist
    // and album fetchers.
    [Fact]
    public void QueueContinuation_AudiobookFetch_AppliesPluginUserLibraryScope()
    {
        _fx.SetupUserMock();

        var bookLibId = Guid.NewGuid();
        var pluginUser = new Entities.User
        {
            Id = Guid.NewGuid(),
            AllowedLibraryIds = new List<string> { bookLibId.ToString() }
        };

        var continuation = new QueueContinuation
        {
            SourceType = "Audiobook",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 20,
            BatchSize = 10
        };

        InternalItemsQuery? captured = null;
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "Chapter 6" } },
                TotalRecordCount = 20
            });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AudiobookScopeTest");
        QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger, pluginUser);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.TopParentIds);
        Assert.Contains(bookLibId, captured.TopParentIds);
    }

    // JF-670: the Audiobook arm runs a real query, so its zero page belongs in the
    // WARN-gated set. Before the arm existed the structural zero was silent (and
    // truncated books at the initial page); a real empty page while StartIndex <
    // TotalCount must stay loud, naming the book.
    [Fact]
    public void QueueContinuation_AudiobookFetch_ZeroPage_LogsWarningNamingTheBook()
    {
        _fx.SetupUserMock();

        var bookId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Audiobook",
            ParentId = bookId,
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 20,
            BatchSize = 10
        };

        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        var records = new List<(LogLevel Level, string Message)>();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(TestCaptureLogger.Into(records)));
        QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, loggerFactory.CreateLogger("QueueContinuation"));

        var warnings = TestCaptureLogger.Snapshot(records)
            .Where(r => r.Level == LogLevel.Warning)
            .ToList();
        string warning = Assert.Single(warnings).Message;
        Assert.Contains("fetched 0 items for Audiobook", warning);
        Assert.Contains($"book {bookId}", warning);
        Assert.Contains("offset 5/20", warning);
    }

    // JF-670 executor parity: the initial page runs the same query through
    // SearchService.SafeGetItemsResult (NRE -> GetItemList fallback for Jellyfin's
    // Count()-translation NRE class); the tail arm must survive the same class, or
    // the head lives while the tail dies mid-book.
    [Fact]
    public void QueueContinuation_AudiobookFetch_NreFallback_UsesGetItemList()
    {
        _fx.SetupUserMock();

        var chapterId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Audiobook",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = 20,
            BatchSize = 10
        };

        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        InternalItemsQuery? captured = null;
        _fx.LibraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem> { new Audio { Id = chapterId, Name = "Chapter 6" } });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AudiobookNreFallbackTest");
        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        Assert.NotNull(captured);
        Assert.Single(batch);
        Assert.Equal(chapterId, batch[0].Id);
        Assert.Equal(6, continuation.StartIndex);
    }

    [Fact]
    public async Task PlayAlbum_MultiDiscAlbum_QueueFollowsDiscThenTrackOrder()
    {
        // Verifies the handler preserves the DB's disc/track order into the queue (no reshuffle).
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

        var session = CreateSession();
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Id = albumId,
            Name = "Double Album"
        };

        // 2 discs x 2 tracks, in disc/track order (disc 1 first). Four tracks fit within
        // the initial fetch (default 5), so the whole album lands in the first queue page.
        var d1t1 = new Audio { Id = Guid.NewGuid(), Name = "D1 T1", ParentIndexNumber = 1, IndexNumber = 1 };
        var d1t2 = new Audio { Id = Guid.NewGuid(), Name = "D1 T2", ParentIndexNumber = 1, IndexNumber = 2 };
        var d2t1 = new Audio { Id = Guid.NewGuid(), Name = "D2 T1", ParentIndexNumber = 2, IndexNumber = 1 };
        var d2t2 = new Audio { Id = Guid.NewGuid(), Name = "D2 T2", ParentIndexNumber = 2, IndexNumber = 2 };
        var ordered = new List<BaseItem> { d1t1, d1t2, d2t1, d2t2 };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new QueryResult<BaseItem> { Items = ordered, TotalRecordCount = ordered.Count });

        var context = CreateContext();
        await handler.HandleAsync(CreateAlbumIntent("Double Album"), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // The playback queue must be disc 1 (t1, t2) then disc 2 (t1, t2) — not reshuffled.
        var expectedIds = new[] { d1t1.Id, d1t2.Id, d2t1.Id, d2t2.Id };
        Assert.Equal(expectedIds, session.NowPlayingQueue.Select(q => q.Id).ToArray());

        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
    }
}
