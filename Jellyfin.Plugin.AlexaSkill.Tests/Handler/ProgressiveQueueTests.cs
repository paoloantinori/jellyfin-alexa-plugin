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
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;

    private static readonly string DeviceId = "test-device";

    public ProgressiveQueueTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096", AsrCompoundWordFixEnabled = false };
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();

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
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
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
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackNearlyFinished",
            Token = token ?? Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 0
        };
    }

    private PlaybackNearlyFinishedEventHandler CreatePlaybackHandler()
    {
        return new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
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
        SetupUserMock();

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

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { track4, track5 },
                TotalRecordCount = 2
            });

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id))
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

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id))
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
        SetupUserMock();

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
        SetupUserMock();

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

        _libraryManagerMock.Setup(l => l.GetItemById(tracks[1]))
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
        _libraryManagerMock.Verify(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()), Times.Never);

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
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

        var session = CreateSession();
        SetupUserMock();

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
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { album });

        // Mock: album track query returns first page (5 items) with total count 20
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
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
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

        var session = CreateSession();
        SetupUserMock();

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

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { album });

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
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
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

        var session = CreateSession();
        SetupUserMock();

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
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { artist });

        // Mock: artist songs query with limit (uses GetItemList to avoid Jellyfin NRE)
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Contains(artistId))))
            .Returns(allTracks.Take(ProgressiveQueueConstants.GetInitialFetchSize()).Cast<MediaBrowser.Controller.Entities.BaseItem>().ToList());

        // Mock: no favorites
        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<MediaBrowser.Controller.Entities.BaseItem>()))
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
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

        var session = CreateSession();
        SetupUserMock();

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
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.SearchTerm == "Kidz Bop" && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());

        // Mock: first-word prefix query ("Kidz") returns empty
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.NameStartsWith == "Kidz" && q.SearchTerm == null)))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());

        // Mock: full-prefix query ("Kidz Bop") returns the artist
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.NameStartsWith == "Kidz Bop" && q.SearchTerm == null)))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { artist });

        // Mock: artist songs query (uses GetItemList to avoid Jellyfin NRE)
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
            q.ArtistIds != null && q.ArtistIds.Contains(artistId))))
            .Returns(tracks.Cast<MediaBrowser.Controller.Entities.BaseItem>().ToList());

        // Mock: no favorites
        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<MediaBrowser.Controller.Entities.BaseItem>()))
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
        SetupUserMock();

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

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(continuationTracks.ToList<BaseItem>());

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id))
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
        SetupUserMock();

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

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(contTracks.Cast<BaseItem>().ToList());

        _libraryManagerMock.Setup(l => l.GetItemById(track2Id))
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
        SetupUserMock();

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

        ILogger logger = _loggerFactory.CreateLogger("PlaylistContinuationTest");

        // Batch 1: first 4
        IReadOnlyList<BaseItem> batch1 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _libraryManagerMock.Object, _userManagerMock.Object, logger);
        Assert.Equal(ids.GetRange(0, 4), batch1.Select(b => b.Id).ToList());
        Assert.Equal(4, continuation.StartIndex);

        // Batch 2: next 4
        IReadOnlyList<BaseItem> batch2 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _libraryManagerMock.Object, _userManagerMock.Object, logger);
        Assert.Equal(ids.GetRange(4, 4), batch2.Select(b => b.Id).ToList());
        Assert.Equal(8, continuation.StartIndex);

        // Batch 3: remaining 2
        IReadOnlyList<BaseItem> batch3 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _libraryManagerMock.Object, _userManagerMock.Object, logger);
        Assert.Equal(ids.GetRange(8, 2), batch3.Select(b => b.Id).ToList());
        Assert.Equal(10, continuation.StartIndex);

        // Batch 4: exhausted -> empty (StartIndex >= TotalCount short-circuit)
        IReadOnlyList<BaseItem> batch4 = QueueContinuationFetcher.FetchNextBatch(
            continuation, _libraryManagerMock.Object, _userManagerMock.Object, logger);
        Assert.Empty(batch4);

        // The cached path must NOT touch the library manager at all (no re-resolution).
        _libraryManagerMock.Verify(
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
        SetupUserMock();

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
        _libraryManagerMock
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "t" } },
                TotalRecordCount = 30
            });

        ILogger logger = _loggerFactory.CreateLogger("AlbumOrderTest");
        QueueContinuationFetcher.FetchNextBatch(continuation, _libraryManagerMock.Object, _userManagerMock.Object, logger);

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
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

        var session = CreateSession();
        SetupUserMock();

        var albumId = Guid.NewGuid();
        var album = new MediaBrowser.Controller.Entities.Audio.MusicAlbum
        {
            Id = albumId,
            Name = "Multi-Disc Album"
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        InternalItemsQuery? capturedTrackQuery = null;
        _libraryManagerMock
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
        SetupUserMock();

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
        _libraryManagerMock
            .Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.AlbumIds == null || q.AlbumIds.Length == 0)))
            .Returns(new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 });

        // AlbumIds fallback returns tracks; capture its query.
        _libraryManagerMock
            .Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.AlbumIds != null && q.AlbumIds.Contains(albumId))))
            .Callback<InternalItemsQuery>(q => capturedFallback = q)
            .Returns(new QueryResult<BaseItem>
            {
                Items = new List<BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "t" } },
                TotalRecordCount = 30
            });

        ILogger logger = _loggerFactory.CreateLogger("AlbumIdsFallbackOrderTest");
        QueueContinuationFetcher.FetchNextBatch(continuation, _libraryManagerMock.Object, _userManagerMock.Object, logger);

        Assert.NotNull(capturedFallback);
        Assert.Equal(
            QueueContinuationFetcher.AlbumTrackOrder,
            capturedFallback!.OrderBy);
    }

    [Fact]
    public async Task PlayAlbum_MultiDiscAlbum_QueueFollowsDiscThenTrackOrder()
    {
        // Verifies the handler preserves the DB's disc/track order into the queue (no reshuffle).
        var handler = new PlayAlbumIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

        var session = CreateSession();
        SetupUserMock();

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

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem> { album });

        _libraryManagerMock.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ParentId == albumId)))
            .Returns(new QueryResult<BaseItem> { Items = ordered, TotalRecordCount = ordered.Count });

        var context = CreateContext();
        await handler.HandleAsync(CreateAlbumIntent("Double Album"), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // The playback queue must be disc 1 (t1, t2) then disc 2 (t1, t2) — not reshuffled.
        var expectedIds = new[] { d1t1.Id, d1t2.Id, d2t1.Id, d2t2.Id };
        Assert.Equal(expectedIds, session.NowPlayingQueue.Select(q => q.Id).ToArray());

        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);
    }
}
