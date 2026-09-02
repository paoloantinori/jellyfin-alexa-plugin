using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-450: de-DE, fr-FR, fr-CA and it-IT declare the custom loop vocabulary
/// (LoopAllOnIntent / LoopAllOffIntent / RepeatSingleOnIntent) instead of the
/// AMAZON.LoopOn/LoopOff built-ins used by the other 13 locales. These tests pin
/// that both names route to the same handlers and set the matching repeat mode.
/// </summary>
[Collection("Plugin")]
public class LoopIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public LoopIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _sessionManagerMock
            .Setup(s => s.OnPlaybackProgress(It.IsAny<PlaybackProgressInfo>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private static IntentRequest IntentRequestFor(string intentName) =>
        new() { Intent = new Intent { Name = intentName }, Locale = "en-US", RequestId = "test-req" };

    private static Context ContextWithPlayingToken()
    {
        Context context = TestHelpers.CreateTestContext();
        context.AudioPlayer = new PlaybackState { Token = Guid.NewGuid().ToString(), OffsetInMilliseconds = 42_000 };
        return context;
    }

    private SessionInfo CreateSession()
    {
        SessionInfo session = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        session.PlayState = new PlayerStateInfo();
        return session;
    }

    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    // The owning handler for each custom locale intent name (the built-in twin of the
    // same row is claimed by the same instance).
    private BaseHandler CreateOwner(string customIntentName) => customIntentName switch
    {
        IntentNames.LoopAllOn => new LoopOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory),
        IntentNames.LoopAllOff => new LoopOffIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory),
        _ => new LoopSongOnIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory),
    };

    public static TheoryData<string, string> HandledIntentNames => new()
    {
        { "AMAZON.LoopOnIntent", IntentNames.LoopAllOn },
        { "AMAZON.LoopOffIntent", IntentNames.LoopAllOff },
        { IntentNames.LoopSongOn, IntentNames.RepeatSingleOn },
    };

    [Theory]
    [MemberData(nameof(HandledIntentNames))]
    public void CanHandle_AcceptsBothBuiltInAndCustomLocaleName(string builtInName, string customName)
    {
        BaseHandler handler = CreateOwner(customName);

        Assert.True(handler.CanHandle(IntentRequestFor(builtInName)), builtInName);
        Assert.True(handler.CanHandle(IntentRequestFor(customName)), customName);
        Assert.False(handler.CanHandle(IntentRequestFor("PlaySongIntent")));
    }

    public static TheoryData<string, RepeatMode> IntentToRepeatMode => new()
    {
        { IntentNames.LoopAllOn, RepeatMode.RepeatAll },
        { IntentNames.LoopAllOff, RepeatMode.RepeatNone },
        { IntentNames.RepeatSingleOn, RepeatMode.RepeatOne },
    };

    [Theory]
    [MemberData(nameof(IntentToRepeatMode))]
    public async Task HandleAsync_SetsMatchingRepeatMode(string intentName, RepeatMode expectedMode)
    {
        BaseHandler handler = CreateOwner(intentName);
        Context context = ContextWithPlayingToken();
        SessionInfo session = CreateSession();

        SkillResponse response = await handler.HandleAsync(IntentRequestFor(intentName), context, CreateUser(), session, CancellationToken.None);

        Assert.NotNull(response);

        PlaybackProgressInfo? info = _sessionManagerMock
            .Invocations
            .Select(i => i.Arguments.OfType<PlaybackProgressInfo>().FirstOrDefault())
            .FirstOrDefault(i => i != null);
        Assert.NotNull(info);
        Assert.Equal(expectedMode, info!.RepeatMode);
        Assert.Equal(context.AudioPlayer.Token, info.ItemId.ToString());
        Assert.Equal(TimeSpan.FromMilliseconds(context.AudioPlayer.OffsetInMilliseconds).Ticks, info.PositionTicks);
    }

    [Theory]
    [InlineData(IntentNames.LoopAllOn)]
    [InlineData(IntentNames.LoopAllOff)]
    [InlineData(IntentNames.RepeatSingleOn)]
    public async Task HandleAsync_NoAudioContext_ReturnsNoMediaPlayingInsteadOfThrowing(string intentName)
    {
        // Review finding (JF-450): "attiva ripetizione" from an open session with
        // nothing playing must answer with the no-media tell, not a Guid crash.
        BaseHandler handler = CreateOwner(intentName);
        Context context = TestHelpers.CreateTestContext(); // No AudioPlayer state.

        SkillResponse response = await handler.HandleAsync(
            IntentRequestFor(intentName), context, CreateUser(), CreateSession(), CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NoMediaPlaying", "en-US"), speech, StringComparison.OrdinalIgnoreCase);
    }
}
