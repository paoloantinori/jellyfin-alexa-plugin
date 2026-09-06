using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Alexa.NET.Assertions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-505: every Movie/Episode/live-TV launch site routes through the shared
/// <c>BaseHandler.BuildVideoAppLaunchResponse</c> chokepoint, so a device without the
/// VideoApp interface (an Echo Dot) gets the localized VideoRequiresScreen Tell instead
/// of a directive the platform rejects with an audible error (device evidence
/// 2026-09-06). The audio-content builders (native controls, audiobooks) degrade to the
/// AudioPlayer response, which a screenless speaker can still play.
/// </summary>
[Collection("Plugin")]
public class VideoAppCapabilityGateTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public VideoAppCapabilityGateTests()
    {
        _fx.UserManager
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
    }

    private static bool HasVideoAppDirective(SkillResponse response)
        => response.Response.Directives?.OfType<VideoAppLaunchDirective>().Any() == true;

    private static IntentRequest CreatePlayRequest(string intentName, string slotName, string slotValue, string locale = "en-US")
        => new()
        {
            Locale = locale,
            Intent = new Intent
            {
                Name = intentName,
                Slots = new Dictionary<string, Slot> { [slotName] = new Slot { Value = slotValue } }
            }
        };

    // ========== Chokepoint: BaseHandler.BuildVideoAppLaunchResponse ==========

    [Fact]
    public void Chokepoint_ScreenlessDevice_NoDirective_VideoRequiresScreenTell()
    {
        var probe = new SharedGateProbeHandler(Mock.Of<ISessionManager>(), _fx.Config, _fx.LoggerFactory);

        SkillResponse response = probe.CallBuildVideoAppLaunchResponse(
            TestHelpers.CreateScreenlessContext(), "en-US", "https://test.example.com/Videos/x/stream?static=true", "The Matrix");

        Assert.False(HasVideoAppDirective(response), "a screenless device must not receive a VideoApp.Launch directive");
        Assert.True(response.Response.ShouldEndSession, "the capability response is a Tell");
        Assert.Contains("requires a device with a screen", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public void Chokepoint_VideoAppDevice_LaunchDirective()
    {
        var probe = new SharedGateProbeHandler(Mock.Of<ISessionManager>(), _fx.Config, _fx.LoggerFactory);

        SkillResponse response = probe.CallBuildVideoAppLaunchResponse(
            TestHelpers.CreateContextWithVideoApp(), "en-US", "https://test.example.com/Videos/x/stream?static=true", "The Matrix");

        Assert.True(HasVideoAppDirective(response));
        Assert.Null(response.Response.ShouldEndSession);
    }

    [Fact]
    public void Chokepoint_MissingCapabilityData_FailsOpen()
    {
        // A context with no SupportedInterfaces map at all keeps the pre-gate launch
        // behavior: absent capability data must not refuse video on a guess.
        var probe = new SharedGateProbeHandler(Mock.Of<ISessionManager>(), _fx.Config, _fx.LoggerFactory);

        SkillResponse response = probe.CallBuildVideoAppLaunchResponse(
            TestHelpers.CreateTestContext(), "en-US", "https://test.example.com/Videos/x/stream?static=true", "The Matrix");

        Assert.True(HasVideoAppDirective(response));
    }

    // ========== Handler launch sites (the existing launch-site test shape) ==========

    [Fact]
    public async Task PlayVideo_ScreenlessDevice_VideoRequiresScreenTell()
    {
        var movie = new Movie { Name = "The Matrix", Id = Guid.NewGuid() };
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = new PlayVideoIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);

        SkillResponse response = await handler.HandleAsync(
            CreatePlayRequest("PlayVideoIntent", "title", "The Matrix"),
            TestHelpers.CreateScreenlessContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.False(HasVideoAppDirective(response), "the Dot must not get a VideoApp.Launch it will reject");
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains("requires a device with a screen", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayVideo_VideoAppDevice_LaunchesDirective()
    {
        var movie = new Movie { Name = "The Matrix", Id = Guid.NewGuid() };
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { movie });

        var handler = new PlayVideoIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);

        SkillResponse response = await handler.HandleAsync(
            CreatePlayRequest("PlayVideoIntent", "title", "The Matrix"),
            TestHelpers.CreateContextWithVideoApp(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        var directive = response.HasDirective<VideoAppLaunchDirective>();
        Assert.NotNull(directive.VideoItem);
        Assert.Null(response.Response.ShouldEndSession);
    }

    [Fact]
    public async Task PlayChannel_ScreenlessDevice_VideoRequiresScreenTell()
    {
        var resolver = new Mock<ILiveTvStreamResolver>();
        resolver
            .Setup(r => r.ResolveAsync(It.IsAny<BaseItem>(), It.IsAny<Entities.User>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveTvStream("https://remote.example/playlist.m3u8"));
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { new Movie { Name = "CNN", Id = Guid.NewGuid() } });

        var handler = new PlayChannelIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, resolver.Object, _fx.LoggerFactory);

        SkillResponse response = await handler.HandleAsync(
            CreatePlayRequest("PlayChannelIntent", "channel", "CNN"),
            TestHelpers.CreateScreenlessContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.False(HasVideoAppDirective(response));
        Assert.True(response.Response.ShouldEndSession);
        Assert.Contains("requires a device with a screen", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    // ========== Audio-content builders degrade to AudioPlayer ==========

    private SharedGateProbeHandler CreateBuilderProbe(PluginConfiguration config, ILoggerFactory loggerFactory)
        => new(Mock.Of<ISessionManager>(), config, loggerFactory);

    [Fact]
    public void BuildVideoAppAudioResponse_ScreenlessDevice_DegradesToAudioPlayer()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        var loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(config, loggerFactory, _ => { }, "jf505-gate-tests");
        var handler = CreateBuilderProbe(config, loggerFactory);
        var user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "tok" };
        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };

        SkillResponse response = handler.BuildVideoAppAudioResponse(song.Id.ToString(), song, user, null, TestHelpers.CreateScreenlessContext());

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains("/Audio/", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAudiobookResumeResponse_ScreenlessDevice_DegradesToAudioPlayerWithOffset()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        var handler = CreateBuilderProbe(config, LoggerFactory.Create(b => { }));
        var user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "tok" };
        var chapter = new Audio { Name = "Chapter 3", Id = Guid.NewGuid() };
        long startTicks = TimeSpan.FromMinutes(12).Ticks;

        SkillResponse response = handler.BuildAudiobookResumeResponse(chapter, startTicks, user, TestHelpers.CreateScreenlessContext());

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Equal(chapter.Id.ToString(), directive.AudioItem.Stream.Token);
        Assert.Equal((int)TimeSpan.FromMinutes(12).TotalMilliseconds, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public void BuildAudiobookResumeResponse_VideoAppDevice_UsesResumePlaylist()
    {
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        var handler = CreateBuilderProbe(config, LoggerFactory.Create(b => { }));
        var user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "tok" };
        var chapter = new Audio { Name = "Chapter 3", Id = Guid.NewGuid() };
        long startTicks = TimeSpan.FromMinutes(12).Ticks;

        SkillResponse response = handler.BuildAudiobookResumeResponse(chapter, startTicks, user, TestHelpers.CreateContextWithVideoApp());

        var directive = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"start={startTicks}", directive.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAudioPlayerResponse_NativeControlsOnScreenless_KeepsAudioPlayer()
    {
        // NativeControlsForAudio would route music through VideoApp; on a screenless
        // device the play must degrade to the AudioPlayer directive (this also proves
        // the fallback pair cannot recurse: both sides gate on the same capability).
        var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        var loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(config, loggerFactory, c => c.NativeControlsForAudio = true, "jf505-gate-tests");
        var handler = CreateBuilderProbe(config, loggerFactory);
        var user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "tok", VideoAppForAudio = null };
        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };
        string rawStream = "http://localhost:8096/Audio/" + song.Id + "/stream?static=true&api_key=tok";

        SkillResponse response = handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, rawStream, song.Id.ToString(), song, user, TestHelpers.CreateScreenlessContext());

        var directive = Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Equal(rawStream, directive.AudioItem.Stream.Url);
    }
}
