using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class SleepTimerIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SleepTimerIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SleepTimerIntentHandler CreateHandler()
    {
        return new SleepTimerIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? durationMinutes = null)
    {
        var intent = new Intent { Name = IntentNames.SleepTimer };
        intent.Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>();

        if (durationMinutes != null)
        {
            intent.Slots["duration_minutes"] = new global::Alexa.NET.Request.Slot { Name = "duration_minutes", Value = durationMinutes };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
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

    [Fact]
    public void CanHandle_SleepTimerIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "30");

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
    public async Task HandleAsync_MissingDuration_ReturnsPrompt()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_ReturnsNoMediaPlaying()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "30");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_SetsSleepTimer_ReturnsTimerConfirmation()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "30");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_ZeroDuration_CancelsTimer()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "0");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(2).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_ZeroDuration_MidSleepCompositeToken_ReplaysWithCleanIdAndNoDeadline()
    {
        // JF-447 review finding (cancel branch): during sleep playback the AudioPlayer
        // token carries the sleep suffix. The cancel replay must be built from the CLEAN
        // id: a composite id in the stream URL path is unmatchable, and a composite
        // replay Token would carry the old deadline so the sleep would still fire after
        // the cancel.
        Guid songId = Guid.NewGuid();
        var handler = CreateHandler();
        var request = CreateIntentRequest(durationMinutes: "0");
        var context = CreateContext();
        context.AudioPlayer = new PlaybackState
        {
            Token = $"{songId}|sleep:{DateTimeOffset.UtcNow.AddMinutes(30).UtcTicks}",
            OffsetInMilliseconds = 90_000
        };
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Test Song", Id = songId };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(1).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        var directive = Assert.Single(response.Response!.Directives.OfType<global::Alexa.NET.Response.Directive.AudioPlayerPlayDirective>());
        Assert.Equal(songId.ToString(), directive.AudioItem.Stream.Token);
        Assert.DoesNotContain("|sleep:", directive.AudioItem.Stream.Token, StringComparison.Ordinal);
        Assert.Contains(songId.ToString(), directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("|sleep:", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(90_000, directive.AudioItem.Stream.OffsetInMilliseconds);
    }
}
