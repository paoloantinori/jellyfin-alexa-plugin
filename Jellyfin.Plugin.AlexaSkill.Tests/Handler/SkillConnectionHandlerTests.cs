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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class SkillConnectionHandlerTests
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
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
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
