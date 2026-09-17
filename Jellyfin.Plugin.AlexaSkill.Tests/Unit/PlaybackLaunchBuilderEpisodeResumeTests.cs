using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-565: the episode resume-slice producer. GetVideoAppLaunchUrl threads a caller
/// position into the remux URL as ?start= (the JF-499 endpoint slices every serve
/// path EXTINF-accurately; VideoApp.Launch has no offset parameter, so the slice IS
/// the episode resume mechanism). Scope pins: EPISODE-only (a Movie ignores the
/// position), Static-route-only items ignore it too (the static stream has no seek
/// mechanism), and a position at or beyond the item runtime degrades to a fresh
/// start (the JF-521 clamp's episode-slice mirror).
/// </summary>
[Collection("Plugin")]
public class PlaybackLaunchBuilderEpisodeResumeTests : PluginTestBase
{
    private readonly PluginConfiguration _config = new();
    private readonly PlaybackLaunchBuilder _builder;

    public PlaybackLaunchBuilderEpisodeResumeTests()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _builder = TestHelpers.CreateLaunchBuilder(_config);
    }

    private static TestHelpers.TestEpisodeWithStreams RemuxEpisode(Guid id)
    {
        // JF-565: the runtime clamp is fail-closed (an unknown runtime cannot prove
        // a mid-episode position), so the resumable-shape fixture needs one.
        return new TestHelpers.TestEpisodeWithStreams(
            "The Convention",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"))
        {
            RunTimeTicks = TimeSpan.FromMinutes(60).Ticks
        };
    }

    private static TestHelpers.TestEpisodeWithStreams StaticEpisode(Guid id)
        => new(
            "The Convention",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "aac"));

    private static TestHelpers.TestMovieWithStreams RemuxMovie(Guid id)
        => new(
            "The Matrix",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));

    [Fact]
    public void LaunchUrl_RemuxEpisode_WithStart_MintsStartOnEpisodeHlsUrl()
    {
        var id = Guid.NewGuid();
        long resumeTicks = TimeSpan.FromMinutes(10).Ticks;

        string url = _builder.GetVideoAppLaunchUrl(RemuxEpisode(id), TestHelpers.CreateTestUser(), resumeTicks);

        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/stream.m3u8?start={resumeTicks}&token=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchUrl_RemuxEpisode_WithoutStart_KeepsTokenOnlyUrl()
    {
        var id = Guid.NewGuid();

        string url = _builder.GetVideoAppLaunchUrl(RemuxEpisode(id), TestHelpers.CreateTestUser());

        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/stream.m3u8?token=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("start=", url, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Static route has no seek mechanism (the platform limit that makes the
    /// slice necessary), so a position passed for a Static-routed episode is
    /// dropped, not silently encoded into a URL that cannot honor it.
    /// </summary>
    [Fact]
    public void LaunchUrl_StaticRouteEpisode_IgnoresStart()
    {
        var id = Guid.NewGuid();

        string url = _builder.GetVideoAppLaunchUrl(StaticEpisode(id), TestHelpers.CreateTestUser(), TimeSpan.FromMinutes(10).Ticks);

        Assert.Contains($"/Videos/{id}/stream?static=true&api_key=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("start=", url, StringComparison.Ordinal);
    }

    /// <summary>
    /// JF-565 scope pin: the slice is EPISODE-only. A remux-routed Movie keeps the
    /// unsliced URL even when a caller passes a position (movie resume stays the
    /// announced-position-only shape).
    /// </summary>
    [Fact]
    public void LaunchUrl_RemuxMovie_IgnoresStart_EpisodeOnlyScope()
    {
        var id = Guid.NewGuid();

        string url = _builder.GetVideoAppLaunchUrl(RemuxMovie(id), TestHelpers.CreateTestUser(), TimeSpan.FromMinutes(10).Ticks);

        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/stream.m3u8?token=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("start=", url, StringComparison.Ordinal);
    }

    /// <summary>
    /// The JF-521 clamp's episode-slice mirror: a stored position at or beyond the
    /// runtime cannot be a legitimate mid-episode resume and would slice to a
    /// zero-length playlist, so the launch degrades to a fresh start.
    /// </summary>
    [Fact]
    public void LaunchUrl_RemuxEpisode_StartAtOrBeyondRuntime_PlaysFromStart()
    {
        var beyondId = Guid.NewGuid();
        var atId = Guid.NewGuid();
        long runtime = TimeSpan.FromMinutes(30).Ticks;
        var beyond = RemuxEpisode(beyondId);
        beyond.RunTimeTicks = runtime;
        var at = RemuxEpisode(atId);
        at.RunTimeTicks = runtime;

        string beyondUrl = _builder.GetVideoAppLaunchUrl(beyond, TestHelpers.CreateTestUser(), runtime + 1);
        string atUrl = _builder.GetVideoAppLaunchUrl(at, TestHelpers.CreateTestUser(), runtime);

        Assert.DoesNotContain("start=", beyondUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("start=", atUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// A position strictly inside the runtime still slices (the clamp must not eat
    /// legitimate resumes on runtime-carrying items).
    /// </summary>
    [Fact]
    public void LaunchUrl_RemuxEpisode_StartInsideRuntime_MintsStart()
    {
        var id = Guid.NewGuid();
        long runtime = TimeSpan.FromMinutes(30).Ticks;
        long resumeTicks = runtime - TimeSpan.FromSeconds(1).Ticks;
        var episode = RemuxEpisode(id);
        episode.RunTimeTicks = runtime;

        string url = _builder.GetVideoAppLaunchUrl(episode, TestHelpers.CreateTestUser(), resumeTicks);

        Assert.Contains($"?start={resumeTicks}&token=", url, StringComparison.Ordinal);
    }
}
