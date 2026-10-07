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
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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

    // JF-753 RED PROOF: on NRE-class servers the album's first track page arrives
    // through the SafeGetItemsResult fallback (GetItemList), which cannot know the
    // library total. The pre-fix fallback wrapped the PAGE SIZE as
    // TotalRecordCount, so a FULL initial page read as "complete" at the store gate
    // (TotalRecordCount > Items.Count), the album continuation never engaged, and
    // the album truncated at the initial page exactly as books did before JF-673.
    // The honest end-unknown total must make the head engage the store in the
    // end-unknown regime so the tail keeps fetching (short page = end).
    [Fact]
    public async Task PlayAlbum_NreFallbackFullInitialPage_StoresEndUnknownContinuation()
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
        var album = new MusicAlbum { Id = albumId, Name = "NRE Album" };

        // Mock: album search returns one result (the search runs on GetItemList;
        // only the track query below uses GetItemsResult).
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        // The NRE-class server: the track query's GetItemsResult throws, and the
        // fallback serves a FULL initial page (5 = GetInitialFetchSize).
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        List<BaseItem> fullPage = Enumerable.Range(0, ProgressiveQueueConstants.GetInitialFetchSize())
            .Select(i => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(fullPage);

        var request = CreateAlbumIntent("NRE Album");
        var context = CreateContext();

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(continuation);
        Assert.Equal("Album", continuation!.SourceType);
        Assert.Equal(albumId, continuation.ParentId);
        Assert.Equal(SearchService.UnknownTotal, continuation.TotalCount);
        Assert.Equal(fullPage.Count, continuation.StartIndex);

        // Cleanup
        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
    }

    // JF-753 guardrail: under the end-unknown regime a SHORT initial page IS the
    // album's end signal; the head must not store a doomed continuation whose
    // first batch would come back empty and WARN.
    [Fact]
    public async Task PlayAlbum_NreFallbackShortInitialPage_StoresNoContinuation()
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
        var album = new MusicAlbum { Id = albumId, Name = "Short NRE Album" };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        // A 2-track album: 2 < 5 = the short page that ends the album.
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new List<BaseItem>
            {
                new Audio { Id = Guid.NewGuid(), Name = "Track 1" },
                new Audio { Id = Guid.NewGuid(), Name = "Track 2" }
            });

        var request = CreateAlbumIntent("Short NRE Album");
        var context = CreateContext();

        await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Equal(2, session.NowPlayingQueue.Count);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));
    }

    // JF-753 guardrail: an album with NO tracks on an NRE-class server (both the
    // ParentId page and the JF-338 AlbumIds retry come back empty through the
    // fallback) must still reach the NoSongsInAlbum tell: the zero check carries
    // its own end-unknown arm because the opted-in fallback reports UnknownTotal
    // (never 0) for an empty page.
    [Fact]
    public async Task PlayAlbum_NreFallbackZeroTracks_BothQueriesEmpty_SpeaksNoSongs()
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
        var album = new MusicAlbum { Id = albumId, Name = "Empty NRE Album" };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new List<BaseItem>());
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.AlbumIds != null && q.AlbumIds.Contains(albumId))))
            .Returns(new List<BaseItem>());

        var request = CreateAlbumIntent("Empty NRE Album");
        var context = CreateContext();

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // The NoSongsInAlbum tell: speech naming the album, no play directive,
        // and no continuation for an album with no playable tracks.
        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.Contains("Empty NRE Album", speech, StringComparison.Ordinal);
        Assert.True(
            response.Response.Directives == null || response.Response.Directives.Count == 0,
            "a no-songs album must not launch playback");
        Assert.Empty(session.NowPlayingQueue);
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

    // JF-757 lockstep pin: the album-tracks query shape is ONE builder
    // (QueueContinuationFetcher.BuildAlbumTracksQuery) shared by the head
    // (BuildAlbumPlayResponseAsync first page: the ParentId arm plus the JF-338
    // AlbumIds retry) and the tail (FetchAlbumTracks, the same two arms). This pin
    // drives BOTH ends against a split-album server (empty ParentId pages,
    // populated AlbumIds pages) and captures all seven issued queries in call
    // order (head primary, head retry, the JF-797 played and resumable resume
    // probes, the JF-796 deep-resume fetch the probe hit releases, tail primary,
    // tail retry), then asserts each arm's
    // head and tail shapes are IDENTICAL except
    // the paging fields. JF-763 joined the library-scope dimension to the
    // lockstep: the plugin user is RESTRICTED and both ends' queries must carry
    // the resolved allowed library as TopParentIds (head/tail parity, the JF-666
    // tail rule extended to the head's track pages). Division of labor with the
    // literal pins above: they keep the ABSOLUTE OrderBy honest (a corrupted
    // AlbumTrackOrder constant reds there); the absolute kind filter, DTO fields,
    // paging, and TopParentIds asserts live INSIDE this test's AssertArmLockstep
    // (nowhere else), so do not trim them as redundant. This pin keeps the two
    // ENDS from drifting apart (a hand-kept initializer reintroduced at either
    // end reds here even while every literal pin still passes, which is the drift
    // class the JF-757 consolidation exists to close).
    [Fact]
    public async Task AlbumTracks_HeadAndTail_ShareOneQueryShapeModuloPaging_BothArms()
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

        // JF-763: a RESTRICTED plugin user threads both ends, so the scope
        // dimension is exercised (not just exempted as before).
        Guid musicLibId = Guid.NewGuid();
        var pluginUser = TestHelpers.CreateTestUser(allowedLibraryIds: new[] { musicLibId.ToString() });

        var albumId = Guid.NewGuid();
        var album = new MusicAlbum { Id = albumId, Name = "Lockstep Album" };

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        // JF-797: the membership arm serves a real 25-track album through the
        // flag-honoring page server (paging plus the IsPlayed/IsResumable probes
        // the discriminator issues), with one in-progress track at index 9 beyond
        // the initial page, so the ask pays the probe pair AND the deep fetch the
        // probe hit triggers (both members of the ONE shape stay pinned).
        List<BaseItem> albumTracks = Enumerable.Range(0, 25)
            .Select(i => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();
        BaseItem progressRow = albumTracks[9];
        _fx.UserDataManager
            .Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns((Jellyfin.Database.Implementations.Entities.User _, BaseItem item) =>
                item.Id == progressRow.Id
                    ? new UserItemData { Key = "k", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(1).Ticks }
                    : null);

        var captured = new List<InternalItemsQuery>();

        // Split-album server shape (JF-338): the folder-based ParentId page is
        // empty at both ends, the AlbumIds membership page carries the tracks, so
        // the head issues primary, retry, the two resume probes, and the deep
        // fetch, then the tail issues primary and retry (that call order).
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.ParentId == albumId && (q.AlbumIds == null || q.AlbumIds.Length == 0))))
            .Callback<InternalItemsQuery>(q => captured.Add(q))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q =>
                q.AlbumIds != null && q.AlbumIds.Contains(albumId))))
            .Callback<InternalItemsQuery>(q => captured.Add(q))
            .Returns((InternalItemsQuery q) => TestHelpers.ServePagedTracks(
                albumTracks,
                q,
                playedRow: null,
                resumableRow: t => t.Id == progressRow.Id));

        var context = CreateContext();
        await handler.HandleAsync(
            CreateAlbumIntent("Lockstep Album"), context, pluginUser, session, CancellationToken.None);

        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = albumId,
            UserId = session.UserId,
            StartIndex = 5,
            TotalCount = 30,
            BatchSize = 10
        };
        ILogger logger = _fx.LoggerFactory.CreateLogger("AlbumLockstepTest");
        QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger, pluginUser);

        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);

        Assert.Equal(7, captured.Count);
        InternalItemsQuery headPrimary = captured[0];
        InternalItemsQuery headRetry = captured[1];
        InternalItemsQuery headPlayedProbe = captured[2];
        InternalItemsQuery headResumableProbe = captured[3];
        InternalItemsQuery headDeep = captured[4];
        InternalItemsQuery tailPrimary = captured[5];
        InternalItemsQuery tailRetry = captured[6];

        // The field-set half of the lockstep (everything except paging), shared by
        // the head/tail arm pairs and the JF-796 deep fetch so a field added to the
        // ONE shape reaches every consumer in a single edit.
        void AssertSharedQueryShape(InternalItemsQuery a, InternalItemsQuery b)
        {
            // The shared shape: same user, same recursion, same kind filter, same
            // disc/track order, same full-field DTO options.
            Assert.Same(a.User, b.User);
            Assert.True(a.Recursive);
            Assert.Equal(a.Recursive, b.Recursive);
            Assert.Equal(new[] { BaseItemKind.Audio }, a.IncludeItemTypes);
            Assert.Equal(a.IncludeItemTypes, b.IncludeItemTypes);
            Assert.Equal(QueueContinuationFetcher.AlbumTrackOrder, a.OrderBy);
            Assert.Equal(a.OrderBy, b.OrderBy);
            Assert.NotNull(a.DtoOptions);
            Assert.NotNull(b.DtoOptions);
            // new DtoOptions(true) populates Fields with every ItemFields value; an
            // end drifting to a minimal DtoOptions (e.g. AlbumPlay's CheapDtoOptions
            // shape) reds here.
            Assert.Equal(a.DtoOptions!.Fields, b.DtoOptions!.Fields);

            // JF-763: the library-scope dimension joined the lockstep. Both sides
            // carry the plugin user's resolved allowed library (the absolute id,
            // so a consistently-wrong scope reds too, not only head-vs-tail
            // drift); removing either end's ApplyLibraryFilter reds here (a
            // restricted user's head page and tail batches must enumerate ONE
            // row set or the pages switch sets mid-album).
            Assert.Contains(musicLibId, a.TopParentIds);
            Assert.Equal(a.TopParentIds, b.TopParentIds);
        }

        void AssertArmLockstep(InternalItemsQuery head, InternalItemsQuery tail)
        {
            AssertSharedQueryShape(head, tail);

            // Paging is the ONE intended head/tail difference: the head pages with
            // 0 + the initial fetch size, the tail with its continuation offset
            // and batch size.
            Assert.Equal(0, head.StartIndex);
            Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), head.Limit);
            Assert.Equal(5, tail.StartIndex);
            Assert.Equal(10, tail.Limit);
        }

        // Folder arm (ParentId): scoped by the album id, membership field untouched.
        AssertArmLockstep(headPrimary, tailPrimary);
        Assert.Equal(albumId, headPrimary.ParentId);
        Assert.Equal(albumId, tailPrimary.ParentId);
        Assert.Empty(headPrimary.AlbumIds ?? Array.Empty<Guid>());
        Assert.Empty(tailPrimary.AlbumIds ?? Array.Empty<Guid>());

        // Membership arm (AlbumIds): scoped by album membership, folder field
        // untouched (a set ParentId here would AND a second constraint into the
        // server query and defeat the JF-338 recovery).
        AssertArmLockstep(headRetry, tailRetry);
        Assert.Equal(new[] { albumId }, headRetry.AlbumIds);
        Assert.Equal(new[] { albumId }, tailRetry.AlbumIds);
        Assert.Equal(Guid.Empty, headRetry.ParentId);
        Assert.Equal(Guid.Empty, tailRetry.ParentId);

        // JF-796: the deep-resume fetch is the UNPAGED member of the working arm
        // (the fetch the JF-797 discriminator releases): the ONE shared field set
        // beside the paged head retry, paging null (fetch-all, not Take(0)), so a
        // hand-kept initializer reintroduced for the deep fetch reds here too.
        AssertSharedQueryShape(headRetry, headDeep);
        Assert.Equal(new[] { albumId }, headDeep.AlbumIds);
        Assert.Equal(Guid.Empty, headDeep.ParentId);
        Assert.Null(headDeep.StartIndex);
        Assert.Null(headDeep.Limit);

        // JF-797 item 2: the resume probes are the discriminator's bounded members
        // of the SAME working arm and shape (user, recursion, kind filter, order,
        // scope), paging 0 + 1, each carrying its ONE flag axis, in the played-first
        // call order. The DELIBERATE divergence from the shared field set is the
        // cheap DtoOptions: a one-row existence probe carries no fields, the
        // full-field shape would rebuild the row cost the probe exists to avoid.
        foreach (InternalItemsQuery probe in new[] { headPlayedProbe, headResumableProbe })
        {
            Assert.Same(headRetry.User, probe.User);
            Assert.Equal(headRetry.Recursive, probe.Recursive);
            Assert.Equal(headRetry.IncludeItemTypes, probe.IncludeItemTypes);
            Assert.Equal(headRetry.OrderBy, probe.OrderBy);
            Assert.Equal(headRetry.TopParentIds, probe.TopParentIds);
            Assert.Equal(new[] { albumId }, probe.AlbumIds);
            Assert.Equal(Guid.Empty, probe.ParentId);
            Assert.Equal(0, probe.StartIndex);
            Assert.Equal(1, probe.Limit);
            Assert.True(
                probe.DtoOptions!.Fields == null || probe.DtoOptions.Fields.Count == 0,
                "the probe must use the cheap no-fields DtoOptions shape");
        }

        Assert.True(headPlayedProbe.IsPlayed);
        Assert.Null(headPlayedProbe.IsResumable);
        Assert.True(headResumableProbe.IsResumable);
        Assert.Null(headResumableProbe.IsPlayed);
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
    // shape so they concatenate in one order), and the JF-672 explicit chapter order
    // (the tail pin of the shared AudiobookChapterOrder: the old no-order shape rode
    // each server branch's empty-OrderBy default, SortName with no tiebreaker, read
    // at v10.11.8/v12.2 source; the pinned probe evidence lives in the JF-672 task).
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
        // The mirror contract: the initial book page carries NO kind filter (Jellyfin
        // initializes the field to an empty array, so the contract is "no filter",
        // not the field's exact null/empty shape) and the JF-672 explicit chapter
        // order: constant equality, the album-arm pin form (the ONE literal honesty
        // pin for the constant's value is the builder Fact below; consumer drift
        // away from the shared order reds HERE).
        Assert.True(
            captured.IncludeItemTypes == null || captured.IncludeItemTypes.Length == 0,
            "audiobook query must mirror the initial page: no IncludeItemTypes filter");
        Assert.Equal(QueueContinuationFetcher.AudiobookChapterOrder, captured.OrderBy);
        Assert.Equal(5, captured.StartIndex);
        Assert.Equal(10, captured.Limit);

        // The batch feeds the queue and the offset advances past it.
        Assert.Equal(2, batch.Count);
        Assert.Equal(7, continuation.StartIndex);
    }

    // JF-672: the ONE literal honesty pin for AudiobookChapterOrder's value (the
    // structural twin of the AlbumTrackOrder literal pin above): a corrupted
    // constant fails HERE, while the consumer pins (head, this file's tail, the
    // unpaged endpoint) assert constant equality and red on drift away from the
    // shared order. Both builder forms carry it so the paged queue and the unpaged
    // endpoint flip together; the probe-backed choice and the refuted alternatives
    // (the album-style composite, DateCreated) live on the constant's doc and in
    // the JF-672 task record. RED on the pre-JF-672 tree (OrderBy empty on both
    // forms).
    [Fact]
    public void QueueContinuation_AudiobookChapterOrder_PagedAndUnpagedFormsCarryTheOneExplicitOrder()
    {
        Guid bookId = Guid.NewGuid();
        InternalItemsQuery paged = QueueContinuationFetcher.BuildAudiobookChaptersQuery(null, bookId, 0, 10);
        InternalItemsQuery unpaged = QueueContinuationFetcher.BuildAudiobookChaptersQueryUnpaged(null, bookId);

        // Literal (not the constant) pins the value; NOT the album-style
        // (ParentIndexNumber, IndexNumber) pair.
        var expected = new[] { (ItemSortBy.SortName, SortOrder.Ascending) };
        Assert.Equal(expected, paged.OrderBy);
        Assert.Equal(expected, unpaged.OrderBy);
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

    // JF-673 RED PROOF (tail side): when the head's initial page came through the
    // NRE fallback it stores TotalCount=SearchService.UnknownTotal (GetItemList has
    // no count), so the tail has NO total to exhaust against; the only end signal is
    // a SHORT page, the FetchArtistSongs shape. The short page must mark the
    // continuation exhausted (StartIndex=TotalCount) so the NEXT FetchNextBatch is
    // terminal via the entry guard instead of querying past the end and WARNing.
    [Fact]
    public void QueueContinuation_AudiobookFetch_EndUnknownShortPage_MarksContinuationExhausted()
    {
        _fx.SetupUserMock();

        var continuation = new QueueContinuation
        {
            SourceType = "Audiobook",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = SearchService.UnknownTotal,
            BatchSize = 10
        };

        // The tail's own fetch may even succeed with a per-page total; the regime is
        // carried by the continuation (what the head stored), and result totals are
        // not read here either way.
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem>
                {
                    new Audio { Id = Guid.NewGuid(), Name = "Chapter 6" },
                    new Audio { Id = Guid.NewGuid(), Name = "Chapter 7" },
                    new Audio { Id = Guid.NewGuid(), Name = "Chapter 8" }
                },
                TotalRecordCount = 3
            });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AudiobookEndUnknownTest");
        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        // The batch is served whole; the short page marks the end.
        Assert.Equal(3, batch.Count);
        Assert.Equal(SearchService.UnknownTotal, continuation.StartIndex);

        // The marked state is terminal: the next call returns empty via the entry
        // guard WITHOUT a further library query.
        IReadOnlyList<BaseItem> next = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);
        Assert.Empty(next);
        _fx.LibraryManager.Verify(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    // JF-673 guardrail: a FULL page under the end-unknown regime advances the offset
    // without exhausting; the book continues page by page until a short page ends it.
    [Fact]
    public void QueueContinuation_AudiobookFetch_EndUnknownFullPage_AdvancesWithoutExhausting()
    {
        _fx.SetupUserMock();

        var continuation = new QueueContinuation
        {
            SourceType = "Audiobook",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = SearchService.UnknownTotal,
            BatchSize = 10
        };

        List<BaseItem> fullBatch = Enumerable.Range(0, 10)
            .Select(i => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = $"Chapter {i + 6}" })
            .ToList();
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = fullBatch, TotalRecordCount = 10 });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AudiobookEndUnknownFullPageTest");
        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        Assert.Equal(10, batch.Count);
        Assert.Equal(15, continuation.StartIndex);
        Assert.Equal(SearchService.UnknownTotal, continuation.TotalCount);
    }

    // JF-753 RED PROOF (tail side): the album continuation the end-unknown head
    // stores carries TotalCount=SearchService.UnknownTotal (the NRE fallback has no
    // total), so the tail has NO total to exhaust against; the only end signal is a
    // SHORT page, the same FetchArtistSongs shape as the audiobook tail. Pre-fix the
    // album tail advanced blindly (StartIndex += count), so the next batch queried
    // past the end and WARNed instead of ending at the entry guard.
    [Fact]
    public void QueueContinuation_AlbumFetch_EndUnknownShortPage_MarksContinuationExhausted()
    {
        _fx.SetupUserMock();

        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = SearchService.UnknownTotal,
            BatchSize = 10
        };

        // The tail's own fetch may even succeed with a per-page total; the regime is
        // carried by the continuation (what the head stored), and result totals are
        // not read here either way (the JF-673 audiobook twin above).
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem>
                {
                    new Audio { Id = Guid.NewGuid(), Name = "Track 6" },
                    new Audio { Id = Guid.NewGuid(), Name = "Track 7" },
                    new Audio { Id = Guid.NewGuid(), Name = "Track 8" }
                },
                TotalRecordCount = 3
            });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AlbumEndUnknownTest");
        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        // The batch is served whole; the short page marks the end.
        Assert.Equal(3, batch.Count);
        Assert.Equal(SearchService.UnknownTotal, continuation.StartIndex);

        // The marked state is terminal: the next call returns empty via the entry
        // guard WITHOUT a further library query.
        IReadOnlyList<BaseItem> next = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);
        Assert.Empty(next);
        _fx.LibraryManager.Verify(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Once);
    }

    // JF-753 guardrail: a FULL page under the end-unknown regime advances the offset
    // without exhausting; the album continues page by page until a short page ends it.
    [Fact]
    public void QueueContinuation_AlbumFetch_EndUnknownFullPage_AdvancesWithoutExhausting()
    {
        _fx.SetupUserMock();

        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = SearchService.UnknownTotal,
            BatchSize = 10
        };

        List<BaseItem> fullBatch = Enumerable.Range(0, 10)
            .Select(i => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 6}" })
            .ToList();
        _fx.LibraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem> { Items = fullBatch, TotalRecordCount = 10 });

        ILogger logger = _fx.LoggerFactory.CreateLogger("AlbumEndUnknownFullPageTest");
        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        Assert.Equal(10, batch.Count);
        Assert.Equal(15, continuation.StartIndex);
        Assert.Equal(SearchService.UnknownTotal, continuation.TotalCount);
    }

    // JF-753: the tail must share the head's NRE-guarded executor (the JF-670
    // head/tail contract): once the end-unknown head engages the store on an
    // NRE-class server, the tail's first batch runs the same query shape the head
    // fell back on, so the raw GetItemsResult would throw there and kill every
    // continuation batch. The shared SafeGetItemsResult keeps the tail alive, and
    // the split-album AlbumIds retry fires on the fallback's empty ParentId page
    // (the end-unknown zero-ITEMS arm) to serve a malformed/split album's tracks.
    [Fact]
    public void QueueContinuation_AlbumFetch_NreServer_SplitAlbumServedThroughFallbackRetry()
    {
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var continuation = new QueueContinuation
        {
            SourceType = "Album",
            ParentId = albumId,
            UserId = Guid.NewGuid(),
            StartIndex = 5,
            TotalCount = SearchService.UnknownTotal,
            BatchSize = 10
        };

        // The NRE-class server: every GetItemsResult throws; the fallback serves the
        // pages. Split album: no ParentId rows, the AlbumIds membership has them.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new List<BaseItem>());
        List<BaseItem> albumIdsBatch = Enumerable.Range(0, 10)
            .Select(i => (BaseItem)new Audio { Id = Guid.NewGuid(), Name = $"Split Track {i + 6}" })
            .ToList();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.AlbumIds != null && q.AlbumIds.Contains(albumId))))
            .Returns(albumIdsBatch);

        ILogger logger = _fx.LoggerFactory.CreateLogger("AlbumNreSplitTest");
        IReadOnlyList<BaseItem> batch = QueueContinuationFetcher.FetchNextBatch(
            continuation, _fx.LibraryManager.Object, _fx.UserManager.Object, logger);

        // The retry served the split album's page; the FULL page advances the offset
        // without exhausting the end-unknown continuation.
        Assert.Equal(albumIdsBatch, batch);
        Assert.Equal(15, continuation.StartIndex);
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

    // =====================================================================
    // JF-674: stale-continuation queue-identity validation
    // =====================================================================

    /// <summary>
    /// JF-674: THE filed injection scenario, driven end-to-end through the REAL
    /// mint (PlayBookIntentHandler on the flag-off AudioPlayer arm, where a book
    /// continuation is live) and the REAL fetch (PlaybackNearlyFinished): play a
    /// long book, stop mid-book (the store entry lingers by the documented
    /// semantics; PlaybackStopped never removes it), then play ONE song (the
    /// PlaySongIntentHandler inline shape: a one-item session queue, no
    /// SetQueue, no store clear). At the song's exhaustion the fetch must serve
    /// NOTHING: the stale book continuation is bound to the queue it was minted
    /// for, the song's queue is not that queue, and the entry is discarded
    /// instead of appending mid-book chapters after the song.
    /// Theory legs: the FINITE total (20 chapters) and the END-UNKNOWN total
    /// (the JF-673 SearchService.UnknownTotal sentinel), which JF-673 makes a
    /// real stored shape on NRE-class servers. The identity validation never
    /// reads TotalCount (the sentinel only governs exhaustion arithmetic), so
    /// both legs must discard identically.
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(Jellyfin.Plugin.AlexaSkill.Alexa.Util.SearchService.UnknownTotal)]
    public async Task PlaybackNearlyFinished_SingleSongAfterBookPlay_FetchesNothingForStaleBookContinuation(int totalCount)
    {
        using var queueManager = TestHelpers.CreateDeviceQueueManager("JF674BookInjection");
        var bookHandler = new PlayBookIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            queueManager);

        var session = CreateSession();
        _fx.SetupUserMock();

        var bookItem = new AudioBook { Id = Guid.NewGuid(), Name = "The Long Book" };
        var chapters = Enumerable.Range(0, ProgressiveQueueConstants.GetInitialFetchSize())
            .Select(i => new Audio
            {
                Id = Guid.NewGuid(),
                Name = $"Chapter {i + 1}",
                // JF-790: the tagged class, so the mint lands on the DB paged path
                // (untagged-fixture chapters now take the filename-order branch,
                // whose in-memory total is the served row count and mints nothing
                // against a mock that only models the 5-row page).
                IndexNumber = i + 1
            })
            .ToList();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.AudioBook))))
            .Returns(new List<BaseItem> { bookItem });

        // The initial chapters page (5 of totalCount). The SAME mock answers the
        // stale continuation's tail query on the unfixed tree: that fetch is the
        // bug.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == bookItem.Id)))
            .Returns(new QueryResult<BaseItem>
            {
                Items = chapters.Cast<BaseItem>().ToList(),
                TotalRecordCount = totalCount
            });

        var context = CreateContext();
        var bookResponse = await bookHandler.HandleAsync(
            CreateBookIntent("The Long Book"), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.NotNull(bookResponse.Response.Directives?.OfType<AudioPlayerPlayDirective>().FirstOrDefault());

        // The mint landed (the book has 20 chapters, the page holds 5).
        QueueContinuation? minted = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(minted);
        Assert.Equal("Audiobook", minted.SourceType);

        // Stop mid-book: the store entry lingers (documented; no PlaybackStopped removal).
        // Then the LATER single-song play, exactly as PlaySongIntentHandler's inline
        // path writes it: a one-item queue and NO continuation-store clear.
        var song = new Audio { Id = Guid.NewGuid(), Name = "One Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };
        session.FullNowPlayingItem = song;
        _fx.LibraryManager.Setup(l => l.GetItemById(song.Id)).Returns(song);

        var playbackHandler = new PlaybackNearlyFinishedEventHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory,
            queueManager);

        await playbackHandler.HandleAsync(
            CreateNearlyFinishedRequest(song.Id.ToString()),
            CreateContext(song.Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(song.Id, session.NowPlayingQueue[0].Id);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));
    }

    /// <summary>
    /// JF-674 linger semantics, the JF-574 flow the fix must NOT break: play an
    /// album (mint), stop mid-way (the entry lingers by design), lose the session
    /// queue to a restart/re-registration, then resume the SAME album. The
    /// rehydration guard rebuilds the session queue from the persisted device
    /// queue, and the continuation must STILL serve: the minted page is a subset
    /// of the rebuilt queue, so the identity validation passes and the fetch
    /// extends the queue past its initial page. The second leg re-fires
    /// NearlyFinished near the EXTENDED queue's tail: the identity must survive
    /// the queue's own growth (the minted page stays a subset; an order-sensitive
    /// or full-list fingerprint identity would fail here), and the exhausted
    /// store entry is dropped by the existing exhaustion bookkeeping.
    /// </summary>
    [Fact]
    public async Task PlaybackNearlyFinished_AlbumResumedAcrossSessionWipe_ContinuationStillServes()
    {
        using var queueManager = TestHelpers.CreateDeviceQueueManager("JF674AlbumLinger");
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            queueManager);

        var session = CreateSession();
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Id = albumId, Name = "Linger Album" };
        var allTracks = Enumerable.Range(0, 20)
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();
        var byId = allTracks.ToDictionary(t => t.Id, t => (BaseItem)t);
        byId[albumId] = album;

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        // One page-shaped mock answering the initial page AND every continuation
        // batch by offset (the album fetcher's pagination), so both legs fetch.
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns((InternalItemsQuery q) => new QueryResult<BaseItem>
            {
                Items = allTracks.Skip(q.StartIndex ?? 0).Take(q.Limit ?? int.MaxValue).Cast<BaseItem>().ToList(),
                TotalRecordCount = 20
            });

        _fx.LibraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => byId.TryGetValue(id, out BaseItem? item) ? item : null);

        var context = CreateContext();
        await handler.HandleAsync(CreateAlbumIntent("Linger Album"), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        int pageSize = ProgressiveQueueConstants.GetInitialFetchSize();
        Assert.Equal(pageSize, session.NowPlayingQueue.Count);
        QueueContinuation? minted = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(minted);

        // Stop mid-way (nothing to do: the entry lingers), then the JF-574 wiped
        // shape: the restart/re-registration empties the session queue. The
        // "resume" is the NearlyFinished for a mid-album item, whose rehydration
        // leg rebuilds the queue from the device store before the fetch runs.
        session.NowPlayingQueue = new List<QueueItem>();
        session.FullNowPlayingItem = null;

        var playbackHandler = new PlaybackNearlyFinishedEventHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory,
            queueManager);

        Guid thirdTrack = allTracks[2].Id;
        await playbackHandler.HandleAsync(
            CreateNearlyFinishedRequest(thirdTrack.ToString()),
            CreateContext(thirdTrack.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        // Rehydrated (5) + first batch (10): the continuation still serves.
        Assert.Equal(15, session.NowPlayingQueue.Count);
        Assert.NotNull(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));

        // Second leg: near the EXTENDED tail, the minted page is still a subset.
        Guid fourteenthTrack = allTracks[13].Id;
        await playbackHandler.HandleAsync(
            CreateNearlyFinishedRequest(fourteenthTrack.ToString()),
            CreateContext(fourteenthTrack.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Equal(20, session.NowPlayingQueue.Count);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));
    }

    /// <summary>
    /// JF-674, the music-arm twin of the book injection pin (the filing notes the
    /// Album/Artist/Playlist arms always had the same lingering-store property):
    /// a stale ALBUM continuation must not append the album's tail after a later
    /// single-song play either.
    /// </summary>
    [Fact]
    public async Task PlaybackNearlyFinished_SingleSongAfterAlbumPlay_FetchesNothingForStaleAlbumContinuation()
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
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Id = albumId, Name = "Stale Album" };
        var tracks = Enumerable.Range(0, ProgressiveQueueConstants.GetInitialFetchSize())
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new QueryResult<BaseItem>
            {
                Items = tracks.Cast<BaseItem>().ToList(),
                TotalRecordCount = 20
            });

        var context = CreateContext();
        await handler.HandleAsync(CreateAlbumIntent("Stale Album"), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.NotNull(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));

        // The later single-song play (the inline PlaySong shape: one-item queue,
        // no store clear).
        var song = new Audio { Id = Guid.NewGuid(), Name = "One Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };
        session.FullNowPlayingItem = song;
        _fx.LibraryManager.Setup(l => l.GetItemById(song.Id)).Returns(song);

        var playbackHandler = CreatePlaybackHandler();
        await playbackHandler.HandleAsync(
            CreateNearlyFinishedRequest(song.Id.ToString()),
            CreateContext(song.Id.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Single(session.NowPlayingQueue);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));
    }

    /// <summary>
    /// JF-674 gate-marker GM-F3: ClearQueue's NEW stop-semantics, pinned. ClearQueue
    /// trims the session queue to the currently-playing item and clears the device
    /// queue, but historically left the continuation store entry alone, so at the
    /// current item's end the fetch regrew the "cleared" queue with the album tail.
    /// The identity validation closes that as a deliberate behavior change (the
    /// trimmed-to-current queue no longer contains the other minted page ids, so
    /// the entry is discarded): a cleared queue now truly stops the progressive
    /// tail, the same discipline ClearQueue already applies to the precompute
    /// cache (the JF-424.1 NextTrackPrecomputeCache invalidation, without needing
    /// a new ClearQueue hook).
    /// </summary>
    [Fact]
    public async Task PlaybackNearlyFinished_AfterClearQueue_FetchesNothingAndDiscardsContinuation()
    {
        using var queueManager = TestHelpers.CreateDeviceQueueManager("JF674ClearQueue");
        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            queueManager);

        var session = CreateSession();
        _fx.SetupUserMock();

        var albumId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum { Id = albumId, Name = "Cleared Album" };
        var tracks = Enumerable.Range(0, ProgressiveQueueConstants.GetInitialFetchSize())
            .Select(i => new Audio { Id = Guid.NewGuid(), Name = $"Track {i + 1}" })
            .ToList();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });
        _fx.LibraryManager.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new QueryResult<BaseItem>
            {
                Items = tracks.Cast<BaseItem>().ToList(),
                TotalRecordCount = 20
            });
        foreach (Audio track in tracks)
        {
            _fx.LibraryManager.Setup(l => l.GetItemById(track.Id)).Returns(track);
        }

        var context = CreateContext();
        await handler.HandleAsync(CreateAlbumIntent("Cleared Album"), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.NotNull(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));

        // "Alexa, clear the queue": the REAL handler trims the session queue to the
        // current item and clears the device queue.
        var clearHandler = new ClearQueueIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LoggerFactory,
            queueManager);
        var clearRequest = new IntentRequest
        {
            Type = "IntentRequest",
            Intent = new Intent { Name = IntentNames.ClearQueue, Slots = new Dictionary<string, Slot>() }
        };
        await clearHandler.HandleAsync(clearRequest, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);
        Assert.Single(session.NowPlayingQueue);

        // The still-playing item's end: the trimmed queue cannot contain the minted
        // page, so the fetch serves nothing, the entry is discarded, and the
        // cleared queue does NOT regrow the album tail.
        var playbackHandler = new PlaybackNearlyFinishedEventHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.LoggerFactory,
            queueManager);
        Guid currentTrack = tracks[0].Id;
        await playbackHandler.HandleAsync(
            CreateNearlyFinishedRequest(currentTrack.ToString()),
            CreateContext(currentTrack.ToString()),
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        Assert.Single(session.NowPlayingQueue);
        Assert.Null(QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID));
    }

    /// <summary>JF-674: an entry minted without ids (the hand-constructed shape) skips validation.</summary>
    [Fact]
    public void QueueContinuation_IsForLiveQueue_EmptyIdentity_AlwaysTrue()
    {
        var session = CreateSession();
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = Guid.NewGuid() } };

        var continuation = new QueueContinuation { SourceType = "Album", UserId = Guid.Empty };
        Assert.True(continuation.IsForLiveQueue(session));
    }

    /// <summary>JF-674: every minted id queued (queue may be a superset, order-free) validates.</summary>
    [Fact]
    public void QueueContinuation_IsForLiveQueue_AllMintedIdsQueued_True()
    {
        var minted = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        // A superset in a DIFFERENT order, with fetched-batch items interleaved:
        // the identity is set membership, so this still validates.
        var session = CreateSession();
        session.NowPlayingQueue = new List<QueueItem>
        {
            new() { Id = Guid.NewGuid() },
            new() { Id = minted[2] },
            new() { Id = Guid.NewGuid() },
            new() { Id = minted[0] },
            new() { Id = minted[1] },
        };

        var continuation = new QueueContinuation { SourceType = "Album", UserId = Guid.Empty, MintedQueueItemIds = minted };
        Assert.True(continuation.IsForLiveQueue(session));
    }

    /// <summary>JF-674: any minted id absent from the live queue (the later-play shape) fails.</summary>
    [Fact]
    public void QueueContinuation_IsForLiveQueue_AnyMintedIdMissing_False()
    {
        var minted = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var session = CreateSession();
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = minted[0] } };

        var continuation = new QueueContinuation { SourceType = "Audiobook", UserId = Guid.Empty, MintedQueueItemIds = minted };
        Assert.False(continuation.IsForLiveQueue(session));
    }

    private static IntentRequest CreateBookIntent(string book)
        => new()
        {
            Type = "IntentRequest",
            Intent = new Intent
            {
                Name = IntentNames.PlayBook,
                Slots = new Dictionary<string, Slot> { ["book"] = new() { Value = book } }
            }
        };
}
