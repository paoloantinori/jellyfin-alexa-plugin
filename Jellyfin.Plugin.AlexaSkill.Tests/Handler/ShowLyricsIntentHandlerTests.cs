using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Interface;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Lyrics;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-926 pins for the opt-in lyrics view handler: the three Tell legs (nothing
/// playing, no synced lyrics, screenless via the launch chokepoint) and the
/// happy path (VideoApp.Launch at the lyrics endpoint URL, shouldEndSession
/// null per the standing VideoApp rule, the launch announce naming the song).
/// </summary>
public class ShowLyricsIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock = new();
    private readonly Mock<ILibraryManager> _libraryManagerMock = new();
    private readonly Mock<ILyricManager> _lyricManagerMock = new();
    private readonly PluginConfiguration _config = new()
    {
        ServerAddress = "http://localhost:8096",
        StreamTokenSecret = "test-secret",
    };

    private ShowLyricsIntentHandler CreateHandler() => new(
        _sessionManagerMock.Object,
        _config,
        _libraryManagerMock.Object,
        _lyricManagerMock.Object,
        LoggerFactory.Create(b => { }));

    private static IntentRequest CreateRequest() => new()
    {
        Type = "IntentRequest",
        Intent = new Intent { Name = IntentNames.ShowLyricsIntent },
        Locale = "it-IT",
    };

    private static SessionInfo CreateSession(Mock<ISessionManager> sessionManager) =>
        TestHelpers.CreateTestSession(sessionManager.Object, LoggerFactory.Create(b => { }));

    [Fact]
    public void CanHandle_MatchesOnlyShowLyrics()
    {
        var handler = CreateHandler();
        Assert.True(handler.CanHandle(CreateRequest()));
        Assert.False(handler.CanHandle(new IntentRequest { Intent = new Intent { Name = "AMAZON.PauseIntent" } }));
    }

    [Fact]
    public async Task Handle_NothingPlaying_NoMediaPlayingTell()
    {
        var handler = CreateHandler();
        var session = CreateSession(_sessionManagerMock);
        var context = TestHelpers.CreateTestContext(); // no AudioPlayer token, empty session

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains(ResponseStrings.Get("NoMediaPlaying", "it-IT"), speech, StringComparison.Ordinal);
        Assert.True(response.Response!.ShouldEndSession == true);
    }

    [Fact]
    public async Task Handle_UnsyncedLyrics_LyricsNoSyncedTell()
    {
        var song = new Audio { Id = Guid.NewGuid(), Name = "Goshen" };
        _libraryManagerMock.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _lyricManagerMock
            .Setup(m => m.GetLyricsAsync(song, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricDto
            {
                Lyrics = new List<LyricLine> { new("plain one"), new("plain two") },
            });

        var handler = CreateHandler();
        var session = CreateSession(_sessionManagerMock);
        session.FullNowPlayingItem = song;
        var context = TestHelpers.CreateContextWithToken(song.Id.ToString(), "test-device", "PLAYING");

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.Contains(ResponseStrings.Get("LyricsNoSynced", "it-IT"), TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handle_SyncedLyrics_LaunchesVideoAppAtLyricsUrl()
    {
        var song = new Audio { Id = Guid.NewGuid(), Name = "Goshen" };
        _libraryManagerMock.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _lyricManagerMock
            .Setup(m => m.GetLyricsAsync(song, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricDto
            {
                Lyrics = new List<LyricLine> { new("first", 0), new("second", 80_000_000) },
            });

        var handler = CreateHandler();
        var session = CreateSession(_sessionManagerMock);
        session.FullNowPlayingItem = song;
        var context = TestHelpers.CreateContextWithToken(song.Id.ToString(), "test-device", "PLAYING");

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        var launch = Assert.IsType<global::Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoAppLaunchDirective>(
            Assert.Single(response.Response!.Directives!));
        Assert.Contains(
            $"/alexaskill/api/video-audio/lyrics/{song.Id}/stream.m3u8?token=",
            launch.VideoItem.Source,
            StringComparison.Ordinal);
        // The standing VideoApp rule: shouldEndSession ABSENT (null), never set.
        // The announce rides the progressive vehicle (the async variant moves the
        // OutputSpeech out of the launch response), so the title lives on the
        // directive metadata.
        Assert.Null(response.Response.ShouldEndSession);
        Assert.Equal(song.Name, launch.VideoItem.Metadata?.Title);
    }

    [Fact]
    public async Task Handle_ScreenlessDevice_VideoRequiresScreenTell()
    {
        var song = new Audio { Id = Guid.NewGuid(), Name = "Goshen" };
        _libraryManagerMock.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _lyricManagerMock
            .Setup(m => m.GetLyricsAsync(song, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LyricDto
            {
                Lyrics = new List<LyricLine> { new("first", 0), new("second", 80_000_000) },
            });

        var handler = CreateHandler();
        var session = CreateSession(_sessionManagerMock);
        session.FullNowPlayingItem = song;
        // The screenless shape: no VideoApp in supportedInterfaces.
        var context = new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "screenless-dev",
                    SupportedInterfaces = new Dictionary<string, object>(),
                },
                User = new global::Alexa.NET.Request.User(),
            },
            AudioPlayer = new PlaybackState { Token = song.Id.ToString(), PlayerActivity = "PLAYING" },
        };

        SkillResponse response = await handler.HandleAsync(
            CreateRequest(), context, TestHelpers.CreateTestUser(), session, CancellationToken.None);

        Assert.True(response.Response!.ShouldEndSession == true);
        Assert.DoesNotContain("VideoAppLaunchDirective", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.Contains(ResponseStrings.Get("VideoRequiresScreen", "it-IT"), TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }
}
