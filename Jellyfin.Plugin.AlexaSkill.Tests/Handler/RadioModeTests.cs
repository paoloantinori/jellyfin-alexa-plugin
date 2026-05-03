using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class RadioModeTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public RadioModeTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private static AudioPlayerRequest CreateNearlyFinishedRequest(string token)
    {
        return new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackNearlyFinished",
            Token = token,
            OffsetInMilliseconds = 0
        };
    }

    [Fact]
    public void PlayRadio_CanHandle_ReturnsTrue()
    {
        var handler = new PlayRadioIntentHandler(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void TurnRadioOn_CanHandle_ReturnsTrue()
    {
        var handler = new TurnRadioOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "TurnRadioOnIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void TurnRadioOff_CanHandle_ReturnsTrue()
    {
        var handler = new TurnRadioOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var request = new IntentRequest { Intent = new Intent { Name = "TurnRadioOffIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public async Task PlayRadio_NothingPlaying_ReturnsNothingPlayingMessage()
    {
        var handler = new PlayRadioIntentHandler(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } },
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnRadioOn_EnablesRadioMode()
    {
        var handler = new TurnRadioOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOnIntent" } },
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID));
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("radio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TurnRadioOff_DisablesRadioMode()
    {
        var handler = new TurnRadioOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);
        Assert.True(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID));

        var response = await handler.HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "TurnRadioOffIntent" } },
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID));
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("radio", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlaybackNearlyFinished_WithRadioMode_AutoQueuesSimilar()
    {
        var handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };

        session.FullNowPlayingItem = currentAudio;
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };

        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);

        var similarId = Guid.NewGuid();
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { new Audio { Id = similarId, Name = "Similar Rock Song" } });

        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(session.NowPlayingQueue.Count > 1);
    }

    [Fact]
    public async Task PlaybackNearlyFinished_WithoutRadioMode_ReturnsEmpty()
    {
        var handler = new PlaybackNearlyFinishedEventHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        var currentId = Guid.NewGuid();
        session.FullNowPlayingItem = new Audio { Id = currentId, Name = "Song" };
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = currentId } };

        RadioModeState.Disable(session.UserId, context.System.Device.DeviceID);

        var response = await handler.HandleAsync(
            CreateNearlyFinishedRequest(currentId.ToString()), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Null(response.Response.OutputSpeech);
    }
}
