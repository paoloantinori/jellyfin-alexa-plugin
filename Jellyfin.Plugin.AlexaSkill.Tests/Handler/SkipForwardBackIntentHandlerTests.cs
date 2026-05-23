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
public class SkipForwardBackIntentHandlerTests : IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public SkipForwardBackIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });

        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.SeekEnabled = _config.SeekEnabled,
            "alexa-seek-feature-test");
    }

    public void Dispose() => _loggerFactory.Dispose();

    private SkipForwardBackIntentHandler CreateHandler()
    {
        return new SkipForwardBackIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
    }

    private static IntentRequest CreateIntentRequest(string? direction = null, string? amount = null, string? unit = null)
    {
        var intent = new Intent { Name = IntentNames.SkipForwardBack };
        intent.Slots = new Dictionary<string, Slot>();

        if (direction != null)
        {
            intent.Slots["seek_direction"] = new Slot { Name = "seek_direction", Value = direction };
        }

        if (amount != null)
        {
            intent.Slots["seek_amount"] = new Slot { Name = "seek_amount", Value = amount };
        }

        if (unit != null)
        {
            intent.Slots["seek_unit"] = new Slot { Name = "seek_unit", Value = unit };
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
    public void CanHandle_SkipForwardBackIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "forward");

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
        var request = CreateIntentRequest(direction: "forward", amount: "30", unit: "seconds");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("disabled", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NothingPlaying_ReturnsNoMediaPlaying()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "forward", amount: "30", unit: "seconds");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("playing", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SkipForward30Seconds_SeeksCorrectly()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "forward", amount: "30", unit: "seconds");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(5).Ticks };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Contains("forward", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SkipBack30Seconds_SeeksCorrectly()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "back", amount: "30", unit: "seconds");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(5).Ticks };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Contains("back", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SkipBackPastBeginning_ClampsToZero()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "back", amount: "60", unit: "seconds");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromSeconds(10).Ticks };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task HandleAsync_AtBeginningSkipBack_ReturnsAtBeginning()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "back", amount: "30", unit: "seconds");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = 0 };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Contains("beginning", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NoSlots_DefaultsToForward30Seconds()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        // No slots at all — should default to forward 30 seconds
        var request = CreateIntentRequest();
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(1).Ticks };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
        // Should have skipped forward (default direction)
        Assert.Contains("forward", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_SkipForwardMinutes_ConvertsCorrectly()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "forward", amount: "2", unit: "minutes");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromHours(8).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(10).Ticks };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(8).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
        var text = TestHelpers.GetSpeechText(response);
        Assert.Contains("forward", text, StringComparison.OrdinalIgnoreCase);
        // Should show "12 minutes" as the new position
        Assert.Contains("12", text);
    }

    [Fact]
    public async Task HandleAsync_SkipForwardPastEnd_ClampsToEnd()
    {
        _config.SeekEnabled = true;
        Plugin.Instance!.Configuration.SeekEnabled = true;

        var handler = CreateHandler();
        var request = CreateIntentRequest(direction: "forward", amount: "60", unit: "minutes");
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        var audioItem = new Audio { Name = "Audiobook", Id = Guid.NewGuid(), RunTimeTicks = TimeSpan.FromMinutes(30).Ticks };
        session.FullNowPlayingItem = audioItem;
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(25).Ticks };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromMinutes(30).Ticks };

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Response.Directives);
    }
}
