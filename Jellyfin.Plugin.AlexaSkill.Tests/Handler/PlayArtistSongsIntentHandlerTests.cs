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

public class PlayArtistSongsIntentHandlerTests
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
        _config = new PluginConfiguration();
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
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>(songs.ToList<BaseItem>()));
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
    }

    [Fact]
    public async Task HandleAsync_NoSongsForArtist_ReturnsNoSongs()
    {
        var artist = new MusicArtist { Name = "Empty Artist", Id = Guid.NewGuid() };
        var allArtists = new List<BaseItem> { artist };

        _artistIndexMock.Setup(i => i.IsReady).Returns(true);
        _artistIndexMock.Setup(i => i.GetArtists(It.IsAny<Guid[]?>())).Returns(allArtists);

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(new QueryResult<BaseItem>());

        var handler = CreateHandler(_artistIndexMock.Object);
        var request = CreateIntentRequest(musician: "Empty Artist");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }
}
