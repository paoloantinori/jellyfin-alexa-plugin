using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class PlayArtistSongsIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly Mock<IArtistIndex> _artistIndexMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayArtistSongsIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _artistIndexMock = new Mock<IArtistIndex>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayArtistSongsIntentHandler CreateHandler(IArtistIndex? artistIndex = null)
    {
        return new PlayArtistSongsIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory,
            artistIndex);
    }

    private static IntentRequest CreateIntentRequest(string? musician = null)
    {
        var intent = new Intent { Name = IntentNames.PlayArtistSongs };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (musician != null)
        {
            intent.Slots["musician"] = new global::Alexa.NET.Request.Slot { Name = "musician", Value = musician };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private void SetupSongResult(params Audio[] songs)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(songs.ToList<BaseItem>());
    }

    [Fact]
    public void CanHandle_PlayArtistSongsIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Beatles");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_WithInMemoryIndex_FindsArtist()
    {
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { artist };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);

        SetupUserMock();
        SetupSongResult(
            new Audio { Name = "Yesterday", Id = Guid.NewGuid() },
            new Audio { Name = "Let It Be", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        // Verify the index was queried, not the DB
        _artistIndexMock.Verify(i => i.GetArtists(It.IsAny<Guid[]?>()), Times.Once);
        _libraryManagerMock.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == Jellyfin.Data.Enums.BaseItemKind.MusicArtist))), Times.Never);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_IndexNotReady_FallsBackToDatabase()
    {
        _artistIndexMock.Setup(i => i.IsReady).Returns(false);

        SetupUserMock();
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem>();
            });

        SetupSongResult(new Audio { Name = "Yesterday", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        // Verify DB was queried (fallback path)
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.AtLeastOnce);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_NoArtistIndex_FallsBackToDatabase()
    {
        SetupUserMock();
        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem>();
            });

        SetupSongResult(new Audio { Name = "Yesterday", Id = Guid.NewGuid() });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.AtLeastOnce);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_InMemoryIndex_FuzzyMatch()
    {
        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { artist };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);

        SetupUserMock();
        SetupSongResult(new Audio { Name = "Screenwriter's Blues", Id = Guid.NewGuid() });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "soul coughin"); // misspelling
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_MissingArtistName_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("artist", speech, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_ArtistNotFound_ReturnsNotFound()
    {
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(new List<BaseItem>());

        SetupUserMock();

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Unknown");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Unknown", speech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_NoSongsForArtist_ReturnsNoSongs()
    {
        var artist = new MusicArtist { Name = "Empty Artist", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { artist };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Empty Artist");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// Verifies that the library filter is resolved only once per request in the database
    /// fallback path, even when all four search tiers are exercised.
    /// Before the optimization, ResolveTopParentIds was called once per query (up to 5 calls).
    /// After, it is called exactly once.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DatabaseFallback_ResolveLibraryFilterOnce()
    {
        // Arrange: user with library restriction so ResolveTopParentIds is invoked.
        var libraryId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser();
        user.AllowedLibraryIds = new List<string> { libraryId.ToString() };

        // The CollectionFolder resolves to a physical folder.
        var physicalFolderId = Guid.NewGuid();
        var cf = new CollectionFolder { Id = libraryId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };

        var physicalFolder = new Folder { Id = physicalFolderId };

        _libraryManagerMock.Setup(l => l.GetItemById(libraryId))
            .Returns(cf);
        _libraryManagerMock.Setup(l => l.FindByPath("/data/media/music", true))
            .Returns(physicalFolder);

        SetupUserMock();

        // No artist found in first 3 tiers, found in 4th tier -- forces all 4 tiers + artist songs query.
        int getItemListCallCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                getItemListCallCount++;
                if (getItemListCallCount == 4)
                {
                    return new List<BaseItem> { new MusicArtist { Name = "Test Artist", Id = Guid.NewGuid() } };
                }

                return new List<BaseItem>();
            });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Test Song", Id = Guid.NewGuid() }
            });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "xyzzyfoo");
        var context = CreateContext();
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: response is valid (artist found and songs played)
        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);

        // GetItemById (used by ResolveTopParentIds) should be called exactly once,
        // not once per query tier. Before the optimization it would be called 5 times
        // (SearchTerm + PrefixFirstWord + PrefixFull + Contains + ArtistSongs).
        _libraryManagerMock.Verify(
            l => l.GetItemById(libraryId),
            Times.Once,
            "Library filter should be resolved exactly once per request, not once per query tier");
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// Verifies that all database fallback queries receive the same TopParentIds
    /// value (the pre-resolved filter), not re-resolved per tier.
    /// </summary>
    [Fact]
    public async Task HandleAsync_DatabaseFallback_AllQueriesShareResolvedFilter()
    {
        // Arrange: user with 2 library restrictions
        var libraryId1 = Guid.NewGuid();
        var libraryId2 = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser();
        user.AllowedLibraryIds = new List<string> { libraryId1.ToString(), libraryId2.ToString() };

        var cf1 = new CollectionFolder { Id = libraryId1 };
        cf1.PhysicalLocationsList = new[] { "/media/music" };
        var cf2 = new CollectionFolder { Id = libraryId2 };
        cf2.PhysicalLocationsList = new[] { "/media/jazz" };

        var folder1 = new Folder { Id = Guid.NewGuid() };
        var folder2 = new Folder { Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemById(libraryId1)).Returns(cf1);
        _libraryManagerMock.Setup(l => l.GetItemById(libraryId2)).Returns(cf2);
        _libraryManagerMock.Setup(l => l.FindByPath("/media/music", true)).Returns(folder1);
        _libraryManagerMock.Setup(l => l.FindByPath("/media/jazz", true)).Returns(folder2);

        SetupUserMock();

        // Return an artist on the first tier
        var artist = new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { artist });

        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Comfortably Numb", Id = Guid.NewGuid() }
            });

        var handler = CreateHandler(artistIndex: null);
        var request = CreateIntentRequest(musician: "Pink Floyd");
        var context = CreateContext();
        var session = CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert
        Assert.NotNull(response);

        // GetItemById should be called exactly twice (once per library ID), not 2*N per query
        _libraryManagerMock.Verify(l => l.GetItemById(libraryId1), Times.Once);
        _libraryManagerMock.Verify(l => l.GetItemById(libraryId2), Times.Once);
        Assert.True(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// Verifies that the in-memory path also resolves the library filter only once.
    /// </summary>
    [Fact]
    public async Task HandleAsync_InMemoryPath_ResolveLibraryFilterOnce()
    {
        var libraryId = Guid.NewGuid();
        var user = TestHelpers.CreateTestUser();
        user.AllowedLibraryIds = new List<string> { libraryId.ToString() };

        var cf = new CollectionFolder { Id = libraryId };
        cf.PhysicalLocationsList = new[] { "/data/media/music" };
        var physicalFolder = new Folder { Id = Guid.NewGuid() };

        _libraryManagerMock.Setup(l => l.GetItemById(libraryId))
            .Returns(cf);
        _libraryManagerMock.Setup(l => l.FindByPath("/data/media/music", true))
            .Returns(physicalFolder);

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>()))
            .Returns(new List<BaseItem> { new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() } });

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ArtistIds != null && q.ArtistIds.Length > 0)))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Yesterday", Id = Guid.NewGuid() }
            });

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);

        // In-memory path resolves the filter once for artist search and reuses it for songs query.
        // GetItemById is called exactly once (for the single pre-resolution).
        _libraryManagerMock.Verify(
            l => l.GetItemById(libraryId),
            Times.Once,
            "In-memory path should resolve library filter exactly once");
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_ShuffleArtistSongsOff_BuildsPopularityOrderQueue()
    {
        _config.ShuffleArtistSongs = false;

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>()))
            .Returns(new List<BaseItem> { artist });

        SetupUserMock();

        var songs = new[]
        {
            new Audio { Name = "Yesterday", Id = Guid.NewGuid() },
            new Audio { Name = "Let It Be", Id = Guid.NewGuid() },
            new Audio { Name = "Hey Jude", Id = Guid.NewGuid() }
        };
        SetupSongResult(songs);

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        // Queue should preserve original order when shuffle is off
        Assert.Equal(3, session.NowPlayingQueue.Count);
        Assert.Equal(songs[0].Id, session.NowPlayingQueue[0].Id);
        Assert.Equal(songs[1].Id, session.NowPlayingQueue[1].Id);
        Assert.Equal(songs[2].Id, session.NowPlayingQueue[2].Id);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_ShuffleArtistSongsOn_RandomizesQueueOrder()
    {
        _config.ShuffleArtistSongs = true;

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>()))
            .Returns(new List<BaseItem> { artist });

        SetupUserMock();

        // Use enough songs that a shuffle is statistically certain to differ from original order
        var songs = Enumerable.Range(0, 20)
            .Select(i => new Audio { Name = $"Song {i}", Id = Guid.NewGuid() })
            .ToArray();
        SetupSongResult(songs);

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(20, session.NowPlayingQueue.Count);

        // All items must be present (no duplicates, no losses)
        var queueIds = session.NowPlayingQueue.Select(q => q.Id).ToHashSet();
        Assert.Equal(songs.Length, queueIds.Count);
        Assert.All(songs, s => Assert.Contains(s.Id, queueIds));

        // With 20 items, the probability that shuffle produces the exact original order is 1/20! ≈ 4e-19
        bool anyReordered = false;
        for (int i = 0; i < songs.Length; i++)
        {
            if (session.NowPlayingQueue[i].Id != songs[i].Id)
            {
                anyReordered = true;
                break;
            }
        }

        Assert.True(anyReordered, "Shuffle should reorder at least one item in a 20-song queue");
        Assert.True(response.Response.ShouldEndSession);
    }
}