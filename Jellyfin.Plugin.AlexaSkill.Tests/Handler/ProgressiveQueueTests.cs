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

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for progressive queue building (JF-124).
/// Verifies that bulk-play handlers fetch only an initial page of items,
/// store continuation state, and that PlaybackNearlyFinished fetches more
/// items on demand.
/// </summary>
[Collection("PlaybackHandlers")]
public class ProgressiveQueueTests : IDisposable
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
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096" };
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

        // Mock: artist songs query with limit
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Contains(artistId))))
            .Returns(new QueryResult<BaseItem>
            {
                Items = allTracks.Take(ProgressiveQueueConstants.GetInitialFetchSize()).Cast<MediaBrowser.Controller.Entities.BaseItem>().ToList(),
                TotalRecordCount = 15
            });

        // Mock: no favorites
        _userDataManagerMock.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<MediaBrowser.Controller.Entities.BaseItem>()))
            .Returns((UserItemData?)null);

        var request = CreateArtistSongsIntent("Test Artist");
        var context = CreateContext();

        var response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        // Should have only initial items in queue
        Assert.Equal(ProgressiveQueueConstants.GetInitialFetchSize(), session.NowPlayingQueue.Count);

        // Continuation should be stored
        QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, context.System.Device.DeviceID);
        Assert.NotNull(continuation);
        Assert.Equal("Artist", continuation.SourceType);
        Assert.Equal(artistId, continuation.ArtistId);
        Assert.Equal(15, continuation.TotalCount);

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
}
