using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
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
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class SearchMediaIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SearchMediaIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        _config.AsrCompoundWordFixEnabled = false;
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SearchMediaIntentHandler CreateHandler()
    {
        return new SearchMediaIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? query = null)
    {
        var intent = new Intent { Name = IntentNames.SearchMedia };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (query != null)
        {
            intent.Slots["query"] = new global::Alexa.NET.Request.Slot { Name = "query", Value = query };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    [Fact]
    public void CanHandle_SearchMediaIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "test song");

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
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_MissingQuery_ReturnsCouldNotUnderstand()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("understand", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NoResults_ReturnsMediaNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "nonexistent");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SingleAudioResult_ReturnsAudioPlayerResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Bohemian Rhapsody");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio
        {
            Name = "Bohemian Rhapsody",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(audio.Id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task HandleAsync_SingleVideoResult_ReturnsVideoAppResponse()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Inception");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var movie = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Inception",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        // VideoApp.Launch must NOT include shouldEndSession
        Assert.Null(response.Response.ShouldEndSession);
        Assert.NotEmpty(response.Response.Directives);
        // JF-349: video launch now announces the title (was silent).
        Assert.NotNull(response.Response.OutputSpeech);
        string announceText = response.Response.OutputSpeech is SsmlOutputSpeech ss
            ? ss.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;
        Assert.Contains("Inception", announceText, StringComparison.Ordinal);
        Assert.NotNull(session.FullNowPlayingItem);
        Assert.Equal(movie, session.FullNowPlayingItem);
    }

    [Fact]
    public async Task HandleAsync_SingleVideoResult_WithProgress_AnnouncesResumePosition()
    {
        // C4: a half-watched movie found via search announces "Resuming X from Y"
        // (matching PlayVideo), not "Now playing".
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Inception");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();
        SetupUserMock();

        var movie = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Inception",
            Id = Guid.NewGuid()
        };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });
        _userDataManagerMock.Setup(x => x.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response.OutputSpeech);
        string announceText = response.Response.OutputSpeech is SsmlOutputSpeech ss
            ? ss.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;
        Assert.Contains("Resuming", announceText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_MultipleResults_ReturnsDisambiguation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Star Wars");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var item1 = new Audio
        {
            Name = "Star Trek Theme",
            Id = Guid.NewGuid()
        };

        var item2 = new global::MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "Stargate",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { item1, item2 });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    [Fact]
    public async Task HandleAsync_SetsQueueAndNowPlayingItem()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Test Song");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio
        {
            Name = "Test Song",
            Id = Guid.NewGuid()
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(audio.Id, session.NowPlayingQueue[0].Id);
        Assert.Equal(audio, session.FullNowPlayingItem);
    }

    [Fact]
    public async Task HandleAsync_DeduplicatesResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Test");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audioId = Guid.NewGuid();
        var audio1 = new Audio
        {
            Name = "Test Song",
            Id = audioId
        };
        var audio2 = new Audio
        {
            Name = "Test Song",
            Id = audioId
        };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio1, audio2 });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Deduplication means single result → plays directly
        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
    }

    [Fact]
    public async Task HandleAsync_SearchQueryUsesPlayableTypes()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Test");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        InternalItemsQuery? capturedQuery = null;
        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => { if (++callCount == 1) capturedQuery = q; })
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(capturedQuery);
        Assert.Equal("Test", capturedQuery.SearchTerm);
        Assert.NotNull(capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Audio, capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Movie, capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Episode, capturedQuery.IncludeItemTypes);
        Assert.Contains(BaseItemKind.Series, capturedQuery.IncludeItemTypes);
    }

    [Fact]
    public async Task HandleAsync_ZeroResults_ArtistFound_ReturnsArtistSongs()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Soul Coughing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();
        SetupUserMock();

        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var song1 = new Audio { Name = "Circles", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Screenwriter's Blues", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem>(),           // initial title search: empty
                    2 => new List<BaseItem> { artist },   // artist lookup: found
                    3 => new List<BaseItem> { song1, song2 }, // artist items
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // 2 songs → disambiguation (not auto-play)
        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    [Fact]
    public async Task HandleAsync_ZeroResults_NoArtist_ReturnsMediaNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "nonexistent");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();
        SetupUserMock();

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem>(),  // title search: empty
                    2 => new List<BaseItem>(),  // artist lookup: empty
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("not find", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SparseResults_ArtistFound_MergesResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "Soul Coughing");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();
        SetupUserMock();

        var titleResult = new Audio { Name = "Lust in Phaze", Id = Guid.NewGuid() };
        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var artistSong = new Audio { Name = "Circles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem> { titleResult },  // 1 title result (sparse)
                    2 => new List<BaseItem> { artist },        // artist found
                    3 => new List<BaseItem> { artistSong },    // artist's songs
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // titleResult + artistSong = 2 items → disambiguation
        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    [Fact]
    public async Task HandleAsync_SparseResults_NoArtist_ReturnsOriginalResults()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "nonexistent artist");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();
        SetupUserMock();

        // Items whose names won't fuzzy-match the query "nonexistent artist"
        var song1 = new Audio { Name = "Alpha Track", Id = Guid.NewGuid() };
        var song2 = new Audio { Name = "Beta Track", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => new List<BaseItem> { song1, song2 },  // 2 results (sparse)
                    2 => new List<BaseItem>(),                  // no artist
                    _ => new List<BaseItem>()
                };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Original 2 items, no fuzzy match → disambiguation
        Assert.NotNull(response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
    }

    [Fact]
    public async Task HandleAsync_ManyResults_NoArtistFallback()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "test");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();
        SetupUserMock();

        var items = Enumerable.Range(0, 5)
            .Select(i => new Audio { Name = $"Song {i}", Id = Guid.NewGuid() })
            .ToList<BaseItem>();

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // With >3 results, artist fallback is NOT triggered → only 1 call to GetItemList
        _libraryManagerMock.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Once());
    }

    // --- JF-456: out-of-library kinds (playlists) stay searchable for restricted users (GH #22 residual) ---

    private static Entities.User CreateRestrictedUser(Guid libraryId)
    {
        return TestHelpers.CreateTestUser(allowedLibraryIds: new[] { libraryId.ToString() });
    }

    [Fact]
    public async Task HandleAsync_RestrictedUser_PlaylistFoundViaUnfilteredSiblingQuery()
    {
        // A library-restricted user searching for a playlist must still find it: the
        // mixed unified query would drop it (TopParentIds excludes the PlaylistsFolder),
        // so the out-of-library sibling query runs WITHOUT the filter (JF-456).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "road trip");
        var context = CreateContext();
        var libraryId = Guid.NewGuid();
        var user = CreateRestrictedUser(libraryId);
        var session = CreateSession();
        SetupUserMock();

        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Name = "Road Trip",
            Id = Guid.NewGuid()
        };

        var capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns((InternalItemsQuery q) =>
                q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist
                    ? new List<BaseItem> { playlist }
                    : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The playlist was found by the sibling query and auto-played as the single result
        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(playlist.Id, Assert.Single(session.NowPlayingQueue).Id);

        // The playlist query carried no library filter; the library-scoped query did
        var playlistQuery = Assert.Single(capturedQueries.Where(q =>
            q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist));
        Assert.Empty(playlistQuery.TopParentIds);
        Assert.Contains(capturedQueries, q =>
            q.IncludeItemTypes.Contains(BaseItemKind.Audio)
            && q.TopParentIds?.Contains(libraryId) == true);
    }

    [Fact]
    public async Task HandleAsync_RestrictedUser_FuzzyMiss_FallsBackToOutOfLibraryKinds()
    {
        // When neither the scoped primary nor the scoped fuzzy pass matches, the
        // out-of-library fuzzy pass still runs (the old mixed fuzzy array would have
        // hidden the playlist behind the TopParentIds filter, JF-456).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "road trip playlist");
        var context = CreateContext();
        var libraryId = Guid.NewGuid();
        var user = CreateRestrictedUser(libraryId);
        var session = CreateSession();
        SetupUserMock();

        var playlist = new MediaBrowser.Controller.Playlists.Playlist
        {
            Name = "Road Trip Playlist",
            Id = Guid.NewGuid()
        };

        var capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns((InternalItemsQuery q) =>
                // Only the FUZZY playlist query matches (SearchTerm null): the primary
                // sibling and every scoped query miss, forcing the fuzzy chain.
                q.IncludeItemTypes.Length == 1
                    && q.IncludeItemTypes[0] == BaseItemKind.Playlist
                    && string.IsNullOrEmpty(q.SearchTerm)
                    ? new List<BaseItem> { playlist }
                    : new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        response.HasDirective<AudioPlayerPlayDirective>();

        // The fuzzy out-of-library query ran unfiltered and found the playlist
        var fuzzyPlaylistQuery = Assert.Single(capturedQueries.Where(q =>
            q.IncludeItemTypes.Length == 1
            && q.IncludeItemTypes[0] == BaseItemKind.Playlist
            && string.IsNullOrEmpty(q.SearchTerm)));
        Assert.Empty(fuzzyPlaylistQuery.TopParentIds);
    }

    [Fact]
    public async Task HandleAsync_UnrestrictedUser_SingleUnifiedQuery_IncludesPlaylistKind()
    {
        // Unrestricted users keep the single unified query: the playlist kind rides it
        // with no TopParentIds set, and no sibling query is issued (JF-456).
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "test");
        var context = CreateContext();
        var user = CreateUser(); // unrestricted
        var session = CreateSession();
        SetupUserMock();

        var capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns(new List<BaseItem>());

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Contains(capturedQueries, q =>
            q.IncludeItemTypes.Contains(BaseItemKind.Playlist)
            && q.IncludeItemTypes.Contains(BaseItemKind.Audio)
            && q.TopParentIds.Length == 0);
        Assert.DoesNotContain(capturedQueries, q =>
            q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist);
    }

    [Fact]
    public async Task HandleAsync_RestrictedUser_SiblingQuerySkipped_WhenScopedPageSaturatesLimit()
    {
        // Saturation skip (code-review round 2 item 3): a scoped page that came
        // back AT its Limit cannot be improved by the playlist sibling, whose rows
        // the union cap would discard anyway, so the sibling must not be issued:
        // one DB roundtrip saved per attempt on the miss paths inside the 8s window.
        var handler = CreateHandler();
        var request = CreateIntentRequest(query: "song");
        var context = CreateContext();
        var libraryId = Guid.NewGuid();
        var user = CreateRestrictedUser(libraryId);
        var session = CreateSession();
        SetupUserMock();

        int limit = global::Jellyfin.Plugin.AlexaSkill.Plugin.Instance?.Configuration?.MaxSearchResults ?? 20;
        var fullPage = Enumerable.Range(0, limit)
            .Select(i => new Audio { Name = $"Item {i:000}", Id = Guid.NewGuid() })
            .ToList<BaseItem>();

        var capturedQueries = new List<InternalItemsQuery>();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => capturedQueries.Add(q))
            .Returns((InternalItemsQuery q) =>
                q.IncludeItemTypes.Contains(BaseItemKind.Playlist)
                    ? new List<BaseItem>() // sibling must never be reached
                    : fullPage);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The scoped query ran, carried the restriction, and returned a full page.
        Assert.Contains(capturedQueries, q =>
            q.IncludeItemTypes.Contains(BaseItemKind.Audio)
            && q.TopParentIds?.Contains(libraryId) == true);
        // No playlist-only sibling query was issued (no primary one, and none from
        // the fuzzy chain either: results were non-empty so the chain never ran).
        Assert.DoesNotContain(capturedQueries, q =>
            q.IncludeItemTypes.Length == 1 && q.IncludeItemTypes[0] == BaseItemKind.Playlist);
    }
}
