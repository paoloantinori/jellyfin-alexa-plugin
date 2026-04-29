using System;
using System.Collections.Generic;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

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
        _config = new PluginConfiguration();
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

    private static Context CreateContext()
    {
        return new Context
        {
            System = new Alexa.NET.Request.System
            {
                User = new Alexa.NET.Request.User
                {
                    AccessToken = Guid.NewGuid().ToString()
                },
                Device = new Device { DeviceID = "test-device" }
            }
        };
    }

    private static Mock<BaseItem> CreateMockBaseItem(string name, Guid? id = null)
    {
        var item = new Mock<BaseItem>();
        item.Setup(i => i.Name).Returns(name);
        item.Setup(i => i.Id).Returns(id ?? Guid.NewGuid());
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
    public void Handle_NoTitleSlot_ReturnsPrompt()
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

        var response = handler.Handle(request, CreateContext(), TestHelpers.CreateTestUser(), new SessionInfo());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public void Handle_EmptyTitle_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest(""),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public void Handle_NoResults_ReturnsNotFound()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest("Unknown Movie"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public void Handle_FoundMovie_ReturnsVideoAppDirective()
    {
        var movie = CreateMockBaseItem("The Matrix");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie.Object });

        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest("The Matrix"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        Assert.Null(response.Response.OutputSpeech);
        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
        Assert.Equal("VideoApp.Launch", response.Response.Directives[0].Type);
    }

    [Fact]
    public void Handle_FoundMultipleResults_ReturnsFirstMatch()
    {
        var movie1 = CreateMockBaseItem("The Matrix");
        var movie2 = CreateMockBaseItem("The Matrix Reloaded");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie1.Object, movie2.Object });

        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest("The Matrix"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
    }

    [Fact]
    public void Handle_NullTitleSlotValue_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest(null),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public void Handle_WhitespaceTitle_ReturnsPrompt(string title)
    {
        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest(title),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public void Handle_FoundMovie_DirectiveContainsSourceAndMetadata()
    {
        var id = Guid.NewGuid();
        var movie = CreateMockBaseItem("The Matrix", id);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie.Object });

        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest("The Matrix"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        var directive = Assert.IsType<VideoAppLaunchDirective>(response.Response.Directives[0]);
        Assert.NotNull(directive.VideoItem);
        Assert.Contains(id.ToString(), directive.VideoItem.Source);
        Assert.Contains("Download", directive.VideoItem.Source);
        Assert.NotNull(directive.VideoItem.Metadata);
        Assert.Equal("The Matrix", directive.VideoItem.Metadata.Title);
    }

    [Fact]
    public void Handle_FoundMovie_SetsSessionQueue()
    {
        var id = Guid.NewGuid();
        var movie = CreateMockBaseItem("The Matrix", id);
        var session = new SessionInfo();

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie.Object });

        var handler = CreateHandler();
        handler.Handle(CreatePlayVideoRequest("The Matrix"), CreateContext(), TestHelpers.CreateTestUser(), session);

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
    public void Handle_VideoResponse_EndsSession()
    {
        var movie = CreateMockBaseItem("Test Video");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie.Object });

        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayVideoRequest("Test Video"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        Assert.True(response.Response.ShouldEndSession);
    }
}
