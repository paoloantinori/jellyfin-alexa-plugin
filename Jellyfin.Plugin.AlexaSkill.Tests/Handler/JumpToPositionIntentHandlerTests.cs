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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class JumpToPositionIntentHandlerTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public JumpToPositionIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.SeekEnabled = _config.SeekEnabled,
            "alexa-jump-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private JumpToPositionIntentHandler CreateHandler()
    {
        return new JumpToPositionIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? hours = null, string? minutes = null, string? seconds = null)
    {
        var intent = new Intent { Name = IntentNames.JumpToPosition };
        intent.Slots = new Dictionary<string, Slot>();

        if (hours != null)
        {
            intent.Slots["position_hours"] = new Slot { Name = "position_hours", Value = hours };
        }

        if (minutes != null)
        {
            intent.Slots["position_minutes"] = new Slot { Name = "position_minutes", Value = minutes };
        }

        if (seconds != null)
        {
            intent.Slots["position_seconds"] = new Slot { Name = "position_seconds", Value = seconds };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();

    [Fact]
    public void CanHandle_JumpToPositionIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(minutes: "5");

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
    public async Task HandleAsync_FeatureDisabled_ReturnsDisabledMessage()
    {
        _config.SeekEnabled = false;
        Plugin.Instance!.Configuration.SeekEnabled = false;

        var handler = CreateHandler();
        var request = CreateIntentRequest(minutes: "5");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_ReturnsNoMediaPlaying()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(minutes: "5");
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), session, CancellationToken.None);

        Assert.Contains("playing", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_JumpToMinutes_SeeksCorrectly()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(minutes: "30");
        var session = CreateSession();
        var user = CreateUser();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("30", text);
    }

    [Fact]
    public async Task HandleAsync_JumpToHoursAndMinutes_SeeksCorrectly()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(hours: "2", minutes: "30");
        var session = CreateSession();
        var user = CreateUser();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("2", text);
        Assert.Contains("30", text);
    }

    [Fact]
    public async Task HandleAsync_PastEnd_ReturnsPositionPastEnd()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(hours: "5");
        var session = CreateSession();
        var user = CreateUser();

        var audioItem = new Audio { Name = "Short Clip", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(10).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromMinutes(10).Ticks };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, session, CancellationToken.None);

        Assert.NotNull(response);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("past the end", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NoSlots_ClampsToZero()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest();
        var session = CreateSession();
        var user = CreateUser();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_JumpToZero_StartsFromBeginning()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        // All slots resolve to 0
        var request = CreateIntentRequest(hours: "0", minutes: "0", seconds: "0");
        var session = CreateSession();
        var user = CreateUser();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }
}
