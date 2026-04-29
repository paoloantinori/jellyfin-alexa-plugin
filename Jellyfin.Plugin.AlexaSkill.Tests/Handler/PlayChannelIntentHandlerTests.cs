using System;
using System.Collections.Generic;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class PlayChannelIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public PlayChannelIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private PlayChannelIntentHandler CreateHandler()
    {
        return new PlayChannelIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);
    }

    private static IntentRequest CreatePlayChannelRequest(string? channel = "CNN")
    {
        var slots = new Dictionary<string, Slot>();
        if (channel != null)
        {
            slots["channel"] = new Slot { Value = channel };
        }

        return new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayChannelIntent",
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

    private static Mock<BaseItem> CreateMockChannel(string name, Guid? id = null)
    {
        var item = new Mock<BaseItem>();
        item.Setup(i => i.Name).Returns(name);
        item.Setup(i => i.Id).Returns(id ?? Guid.NewGuid());
        return item;
    }

    [Theory]
    [InlineData("PlayChannelIntent", true)]
    [InlineData("PlaySongIntent", false)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        Assert.False(handler.CanHandle(new LaunchRequest()));
    }

    [Fact]
    public void Handle_NoChannelSlot_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayChannelIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        var response = handler.Handle(request, CreateContext(), TestHelpers.CreateTestUser(), new SessionInfo());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public void Handle_NullChannelValue_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayChannelRequest(null),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);

        Assert.Contains("didn't catch", speech.Text);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public void Handle_WhitespaceChannel_ReturnsPrompt(string channel)
    {
        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayChannelRequest(channel),
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
            CreatePlayChannelRequest("Unknown Channel"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public void Handle_FoundChannel_ReturnsAudioPlayerDirective()
    {
        var channelId = Guid.NewGuid();
        var channel = CreateMockChannel("CNN", channelId);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { channel.Object });

        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayChannelRequest("CNN"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        Assert.Null(response.Response.OutputSpeech);
        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(response.Response.Directives[0]);
        Assert.Contains(channelId.ToString(), directive.AudioItem.Stream.Url);
        Assert.Contains("Download", directive.AudioItem.Stream.Url);
    }

    [Fact]
    public void Handle_FoundChannel_SetsSessionQueue()
    {
        var channelId = Guid.NewGuid();
        var channel = CreateMockChannel("CNN", channelId);
        var session = new SessionInfo();

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { channel.Object });

        var handler = CreateHandler();
        handler.Handle(CreatePlayChannelRequest("CNN"), CreateContext(), TestHelpers.CreateTestUser(), session);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(channelId, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public void Handle_FoundMultipleChannels_ReturnsFirstMatch()
    {
        var ch1 = CreateMockChannel("CNN");
        var ch2 = CreateMockChannel("CNN International");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { ch1.Object, ch2.Object });

        var handler = CreateHandler();
        var response = handler.Handle(
            CreatePlayChannelRequest("CNN"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            new SessionInfo());

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
    }
}
