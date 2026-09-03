using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
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

[Collection("Plugin")]
public class RadioModeTests : PluginTestBase, IDisposable
{
    private static readonly string DeviceId = "test-device";
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;

    public RadioModeTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();

        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
    }

    public void Dispose()
    {
        QueueContinuationStore.Remove(Guid.Empty, DeviceId);
        RadioModeState.Disable(Guid.Empty, DeviceId);
        GC.SuppressFinalize(this);
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

    private static IntentRequest CreatePlayRadioRequest(string? stationValue = null, bool dialogInProgress = false)
    {
        var request = new IntentRequest { Intent = new Intent { Name = "PlayRadioIntent" } };
        if (stationValue != null)
        {
            request.Intent.Slots = new Dictionary<string, Slot>
            {
                ["station"] = new Slot { Name = "station", Value = stationValue }
            };
        }

        if (dialogInProgress)
        {
            request.DialogState = "IN_PROGRESS";
        }

        return request;
    }

    /// <summary>
    /// JF-472: bare forms ("suona jazz") stolen by Amazon's NLU arrive as PlayRadioIntent
    /// with an empty station slot while nothing plays. The handler must elicit the
    /// station (session open) instead of answering the out-of-context nothing-playing Tell.
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_NothingPlaying_ElicitsStation()
    {
        var handler = new PlayRadioIntentHandler(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession == true, "the station elicit must keep the session open");
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("station", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nothing", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Anti-pattern #7: whitespace-only slot values count as empty.</summary>
    [Fact]
    public async Task PlayRadio_WhitespaceStationSlot_NothingPlaying_ElicitsStation()
    {
        var handler = new PlayRadioIntentHandler(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("  "),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
    }

    /// <summary>
    /// The elicit is conditional on nothing playing: with a current track the context
    /// seeds the radio, so every slot-less sample ("riproduci radio") must keep starting
    /// radio mode directly (JF-472 acceptance: something playing = unchanged behavior).
    /// </summary>
    [Fact]
    public async Task PlayRadio_EmptyStationSlot_SomethingPlaying_StartsRadioMode()
    {
        var handler = new PlayRadioIntentHandler(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        var context = CreateContext();

        var currentId = Guid.NewGuid();
        var currentAudio = new Audio { Id = currentId, Name = "Rock Song" };
        currentAudio.Genres = new[] { "Rock" };
        session.FullNowPlayingItem = currentAudio;

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
            .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { new Audio { Id = Guid.NewGuid(), Name = "Similar Rock Song" } });
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest(),
            context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(RadioModeState.IsEnabled(session.UserId, context.System.Device.DeviceID), "radio mode must still start from the current track");
        Assert.Contains(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "AudioPlayer.Play");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
    }

    /// <summary>
    /// A filled station slot is not yet actionable (no station playback feature), so the
    /// nothing-playing Tell stays (today's behavior for slot-given requests).
    /// </summary>
    [Fact]
    public async Task PlayRadio_StationGiven_NothingPlaying_KeepsNothingPlayingTell()
    {
        var handler = new PlayRadioIntentHandler(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("jazz"),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("nothing", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true, "the Tell must end the session");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
    }

    /// <summary>
    /// Escape hatch from the elicitation trap (JF-423 pattern): while the station elicit
    /// is open (dialog IN_PROGRESS), a bare cancel word is captured into the slot instead
    /// of routing to AMAZON.Stop/CancelIntent; it must end the flow, not be answered as
    /// a station.
    /// </summary>
    [Fact]
    public async Task PlayRadio_CapturedCancelWordDuringOpenElicit_EndsFlow()
    {
        var handler = new PlayRadioIntentHandler(_sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);
        var session = CreateSession();
        session.FullNowPlayingItem = null;

        var response = await handler.HandleAsync(
            CreatePlayRadioRequest("stop", dialogInProgress: true),
            CreateContext(), TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("stopped", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Response.ShouldEndSession == true, "the cancel escape must end the session");
        Assert.DoesNotContain(response.Response.Directives ?? Enumerable.Empty<IDirective>(), d => d.Type == "Dialog.ElicitSlot");
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
