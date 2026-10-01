using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-687 pins: with an EMPTY <see cref="PluginConfiguration.StreamTokenSecret"/> every
/// URL to the plugin's own token-gated endpoints is dead at birth (the JF-309 route gate
/// 503s each request before reading any token), so the launch builders answer the
/// localized <c>StreamTokenNotConfigured</c> Tell instead of delivering such a URL. One
/// pin family per mint-site family, all at the builder delivery points:
/// - the AudioPlayer chokepoint (JF-636 speed URL + JF-507 episode audio-transcode URL,
///   minted by ResolveAudioLaunchSource from any of its callers),
/// - the VideoApp-audio builder (single-item video-audio URL + the audiobook/album
///   concat URL),
/// - the audiobook resume builder (resume concat URL),
/// - the VideoApp launch chokepoint (the episode/movie remux URL minted by
///   GetVideoAppLaunchUrl and handed in as sourceUrl).
/// The no-overblock pins hold the other half of the contract: static Jellyfin stream
/// URLs and live-TV resolver URLs carry NO plugin token, keep playing with an empty
/// secret, and must never be refused. RED PROOF: disable the shared
/// StreamTokenSecretRefusal guard in PlaybackLaunchBuilder; every ConfigTell pin
/// flips to the dead-URL directive shape while the no-overblock and control pins stay
/// green.
/// </summary>
[Collection("Plugin")]
public class PlaybackLaunchBuilderStreamTokenSecretTests : PluginTestBase
{
    private readonly PluginConfiguration _config = new();
    private readonly PlaybackLaunchBuilder _launch;

