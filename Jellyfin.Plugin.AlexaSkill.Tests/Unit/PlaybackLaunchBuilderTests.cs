using System;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Response;
using global::Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch 4 characterization suite for the AudioPlayer launch family
/// (BuildAudioPlayerResponse + the batch-3-deferred TryAttachNowPlayingDirective
/// and its GetImageUrl dependency). Written BEFORE the extraction and run green
/// on the pre-refactor BaseHandler code via a probe subclass, then retargeted
/// to direct PlaybackLaunchBuilder construction when the members moved (receiver
/// swap only; expectations unchanged). They pin CURRENT behavior: the APL
/// now-playing attach gate (only onto responses that already carry an
/// AudioPlayer.Play directive, only on APL devices with visuals enabled, never
/// onto a null-built document) and the attached art URL resolving through the
/// plugin config server address + user token. BuildAudioPlayerResponse's own
/// byte contracts (stream URL, offset, ShouldEndSession=true, metadata, announce
/// gating, native-controls routing) are already pinned by CoverArtTests /
/// VideoAppForAudioPerUserTests / NativeControlsPerCategoryTests (retargeted to
/// the collaborator in the same batch) and are intentionally not duplicated here.
/// </summary>
[Collection("Plugin")]
public class PlaybackLaunchBuilderTests : PluginTestBase
{
    private readonly PluginConfiguration _config = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });
    private readonly PlaybackLaunchBuilder _launch;

    public PlaybackLaunchBuilderTests()
    {
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        // The delegate stands in for BaseHandler.SendProgressiveResponse (the JF-501
        // virtual seam): a truthful success, matching the audio-launch suite's lack of
        // progressive-path assertions (no builder member this suite exercises sends one).
        _launch = TestHelpers.CreateLaunchBuilder(_config);
    }

    private static Entities.User CreateUser(string token = "test-token")
        => TestHelpers.CreateTestUser(jellyfinToken: token);

    /// <summary>
    /// A play response built for an APL-capable device starts with exactly the
    /// AudioPlayer.Play directive; the attach under test appends to it.
    /// </summary>
    private (SkillResponse Response, string ItemId) CreatePlayResponse(
        Audio song, Entities.User user, Context context)
    {
        string itemId = song.Id.ToString();
        var response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, "http://x/stream", itemId, song, user, context);
        return (response, itemId);
    }

    // ---- TryAttachNowPlayingDirective: gate + attach behavior ----

    [Fact]
    public void NowPlaying_AplDevice_AudioPlayerResponse_AppendsNowPlayingDirective()
    {
        // JF-623: a ReplaceAll music play auto-attaches the NowPlaying screen (the
        // Echo Show's own player persists stale metadata when nothing replaces it).
        var song = TestHelpers.CreateSong();
        var user = CreateUser();
        var context = TestHelpers.CreateContextWithApl();
        var (response, _) = CreatePlayResponse(song, user, context);

        Assert.Equal(2, response.Response.Directives.Count);
        Assert.IsType<AudioPlayerPlayDirective>(response.Response.Directives[0]);
        var apl = Assert.IsType<AplRenderDocumentDirective>(response.Response.Directives[1]);
        Assert.Equal("nowPlaying", apl.Token);
    }

    [Fact]
    public void NowPlaying_AttachedArtUrl_UsesConfigServerAddressAndUserToken()
    {
        // JF-950 item 2: with the stream proxy on (the default), the attached art URL is
        // the image-proxy URL carrying the item-scoped token + u selector, never the
        // Jellyfin session token.
        var song = TestHelpers.CreateSong();
        var user = CreateUser(token: "my-api-key");
        var context = TestHelpers.CreateContextWithApl();
        var (response, itemId) = CreatePlayResponse(song, user, context);

        var apl = Assert.IsType<AplRenderDocumentDirective>(response.Response.Directives[1]);
        Assert.NotNull(apl.DataSources);
        string? artUrl = apl.DataSources["jellyfinData"]?["properties"]?["artUrl"]?.ToString();
        Assert.StartsWith(
            $"http://localhost:8096/alexaskill/api/stream/image/{itemId}?token=",
            artUrl,
            StringComparison.Ordinal);
        Assert.EndsWith($"&u={user.Id}", artUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("my-api-key", artUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void NowPlaying_AttachedArtUrl_ProxyDisabled_LegacyApiKeyUrl()
    {
        // The rollback switch: StreamProxyEnabled=false restores the pre-JF-950 art URL.
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "http://localhost:8096");
        config.StreamProxyEnabled = false;
        var launch = TestHelpers.CreateLaunchBuilder(config);
        var song = TestHelpers.CreateSong();
        var user = CreateUser(token: "my-api-key");
        string itemId = song.Id.ToString();

        var response = launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, "http://x/stream", itemId, song, user, TestHelpers.CreateContextWithApl());

        var apl = Assert.IsType<AplRenderDocumentDirective>(response.Response.Directives[1]);
        string? artUrl = apl.DataSources["jellyfinData"]?["properties"]?["artUrl"]?.ToString();
        Assert.Equal(
            $"http://localhost:8096/Items/{itemId}/Images/Primary?api_key=my-api-key",
            artUrl);
    }

    [Fact]
    public void NowPlaying_NonAplContext_NoAttach()
    {
        var song = TestHelpers.CreateSong();
        var user = CreateUser();
        var context = TestHelpers.CreateContextWithoutApl();
        var (response, _) = CreatePlayResponse(song, user, context);

        var playDirective = Assert.Single(response.Response.Directives);
        Assert.IsType<AudioPlayerPlayDirective>(playDirective);
    }

    [Fact]
    public void NowPlaying_VisualsDisabled_NoAttach()
    {
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration(),
            _loggerFactory,
            _ => { },
            nameof(PlaybackLaunchBuilderTests));
        bool priorVisualsEnabled = Plugin.Instance!.Configuration.AplVisualsEnabled;
        Plugin.Instance.Configuration.AplVisualsEnabled = false;

        try
        {
            var song = TestHelpers.CreateSong();
            var user = CreateUser();
            var context = TestHelpers.CreateContextWithApl();
            var (response, _) = CreatePlayResponse(song, user, context);

            Assert.Single(response.Response.Directives);
        }
        finally
        {
            // Restore the prior value so static state does not leak to other test classes
            Plugin.Instance!.Configuration.AplVisualsEnabled = priorVisualsEnabled;
        }
    }

    [Fact]
    public void NowPlaying_ResponseWithoutPlayDirective_NoAttach()
    {
        // The VideoApp/stop path: a response whose only directive is NOT an
        // AudioPlayer.Play must not gain an APL now-playing screen.
        var song = TestHelpers.CreateSong();
        var user = CreateUser();
        var context = TestHelpers.CreateContextWithApl();
        var response = ResponseBuilder.AudioPlayerStop();

        _launch.TryAttachNowPlayingDirective(response, song, song.Id.ToString(), user, context);

        var directive = Assert.Single(response.Response.Directives);
        Assert.IsType<StopDirective>(directive);
    }

    [Fact]
    public void NowPlaying_EmptyNameItem_NullBuild_NoAttach()
    {
        // AplHelper.BuildNowPlayingDirective returns null for an item with an
        // empty name: the attacher must skip the directive (and log the debug
        // line) without touching anything else.
        var song = TestHelpers.CreateSong(name: string.Empty);
        var user = CreateUser();
        var context = TestHelpers.CreateContextWithApl();
        var (response, _) = CreatePlayResponse(song, user, context);

        Assert.Single(response.Response.Directives);
    }


    [Fact]
    public void NowPlaying_Enhanced_ProgressValues_ReachTheDatasource()
    {
        // JF-624 round 3: 0/0 defaults made the determinate AlexaProgressBar render
        // the sweeping activity animation; the play's offset and the item runtime
        // must reach the datasource.
        var song = TestHelpers.CreateSong();
        song.RunTimeTicks = TimeSpan.FromMinutes(4).Ticks;
        var user = CreateUser();
        var context = TestHelpers.CreateContextWithApl();

        var response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, "http://x/stream", song.Id.ToString(), song, user, context, offsetInMilliseconds: 30_000);

        var apl = Assert.IsType<Jellyfin.Plugin.AlexaSkill.Alexa.Directive.AplRenderDocumentDirective>(
            response.Response.Directives[1]);
        string? progress = apl.DataSources?["jellyfinData"]?["properties"]?["progressValue"]?.ToString();
        string? total = apl.DataSources?["jellyfinData"]?["properties"]?["totalValue"]?.ToString();
        Assert.Equal("30000", progress);
        Assert.Equal(TimeSpan.FromMinutes(4).Ticks.ToString(), total == null ? null : (long.Parse(total) * TimeSpan.TicksPerMillisecond).ToString());
    }

    [Fact]
    public void NowPlaying_EnqueuePlay_DoesNotAutoAttach()
    {
        // JF-623: only ReplaceAll re-renders the screen; an Enqueue plays over the
        // track that is still running and must not replace its display.
        var song = TestHelpers.CreateSong();
        var user = CreateUser();
        var context = TestHelpers.CreateContextWithApl();

        var response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.Enqueue, "http://x/stream", song.Id.ToString(), song, user, context);

        var playDirective = Assert.Single(response.Response.Directives);
        Assert.IsType<AudioPlayerPlayDirective>(playDirective);
    }

    // ---- JF-950 item 2: the stream/image proxy URL flag ----

    /// <summary>Extracts one raw query parameter value from a URL (no encoding).</summary>
    private static string QueryParam(string url, string name)
    {
        string query = url[(url.IndexOf('?') + 1)..];
        foreach (string part in query.Split('&'))
        {
            if (part.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return part[(name.Length + 1)..];
            }
        }

        Assert.Fail($"no query parameter {name} in {url}");
        return string.Empty;
    }

    [Fact]
    public void StreamUrl_ProxyEnabled_EmitsProxyUrlWithTokenAndU()
    {
        // The default (flag on): the audio stream URL is the signed-token proxy URL;
        // the long-lived Jellyfin session token never appears in it.
        var song = TestHelpers.CreateSong();
        var user = CreateUser(token: "session-token");
        string itemId = song.Id.ToString();
        string url = _launch.GetStreamUrl(itemId, user);

        Assert.StartsWith(
            $"http://localhost:8096/alexaskill/api/stream/{itemId}?token=",
            url,
            StringComparison.Ordinal);
        Assert.Equal(user.Id.ToString(), QueryParam(url, "u"));
        Assert.DoesNotContain("api_key=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("session-token", url, StringComparison.Ordinal);
        Assert.True(StreamTokenHelper.TryValidate(QueryParam(url, "token"), itemId, _config.StreamTokenSecret));
    }

    [Fact]
    public void VideoStreamUrl_ProxyEnabled_EmitsTheSameProxyUrlAsAudio()
    {
        // The endpoint derives Audio-vs-Videos from the item's media kind, so both
        // builders emit the SAME proxy URL for the same item; the path segment is a
        // legacy-only concern (the single-BuildStreamUrl contract).
        var user = CreateUser();
        string itemId = Guid.NewGuid().ToString();

        Assert.Equal(_launch.GetStreamUrl(itemId, user), _launch.GetVideoStreamUrl(itemId, user));
    }

    [Fact]
    public void StreamUrl_ProxyDisabled_IsByteIdenticalLegacyUrl()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "http://localhost:8096");
        config.StreamProxyEnabled = false;
        var launch = TestHelpers.CreateLaunchBuilder(config);
        var user = CreateUser(token: "test-token");
        string itemId = Guid.NewGuid().ToString();

        Assert.Equal(
            $"http://localhost:8096/Audio/{itemId}/stream?static=true&api_key=test-token",
            launch.GetStreamUrl(itemId, user));
    }

    [Fact]
    public void VideoStreamUrl_ProxyDisabled_IsByteIdenticalLegacyUrl()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "http://localhost:8096");
        config.StreamProxyEnabled = false;
        var launch = TestHelpers.CreateLaunchBuilder(config);
        var user = CreateUser(token: "test-token");
        string itemId = Guid.NewGuid().ToString();

        Assert.Equal(
            $"http://localhost:8096/Videos/{itemId}/stream?static=true&api_key=test-token",
            launch.GetVideoStreamUrl(itemId, user));
    }

    [Fact]
    public void ImageUrl_ProxyEnabled_EmitsImageProxyUrlWithTokenAndU()
    {
        var song = TestHelpers.CreateSong();
        var user = CreateUser(token: "session-token");
        string itemId = song.Id.ToString();
        string url = _launch.GetImageUrl(itemId, user);

        Assert.StartsWith(
            $"http://localhost:8096/alexaskill/api/stream/image/{itemId}?token=",
            url,
            StringComparison.Ordinal);
        Assert.Equal(user.Id.ToString(), QueryParam(url, "u"));
        Assert.DoesNotContain("api_key=", url, StringComparison.Ordinal);
        Assert.True(StreamTokenHelper.TryValidate(QueryParam(url, "token"), itemId, _config.StreamTokenSecret));
    }

    [Fact]
    public void ImageUrl_ProxyDisabled_IsByteIdenticalLegacyUrl()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "http://localhost:8096");
        config.StreamProxyEnabled = false;
        var launch = TestHelpers.CreateLaunchBuilder(config);
        var user = CreateUser(token: "test-token");
        string itemId = Guid.NewGuid().ToString();

        Assert.Equal(
            $"http://localhost:8096/Items/{itemId}/Images/Primary?api_key=test-token",
            launch.GetImageUrl(itemId, user));
    }
}
