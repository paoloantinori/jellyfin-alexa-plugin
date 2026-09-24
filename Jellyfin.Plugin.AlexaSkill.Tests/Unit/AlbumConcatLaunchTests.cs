using System;
using System.Linq;
using global::Alexa.NET;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using MediaBrowser.Controller.Entities;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Handler;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-625 queue-as-concat: an album play in seek mode (NativeControlsForAudio) launches the
/// WHOLE album as one continuous video-audio concat stream keyed by the album GUID (the
/// audiobook chapter shape with tracks), so the Echo Show seek bar spans the full album and
/// the album-level resume offset rides the playlist ?start=. Without a collection parent the
/// launch stays the single-item URL (a PlaySong must never pull the whole album in).
/// </summary>
[Collection("Plugin")]
public class AlbumConcatLaunchTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096");

    public AlbumConcatLaunchTests()
    {
        _fx.Config.NativeControlsForAudio = true;
    }

    private SkillResponse Launch(BaseItem item, Entities.User user, Guid? collectionParentId, long startTicks)
        => new VideoAppTestHandler(Mock.Of<ISessionManager>(), _fx.Config, _fx.LoggerFactory)
            .Launch.BuildAudioPlayerResponse(
                PlayBehavior.ReplaceAll,
                "https://test.example.com/Audio/x/stream?static=true",
                item.Id.ToString(),
                item,
                user,
                TestHelpers.CreateContextWithVideoApp(),
                collectionParentId: collectionParentId,
                collectionStartTicks: startTicks);

    [Fact]
    public void AlbumParentInSeekMode_LaunchesTheConcatStream()
    {
        var album = Guid.NewGuid();
        var song = new Audio { Name = "Magnolia", Id = Guid.NewGuid() };
        var user = TestHelpers.CreateTestUser();

        var response = Launch(song, user, album, 0);

        var launch = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"/alexaskill/api/video-audio/audiobook/{album}/stream.m3u8", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain($"/video-audio/{song.Id}/", launch.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AlbumResumeOffset_RidesThePlaylistStart()
    {
        var album = Guid.NewGuid();
        var song = new Audio { Name = "Magnolia", Id = Guid.NewGuid() };
        var user = TestHelpers.CreateTestUser();

        long ticks = TimeSpan.FromSeconds(257770).Ticks;
        var response = Launch(song, user, album, ticks);

        var launch = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"start={ticks}", launch.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void NoCollectionParent_KeepsTheSingleItemUrl()
    {
        var song = new Audio { Name = "Magnolia", Id = Guid.NewGuid() };
        var user = TestHelpers.CreateTestUser();

        var response = Launch(song, user, null, 0);

        var launch = Assert.Single(response.Response.Directives.OfType<VideoAppLaunchDirective>());
        Assert.Contains($"/alexaskill/api/video-audio/{song.Id}/stream.m3u8", launch.VideoItem.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("/audiobook/", launch.VideoItem.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SeekModeOff_CollectionParentIsIgnored_AudioPlayerPlay()
    {
        _fx.Config.NativeControlsForAudio = false;
        var album = Guid.NewGuid();
        var song = new Audio { Name = "Magnolia", Id = Guid.NewGuid() };
        var user = TestHelpers.CreateTestUser();

        var response = Launch(song, user, album, 0);

        Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());
        Assert.Empty(response.Response.Directives.OfType<VideoAppLaunchDirective>());
    }

    [Fact]
    public void ConcatFfmpegArgs_ArtInputCarriesTheDimensionSafeFilter_BlackFrameDoesNot()
    {
        var withArt = Jellyfin.Plugin.AlexaSkill.Controller.VideoAudioController.BuildHlsAudiobookFfmpegArguments(
            "/tmp/chapters.txt", "https://test.example.com/art.jpg", useBlackFrame: false,
            "/tmp/out/stream.m3u8", "/tmp/out/seg_%04d.ts", "/seg/");
        Assert.Contains(withArt, a => a == "-vf");
        Assert.Contains(withArt, a => a.Contains("force_original_aspect_ratio=decrease", StringComparison.Ordinal));

        var black = Jellyfin.Plugin.AlexaSkill.Controller.VideoAudioController.BuildHlsAudiobookFfmpegArguments(
            "/tmp/chapters.txt", null, useBlackFrame: true,
            "/tmp/out/stream.m3u8", "/tmp/out/seg_%04d.ts", "/seg/");
        Assert.DoesNotContain(black, a => a == "-vf");
    }
}
