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
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
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
public class PlayAlbumIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DeviceQueueManager _queueManager;

    public PlayAlbumIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
        var queueLogger = new Mock<ILogger<DeviceQueueManager>>();
        _queueManager = new DeviceQueueManager(System.IO.Path.GetTempPath(), queueLogger.Object);

        TestHelpers.EnsurePluginInstance(_config, _loggerFactory, c => { }, "playalbum-tests");
    }

    private PlayAlbumIntentHandler CreateHandler()
    {
        return new PlayAlbumIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory,
            _queueManager);
    }

    private static IntentRequest CreateIntentRequest(string? album = null)
    {
        var intent = new Intent { Name = IntentNames.PlayAlbum };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (album != null)
        {
            intent.Slots["album"] = new global::Alexa.NET.Request.Slot { Name = "album", Value = album };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private SessionInfo CreateSession()
    {
        var session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.DeviceId = "test-device";
        return session;
    }

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private void SetupAlbumsAndTracks(List<BaseItem> albums, QueryResult<BaseItem>? tracks = null)
    {
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(albums);
        if (tracks != null)
        {
            _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
                .Returns(tracks);
        }
    }

    [Fact]
    public async Task HandleAsync_MultipleDistinctNameAlbums_PromptsDisambiguation()
    {
        // JF-341: distinct-name multi-match (e.g. "Greatest Hits" vs "Biggest Hits") must
        // prompt the user (AskFirstMatch), not silently auto-play the best-scoring one.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "hits");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(new List<BaseItem> { album1, album2 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        // AskFirstMatch keeps the session open + does NOT auto-play.
        Assert.False(response.Response.ShouldEndSession);
        Assert.Null(response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective));
    }

    [Fact]
    public async Task HandleAsync_MultipleDistinctNameAlbums_AutoPlayUser_AutoPlaysFirst()
    {
        // JF-341 review: an AutoPlay user (opted out of disambiguation prompts) auto-plays
        // even for distinct-name collisions.
        var user = TestHelpers.CreateTestUser();
        user.FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay;
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "hits");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Greatest Hits", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Biggest Hits", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_MultipleSameNameAlbums_AutoPlaysFirst()
    {
        // JF-341: same-name duplicates (e.g. two "Jazz Cafe" disc-albums) auto-play the first
        // -- a "Jazz Cafe or Jazz Cafe?" prompt would be useless.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "jazz cafe");
        SetupUserMock();

        var album1 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var album2 = new MusicAlbum { Name = "Jazz Cafe", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album1, album2 },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }

    [Fact]
    public async Task HandleAsync_SingleAlbum_AutoPlays()
    {
        // Regression guard (AC#5): single-match albums still auto-play.
        var handler = CreateHandler();
        var request = CreateIntentRequest(album: "the album");
        SetupUserMock();

        var album = new MusicAlbum { Name = "The Album", Id = Guid.NewGuid() };
        var track = new Audio { Name = "Track 1", Id = Guid.NewGuid() };
        SetupAlbumsAndTracks(
            new List<BaseItem> { album },
            new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 });

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
        var playDirective = response.Response.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;
        Assert.NotNull(playDirective);
    }
}
