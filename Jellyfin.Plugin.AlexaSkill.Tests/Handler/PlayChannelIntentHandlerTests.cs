using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
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
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
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

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

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

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static BaseItem CreateTestChannel(string name, Guid? id = null)
    {
        return new Movie { Name = name, Id = id ?? Guid.NewGuid() };
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
    public async Task Handle_NoChannelSlot_ReturnsPrompt()
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

        var response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("didn't catch", speech.Text);
    }

    [Fact]
    public async Task Handle_NullChannelValue_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayChannelRequest(null),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("didn't catch", speech.Text);
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("\t")]
    public async Task Handle_WhitespaceChannel_ReturnsPrompt(string channel)
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayChannelRequest(channel),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

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
            CreatePlayChannelRequest("Unknown Channel"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task Handle_FoundChannel_ReturnsAudioPlayerDirective()
    {
        var channelId = Guid.NewGuid();
        var channel = CreateTestChannel("CNN", channelId);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { channel });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayChannelRequest("CNN"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
        var directive = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Contains(channelId.ToString(), directive.AudioItem.Stream.Url);
        Assert.Contains("Audio", directive.AudioItem.Stream.Url);
    }

    [Fact]
    public async Task Handle_FoundChannel_SetsSessionQueue()
    {
        var channelId = Guid.NewGuid();
        var channel = CreateTestChannel("CNN", channelId);
        var session = CreateSession();

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { channel });

        var handler = CreateHandler();
        await handler.HandleAsync(CreatePlayChannelRequest("CNN"), CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);
        Assert.Equal(channelId, session.NowPlayingQueue[0].Id);
    }

    [Fact]
    public async Task Handle_FoundMultipleChannels_ReturnsFirstMatch()
    {
        var ch1 = CreateTestChannel("CNN");
        var ch2 = CreateTestChannel("CNN International");

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { ch1, ch2 });

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreatePlayChannelRequest("CNN"),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(), CancellationToken.None);

        response.HasDirective<AudioPlayerPlayDirective>();
    }
}
