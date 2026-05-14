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

public class SearchMediaIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SearchMediaIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
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
        Assert.True(response.Response.ShouldEndSession);
        Assert.NotEmpty(response.Response.Directives);
        Assert.NotNull(session.FullNowPlayingItem);
        Assert.Equal(movie, session.FullNowPlayingItem);
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
}
