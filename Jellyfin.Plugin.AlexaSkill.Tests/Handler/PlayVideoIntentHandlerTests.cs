using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Alexa.NET.Assertions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class PlayVideoIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayVideoIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userManagerMock
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayVideoIntentHandler CreateHandler()
    {
        return new PlayVideoIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static IntentRequest CreatePlayVideoRequest(string? title = "The Matrix")
    {
        var slots = new Dictionary<string, Slot>();
        if (title != null)
        {
            slots["title"] = new Slot { Value = title };
        }

        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayVideoIntent",
                Slots = slots
            }
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static BaseItem CreateTestItem(string name, Guid? id = null)
    {
        var item = new Movie { Name = name, Id = id ?? Guid.NewGuid() };
        return item;
    }

    [Theory]
    [InlineData("PlayVideoIntent", true)]
    [InlineData("PlaySongIntent", false)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    [Fact]
    public async Task Handle_NoTitleSlot_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayVideoIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_EmptyTitle_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(""),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_NoResults_ReturnsNotFound()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Unknown Movie"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task Handle_FoundMovie_ReturnsVideoAppDirective()
    {
        var movie = CreateTestItem("The Matrix");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        response.HasDirective<VideoAppLaunchDirective>();
    }

    [Fact]
    public async Task Handle_FoundMultipleResults_ReturnsDisambiguationPrompt()
    {
        var movie1 = CreateTestItem("Inception");
        var movie2 = CreateTestItem("Interstellar");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie1, movie2 });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Nolan"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
        Assert.False(response.Response.ShouldEndSession);

        string speechText = response.Response.OutputSpeech is SsmlOutputSpeech ssml
            ? ssml.Ssml
            : Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech).Text;
        Assert.Contains("Inception", speechText);
    }

    [Fact]
    public async Task Handle_NullTitleSlotValue_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(null),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public async Task Handle_WhitespaceTitle_ReturnsPrompt(string title)
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest(title),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_FoundMovie_DirectiveContainsSourceAndMetadata()
    {
        var id = Guid.NewGuid();
        var movie = CreateTestItem("The Matrix", id);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("The Matrix"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.NotNull(directive.VideoItem);
        Assert.Contains(id.ToString(), directive.VideoItem.Source);
        Assert.Contains("Videos", directive.VideoItem.Source);
        Assert.NotNull(directive.VideoItem.Metadata);
        Assert.Equal("The Matrix", directive.VideoItem.Metadata.Title);
    }

    [Fact]
    public async Task Handle_FoundMovie_SetsSessionQueue()
    {
        var id = Guid.NewGuid();
        var movie = CreateTestItem("The Matrix", id);
        var session = CreateSession();

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        await handler.HandleAsync(CreatePlayVideoRequest("The Matrix"), CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(id, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest();

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task Handle_VideoResponse_EndsSession()
    {
        var movie = CreateTestItem("Test Video");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayVideoRequest("Test Video"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
    }
}
