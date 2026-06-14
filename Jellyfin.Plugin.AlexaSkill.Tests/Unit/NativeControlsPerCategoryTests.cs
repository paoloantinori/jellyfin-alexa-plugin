using System;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;
using PluginVideoApp = Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests that BuildAudioPlayerResponse routes to VideoApp vs AudioPlayer based on the
/// per-category flags: NativeControlsForAudio governs music, NativeControlsForBooks
/// governs audiobooks — each independent of the other.
/// </summary>
[Collection("Plugin")]
public class NativeControlsPerCategoryTests : PluginTestBase
{
    private readonly PluginConfiguration _config;
    private readonly TestHandler _handler;
    private readonly Entities.User _user;

    public NativeControlsPerCategoryTests()
    {
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        var loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(_config, loggerFactory, _ => { }, "native-controls-per-category");
        _handler = new TestHandler(Mock.Of<ISessionManager>(), _config, loggerFactory);
        _user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "tok" };
    }

    private static IDirective? FirstDirective(SkillResponse r) =>
        r.Response?.Directives?.Count > 0 ? r.Response.Directives[0] : null;

    [Fact]
    public void MusicItem_RoutesToVideoApp_WhenAudioFlagOn()
    {
        _config.NativeControlsForAudio = true;
        _config.NativeControlsForBooks = false;

        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };
        var response = _handler.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, "http://x/audio", song.Id.ToString(), song, _user);

        Assert.IsType<PluginVideoApp>(FirstDirective(response));
    }

    [Fact]
    public void MusicItem_StaysAudioPlayer_WhenOnlyBooksFlagOn()
    {
        // The books flag must NOT pull music into VideoApp.
        _config.NativeControlsForAudio = false;
        _config.NativeControlsForBooks = true;

        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };
        var response = _handler.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, "http://x/audio", song.Id.ToString(), song, _user);

        Assert.IsType<AudioPlayerPlayDirective>(FirstDirective(response));
    }

    [Fact]
    public void MusicItem_StaysAudioPlayer_WhenBothFlagsOff()
    {
        _config.NativeControlsForAudio = false;
        _config.NativeControlsForBooks = false;

        var song = new Audio { Name = "Song", Id = Guid.NewGuid() };
        var response = _handler.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, "http://x/audio", song.Id.ToString(), song, _user);

        Assert.IsType<AudioPlayerPlayDirective>(FirstDirective(response));
    }

    /// <summary>
    /// Minimal BaseHandler subclass exposing the inherited BuildAudioPlayerResponse.
    /// </summary>
    private sealed class TestHandler : BaseHandler
    {
        public TestHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => false;
        public override Task<SkillResponse> HandleAsync(
            Request request, Context context, Entities.User user,
            SessionInfo session, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