    public PlaybackLaunchBuilderStreamTokenSecretTests()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        // The broken-configuration state under test. The plugin ctor auto-generates a
        // secret; an XmlSerializer-deserialized config predating JF-309 (or a wiped
        // element) deserializes with the property default: empty.
        _config.StreamTokenSecret = string.Empty;
        _launch = TestHelpers.CreateLaunchBuilder(_config);
    }

    private static Entities.User CreateUser()
        => TestHelpers.CreateTestUser(jellyfinToken: "tok");

    private static TestHelpers.TestEpisodeWithStreams RemuxEpisode()
        => TestHelpers.RemuxEpisode();

    /// <summary>
    /// The JF-687/JF-693 refusal-Tell oracle lives in
    /// <see cref="TestHelpers.AssertStreamTokenRefusalTell"/> (the production
    /// <see cref="PlaybackLaunchBuilder.HasLaunchDirective"/> predicate, the Tell
    /// shape, and the localized StreamTokenNotConfigured speech); the localized
    /// flavor is the JF-693 gate-marker extension (a) twin.
    /// </summary>
    private static void AssertConfigTell(SkillResponse response)
        => TestHelpers.AssertStreamTokenRefusalTell(response);

    private static void AssertLocalizedConfigTell(SkillResponse response, string locale)
        => TestHelpers.AssertStreamTokenRefusalTell(response, locale);

    // ---- Family: the AudioPlayer.Play chokepoint (speed + episode-transcode mints) ----

    [Fact]
    public void AudioPlayerPlay_SpeedUrl_EmptySecret_ConfigTell_NoDirective()
    {
        var song = TestHelpers.CreateSong();
        var user = CreateUser();

        AudioLaunchSource source = _launch.ResolveAudioLaunchSource(song, song.Id.ToString(), user, 0, ratePerMille: 1500);
        Assert.Contains("/alexaskill/api/", source.Url);

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, source, song.Id.ToString(), song, user, TestHelpers.CreateContextWithVideoApp());

        AssertConfigTell(response);
    }

    [Fact]
    public void AudioPlayerPlay_EpisodeTranscodeUrl_EmptySecret_ConfigTell_NoDirective()
    {
        var episode = RemuxEpisode();
        var user = CreateUser();

        AudioLaunchSource source = _launch.ResolveAudioLaunchSource(episode, episode.Id.ToString(), user, 0);
        Assert.Contains("/alexaskill/api/", source.Url);

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, source, episode.Id.ToString(), episode, user, TestHelpers.CreateContextWithVideoApp());

        AssertConfigTell(response);
    }

    [Fact]
    public void AudioPlayerPlay_StaticUrl_EmptySecret_StillPlays()
    {
        // The no-overblock half: a static Jellyfin stream URL carries the user's
        // api_key, never the plugin token, so it still plays with an empty secret.
        var song = TestHelpers.CreateSong();
        var user = CreateUser();
        string staticUrl = $"https://test.example.com/Audio/{song.Id}/stream?static=true&api_key=tok";

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, staticUrl, song.Id.ToString(), song, user, TestHelpers.CreateContextWithVideoApp());

        var directive = Assert.Single(response.Response.Directives);
        Assert.IsType<AudioPlayerPlayDirective>(directive);
    }

    [Fact]
    public void AudioPlayerPlay_TokenGatedUrl_SecretConfigured_StillPlays()
    {
        // Control: the guard keys on the EMPTY secret; a configured secret keeps the
        // token-gated speed route playable.
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        Assert.False(string.IsNullOrEmpty(config.StreamTokenSecret));
        var launch = TestHelpers.CreateLaunchBuilder(config);
        var song = TestHelpers.CreateSong();
        var user = CreateUser();

        AudioLaunchSource source = launch.ResolveAudioLaunchSource(song, song.Id.ToString(), user, 0, ratePerMille: 1500);
        SkillResponse response = launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, source, song.Id.ToString(), song, user, TestHelpers.CreateContextWithVideoApp());

        Assert.Contains(response.Response.Directives, d => d is AudioPlayerPlayDirective);
    }

    // ---- Family: the VideoApp-audio builder (video-audio + concat mints) ----

    [Fact]
    public void VideoAppAudio_Music_EmptySecret_ConfigTell_NoDirective()
    {
        var song = TestHelpers.CreateSong();
        var user = CreateUser();

        SkillResponse response = _launch.BuildVideoAppAudioResponse(
            song.Id.ToString(), song, user, context: TestHelpers.CreateContextWithVideoApp());

        AssertConfigTell(response);
    }

    [Fact]
    public void VideoAppAudio_AlbumConcat_EmptySecret_ConfigTell_NoDirective()
    {
        var song = TestHelpers.CreateSong();
        var user = CreateUser();

        SkillResponse response = _launch.BuildVideoAppAudioResponse(
            song.Id.ToString(), song, user, context: TestHelpers.CreateContextWithVideoApp(),
            collectionParentId: Guid.NewGuid());

        AssertConfigTell(response);
    }

    // ---- Family: the audiobook resume builder (resume concat mint) ----

    [Fact]
    public void AudiobookResume_EmptySecret_ConfigTell_NoDirective()
    {
        var chapter = new MediaBrowser.Controller.Entities.AudioBook { Name = "Chapter 1", Id = Guid.NewGuid() };
        var user = CreateUser();

        SkillResponse response = _launch.BuildAudiobookResumeResponse(
            chapter, TimeSpan.FromMinutes(5).Ticks, user, TestHelpers.CreateContextWithVideoApp());

        AssertConfigTell(response);
    }

    [Fact]
    public void AudiobookResume_Screenless_EmptySecret_StillDegradesToAudioPlayer()
    {
        // The no-overblock half on the resume builder: the screenless degrade plays the
        // chapter on the STATIC audio URL, which no token gates.
        var chapter = new MediaBrowser.Controller.Entities.AudioBook { Name = "Chapter 1", Id = Guid.NewGuid() };
        var user = CreateUser();

        SkillResponse response = _launch.BuildAudiobookResumeResponse(
            chapter, TimeSpan.FromMinutes(5).Ticks, user, TestHelpers.CreateScreenlessContext());

        var directive = Assert.Single(response.Response.Directives);
        Assert.IsType<AudioPlayerPlayDirective>(directive);
        string? url = Assert.IsType<AudioItemStream>(((AudioPlayerPlayDirective)directive).AudioItem.Stream).Url;
        Assert.DoesNotContain("/alexaskill/api/", url);
    }

    // ---- Family: the VideoApp launch chokepoint (episode remux mint, sourceUrl arg) ----

    [Fact]
    public void VideoAppLaunch_RemuxUrl_EmptySecret_ConfigTell_NoDirective()
    {
        // The URL is minted by the CALLER (GetVideoAppLaunchUrl); the guard sits at the
        // delivery decision, so the mint itself is unchanged and the directive is not
        // emitted.
        var episode = RemuxEpisode();
        var user = CreateUser();
        string sourceUrl = _launch.GetVideoAppLaunchUrl(episode, user);
        Assert.Contains("/alexaskill/api/", sourceUrl);

        SkillResponse response = _launch.BuildVideoAppLaunchResponse(
            TestHelpers.CreateContextWithVideoApp(), "en-US", sourceUrl, episode.Name);

        AssertConfigTell(response);
    }

    [Fact]
    public void VideoAppLaunch_NonTokenUrl_EmptySecret_StillLaunches()
    {
        // The no-overblock half: a live-TV resolver URL (or any static source URL)
        // carries no plugin token and launches normally with an empty secret.
        SkillResponse response = _launch.BuildVideoAppLaunchResponse(
            TestHelpers.CreateContextWithVideoApp(), "en-US",
            "https://test.example.com/LiveTv/channels/abc/stream", "Channel 5");

        var directive = Assert.Single(response.Response.Directives);
        Assert.IsType<VideoAppLaunchDirective>(directive);
    }

    [Fact]
    public async Task VideoAppLaunchAsync_RemuxUrl_EmptySecret_ConfigTell_NoProgressiveSend()
    {
        // The async chokepoint refuses BEFORE the progressive announce, so a broken
        // configuration never speaks a now-playing it then refuses to deliver.
        var sends = new List<string>();
        var launch = new PlaybackLaunchBuilder(
            _config,
            LoggerFactory.Create(b => { }).CreateLogger<PlaybackLaunchBuilder>(),
            (_, _, speech) =>
            {
                sends.Add(speech);
                return Task.FromResult(true);
            });
        var episode = RemuxEpisode();
        var user = CreateUser();
        string sourceUrl = launch.GetVideoAppLaunchUrl(episode, user);
        var request = new IntentRequest { Intent = new Intent { Name = "PlayVideoIntent" } };

        SkillResponse response = await launch.BuildVideoAppLaunchResponseAsync(
            TestHelpers.CreateContextWithVideoApp(), request, "en-US", sourceUrl, episode.Name);

        AssertConfigTell(response);
        Assert.Empty(sends);
    }

    // ---- JF-693 gate-marker extension (a): the localized twins, one per family ----

    [Fact]
    public void AudioPlayerPlay_SpeedUrl_ThreadedLocale_SpeaksLocalizedConfigTell()
    {
        var song = TestHelpers.CreateSong();
        var user = CreateUser();

        AudioLaunchSource source = _launch.ResolveAudioLaunchSource(song, song.Id.ToString(), user, 0, ratePerMille: 1500);

        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, source, song.Id.ToString(), song, user,
            TestHelpers.CreateContextWithVideoApp(), locale: "it-IT");

        AssertLocalizedConfigTell(response, "it-IT");
    }

    [Fact]
    public void VideoAppAudio_AlbumConcat_ThreadedLocale_SpeaksLocalizedConfigTell()
    {
        var song = TestHelpers.CreateSong();
        var user = CreateUser();

        SkillResponse response = _launch.BuildVideoAppAudioResponse(
            song.Id.ToString(), song, user, context: TestHelpers.CreateContextWithVideoApp(),
            collectionParentId: Guid.NewGuid(), locale: "it-IT");

        AssertLocalizedConfigTell(response, "it-IT");
    }

    [Fact]
    public void AudiobookResume_ThreadedLocale_SpeaksLocalizedConfigTell()
    {
        var chapter = new MediaBrowser.Controller.Entities.AudioBook { Name = "Chapter 1", Id = Guid.NewGuid() };
        var user = CreateUser();

        SkillResponse response = _launch.BuildAudiobookResumeResponse(
            chapter, TimeSpan.FromMinutes(5).Ticks, user, TestHelpers.CreateContextWithVideoApp(), locale: "it-IT");

        AssertLocalizedConfigTell(response, "it-IT");
    }

    [Fact]
    public void VideoAppLaunch_RemuxUrl_ThreadedLocale_SpeaksLocalizedConfigTell()
    {
        var episode = RemuxEpisode();
        var user = CreateUser();
        string sourceUrl = _launch.GetVideoAppLaunchUrl(episode, user);

        SkillResponse response = _launch.BuildVideoAppLaunchResponse(
            TestHelpers.CreateContextWithVideoApp(), "it-IT", sourceUrl, episode.Name);

        AssertLocalizedConfigTell(response, "it-IT");
    }
}
