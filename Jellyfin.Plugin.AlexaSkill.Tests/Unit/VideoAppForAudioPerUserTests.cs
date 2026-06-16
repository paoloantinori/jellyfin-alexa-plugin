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
/// Tests the per-user VideoAppForAudio override on top of the global NativeControlsForAudio
/// default. Music (Audio items) may be routed to VideoApp (seek bar) or plain AudioPlayer
/// (raw stream) per-user; null inherits the global default. Audiobook routing is governed by
/// NativeControlsForBooks and is not affected by VideoAppForAudio.
/// </summary>
[Collection("Plugin")]
public class VideoAppForAudioPerUserTests : PluginTestBase
{
    private readonly PluginConfiguration _config;
    private readonly TestHandler _handler;
    private readonly Entities.User _user;

    public VideoAppForAudioPerUserTests()
    {
        _config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
        var loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(_config, loggerFactory, _ => { }, "videoapp-for-audio-per-user");
        _handler = new TestHandler(Mock.Of<ISessionManager>(), _config, loggerFactory);
        _user = new Entities.User { Id = Guid.NewGuid(), JellyfinToken = "tok" };
    }

    private static IDirective? FirstDirective(SkillResponse r) =>
        r.Response?.Directives?.Count > 0 ? r.Response.Directives[0] : null;

    private static Audio NewSong() => new() { Name = "Song", Id = Guid.NewGuid() };

    [Fact]
    public void UserOverrideFalse_RoutesToAudioPlayer_EvenWhenGlobalIsTrue()
    {
        // Global default would route to VideoApp, but the per-user false override wins.
        _config.NativeControlsForAudio = true;
        _user.VideoAppForAudio = false;

        var response = _handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, "http://x/audio", NewSong().Id.ToString(), NewSong(), _user);

        Assert.IsType<AudioPlayerPlayDirective>(FirstDirective(response));
    }

    [Fact]
    public void UserOverrideFalse_UsesRawStreamUrl_NotVideoAudioEndpoint()
    {
        _config.NativeControlsForAudio = true;
        _user.VideoAppForAudio = false;

        var song = NewSong();
        var rawStream = "http://localhost:8096/Audio/" + song.Id + "/stream?static=true&api_key=tok";
        var response = _handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, rawStream, song.Id.ToString(), song, _user);

        var directive = Assert.IsType<AudioPlayerPlayDirective>(FirstDirective(response));
        // Raw stream URL passed straight through — no VideoAudioController/ffmpeg involvement.
        Assert.Equal(rawStream, directive.AudioItem.Stream.Url);
    }

    [Fact]
    public void UserOverrideTrue_RoutesToVideoApp_EvenWhenGlobalIsFalse()
    {
        // Global default would stay on AudioPlayer, but the per-user true override wins.
        _config.NativeControlsForAudio = false;
        _user.VideoAppForAudio = true;

        var response = _handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, "http://x/audio", NewSong().Id.ToString(), NewSong(), _user);

        Assert.IsType<PluginVideoApp>(FirstDirective(response));
    }

    [Fact]
    public void UserOverrideNull_InheritsGlobalDefault_WhenGlobalTrue()
    {
        _config.NativeControlsForAudio = true;
        _user.VideoAppForAudio = null;

        var response = _handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, "http://x/audio", NewSong().Id.ToString(), NewSong(), _user);

        Assert.IsType<PluginVideoApp>(FirstDirective(response));
    }

    [Fact]
    public void UserOverrideNull_InheritsGlobalDefault_WhenGlobalFalse()
    {
        _config.NativeControlsForAudio = false;
        _user.VideoAppForAudio = null;

        var response = _handler.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, "http://x/audio", NewSong().Id.ToString(), NewSong(), _user);

        Assert.IsType<AudioPlayerPlayDirective>(FirstDirective(response));
    }

    [Fact]
    public void GetVideoAppForAudio_ReturnsUserValue_WhenExplicitlySet_True()
    {
        _config.NativeControlsForAudio = false;
        _user.VideoAppForAudio = true;

        Assert.True(_handler.GetVideoAppForAudioAccessible(_user));
    }

    [Fact]
    public void GetVideoAppForAudio_ReturnsUserValue_WhenExplicitlySet_False()
    {
        _config.NativeControlsForAudio = true;
        _user.VideoAppForAudio = false;

        Assert.False(_handler.GetVideoAppForAudioAccessible(_user));
    }

    [Fact]
    public void GetVideoAppForAudio_InheritsGlobal_WhenUserValueNull()
    {
        _config.NativeControlsForAudio = true;
        _user.VideoAppForAudio = null;

        Assert.True(_handler.GetVideoAppForAudioAccessible(_user));

        _config.NativeControlsForAudio = false;
        Assert.False(_handler.GetVideoAppForAudioAccessible(_user));
    }

    /// <summary>
    /// Minimal BaseHandler subclass exposing the inherited BuildAudioPlayerResponse and the
    /// protected GetVideoAppForAudio resolver for direct testing.
    /// </summary>
    private sealed class TestHandler : BaseHandler
    {
        public TestHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public bool GetVideoAppForAudioAccessible(Entities.User? user) => GetVideoAppForAudio(user);

        public override bool CanHandle(Request request) => false;
        public override Task<SkillResponse> HandleAsync(
            Request request, Context context, Entities.User user,
            SessionInfo session, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
