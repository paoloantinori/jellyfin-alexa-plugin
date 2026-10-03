using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class SkillConnectionHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SkillConnectionHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SkillConnectionHandler CreateHandler()
    {
        return new SkillConnectionHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static LaunchRequest CreateTaskLaunchRequest(string taskName, string? taskVersion = "1")
    {
        return new LaunchRequest
        {
            RequestId = "test-req",
            Locale = "en-US",
            Task = new LaunchRequestTask
            {
                Name = taskName,
                Version = taskVersion
            }
        };
    }

    private static LaunchRequest CreatePlainLaunchRequest()
    {
        return new LaunchRequest
        {
            RequestId = "test-req",
            Locale = "en-US"
        };
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
            .Returns(TestHelpers.CreateJellyfinUser());
    }

    [Fact]
    public void CanHandle_LaunchRequestWithTask_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateTaskLaunchRequest("PlayFavorites");

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_LaunchRequestWithoutTask_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = CreatePlainLaunchRequest();

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_IntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        // SessionResumedRequest is a non-LaunchRequest type
        var request = new SessionResumedRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_PlayFavoritesTask_PlaysFavorites()
    {
        var handler = CreateHandler();
        var request = CreateTaskLaunchRequest("PlayFavorites");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Favorite Song", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);

        // JF-718: the delivered launch writes the now-playing state (the gate must
        // not swallow the real-play arm).
        Assert.Same(audio, session.FullNowPlayingItem);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(audio.Id, session.NowPlayingQueue[0].Id);
    }

    // JF-718 (the addendum's sixth site): the favorites task's queue write used to
    // precede BOTH the MediaNotFound early Tell and the refusal-throwing launch
    // build, so a refused ask left a phantom queue a bare "open the skill" would
    // resume from. This pin covers the directive-less leg; the ordering fix covers
    // the throw (no throw leg is reachable without a token-gated source here).

    [Fact]
    public async Task HandleAsync_PlayFavoritesTask_FirstItemMissing_MediaNotFoundTellLeavesNoPhantomState()
    {
        var handler = CreateHandler();
        var request = CreateTaskLaunchRequest("PlayFavorites");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Favorite Song", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });
        // The favorites list is non-empty, but the head item cannot be re-fetched.
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((BaseItem?)null);

        // A PRIOR launch's now-playing state must SURVIVE the refused ask (the fix
        // withholds the new write, it never clears the old state).
        var priorItem = new Audio { Name = "Prior", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = priorItem;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = priorItem.Id } };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        // The speech text pins WHICH directive-less leg ran: the generic task-error
        // catch (MediaSearchError) would leave the same session shape and green-light
        // this pin without the ordering fix ever running.
        Assert.Contains("could not find the media", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.True(response.Response?.Directives is null or { Count: 0 }, "the MediaNotFound Tell must carry no launch directive");

        // No phantom write, and the pre-existing state is left untouched.
        Assert.Same(priorItem, session.FullNowPlayingItem);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(priorItem.Id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task HandleAsync_PlayFavoritesTask_NoFavorites_ReturnsTell()
    {
        var handler = CreateHandler();
        var request = CreateTaskLaunchRequest("PlayFavorites");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_PlayMediaTask_ReturnsAskResponse()
    {
        var handler = CreateHandler();
        var request = CreateTaskLaunchRequest("PlayMedia");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_SearchLibraryTask_ReturnsAskResponse()
    {
        var handler = CreateHandler();
        var request = CreateTaskLaunchRequest("SearchLibrary");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_UnknownTask_ReturnsErrorResponse()
    {
        var handler = CreateHandler();
        var request = CreateTaskLaunchRequest("UnknownTask");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task HandleAsync_PrefixedTaskName_StripsSkillId()
    {
        var handler = CreateHandler();
        // Simulate a task name prefixed with skill ID
        var request = CreateTaskLaunchRequest("amzn1.ask.skill.abc123.PlayFavorites");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SetupUserMock();

        var audio = new Audio { Name = "Favorite Song", Id = Guid.NewGuid() };
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { audio });
        _libraryManagerMock.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns(audio);

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response?.Directives);
    }
}
