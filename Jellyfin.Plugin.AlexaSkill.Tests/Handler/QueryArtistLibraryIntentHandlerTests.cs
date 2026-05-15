using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class QueryArtistLibraryIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public QueryArtistLibraryIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private QueryArtistLibraryIntentHandler CreateHandler()
    {
        return new QueryArtistLibraryIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? musician = null, string? queryType = null)
    {
        var intent = new Intent { Name = IntentNames.QueryArtistLibrary };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (musician != null)
        {
            intent.Slots["musician"] = new global::Alexa.NET.Request.Slot { Name = "musician", Value = musician };
        }

        if (queryType != null)
        {
            intent.Slots["query_type"] = new global::Alexa.NET.Request.Slot { Name = "query_type", Value = queryType };
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

    private void SetupArtist(params MusicArtist[] artists)
    {
        // First call = artist search, subsequent calls = track/album search
        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new List<BaseItem>(artists);
                }

                return new List<BaseItem>();
            });
    }

    [Fact]
    public void CanHandle_QueryArtistLibraryIntent_ReturnsTrue()
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
    }

    [Fact]
    public async Task HandleAsync_ArtistNotFound_ReturnsNotFound()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Unknown");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Unknown", speech);
    }

    [Fact]
    public async Task HandleAsync_TracksByArtist_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var track1 = new Audio { Name = "Yesterday", Id = Guid.NewGuid() };
        var track2 = new Audio { Name = "Let It Be", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { track1, track2 };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Yesterday", speech);
        Assert.Contains("Let It Be", speech);
    }

    [Fact]
    public async Task HandleAsync_AlbumsByArtist_ReturnsList()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Beatles", queryType: "albums");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var album = new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { album };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Abbey Road", speech);
    }

    [Fact]
    public async Task HandleAsync_NoTracksForArtist_ReturnsNoSongs()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

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

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_LargeTrackList_Paginates()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Beatles");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var tracks = new List<BaseItem>();
        for (int i = 0; i < 10; i++)
        {
            tracks.Add(new Audio { Name = $"Track {i}", Id = Guid.NewGuid() });
        }

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : tracks;
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Track 0", speech);
        Assert.DoesNotContain("Track 9", speech);
    }

    [Fact]
    public async Task HandleAsync_TracksByArtist_WithApl_IncludesAplDirective()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Soul Coughing");
        var context = TestHelpers.CreateContextWithApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var track1 = new Audio { Name = "Screenwriter's Blues", Id = Guid.NewGuid() };
        var track2 = new Audio { Name = "Super Bon Bon", Id = Guid.NewGuid() };
        var track3 = new Audio { Name = "Circles", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { track1, track2, track3 };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");

        var aplDirective = response.Response.Directives.First(d => d.Type == "Alexa.Presentation.APL.RenderDocument") as AplRenderDocumentDirective;
        Assert.NotNull(aplDirective);
        Assert.Equal("queryArtist", aplDirective.Token);
        Assert.NotNull(aplDirective.DataSources);
    }

    [Fact]
    public async Task HandleAsync_TracksByArtist_WithoutApl_NoAplDirective()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Soul Coughing");
        var context = TestHelpers.CreateContextWithoutApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() };
        var track1 = new Audio { Name = "Screenwriter's Blues", Id = Guid.NewGuid() };
        var track2 = new Audio { Name = "Super Bon Bon", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { track1, track2 };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.DoesNotContain(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }

    [Fact]
    public async Task HandleAsync_AlbumsByArtist_WithApl_IncludesAplDirective()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(musician: "Beatles", queryType: "albums");
        var context = TestHelpers.CreateContextWithApl();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var artist = new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() };
        var album1 = new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Let It Be", Id = Guid.NewGuid() };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1
                    ? new List<BaseItem> { artist }
                    : new List<BaseItem> { album1, album2 };
            });

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains(response.Response.Directives, d => d.Type == "Alexa.Presentation.APL.RenderDocument");
    }
}
