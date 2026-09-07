using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-507 critical-review fix, final form: NO tail offset may be minted into the
/// audio-only transcode's <c>?start=</c> for a transcode-routed item. All three
/// fallback offsets are DEVICE-DERIVED (the AudioPlayer context offset directly;
/// PlayState.PositionTicks and DeviceQueue.CurrentPositionTicks via the writers at
/// PlaybackStoppedEventHandler/PlaybackStartedEventHandler, which persist the device
/// offset without adding the transcode base), so for a transcode-routed item they are
/// all relative to the previous playback's OUTPUT timeline, which starts at that
/// stream's seek point. Corrected contract per fallback:
/// - Fallback 1 (AudioPlayer context offset) -> NO ?start= (restart), directive offset 0.
/// - Fallback 2 (session PlayState.PositionTicks) -> NO ?start= (restart), directive offset 0.
/// - Fallback 3 (DeviceQueue.CurrentPositionTicks) -> NO ?start= (restart), directive offset 0.
/// Raw-static launches (audio items, Echo-decodable video) keep the caller's offset
/// unchanged on every fallback. The stream-relative-to-absolute correction that would
/// let a transcode-routed resume restore its position is tracked in JF-514.
/// </summary>
[Collection("Plugin")]
public class ResumeIntentAudioVariantOffsetTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();
    private readonly DeviceQueueManager _queueManager;
    private readonly string _tempDir;

    public ResumeIntentAudioVariantOffsetTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "resume-audio-variant-tests-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(_tempDir);
        _queueManager = new DeviceQueueManager(_tempDir, _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());

        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "resume-audio-variant-tests");
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        try
        {
            if (System.IO.Directory.Exists(_tempDir))
            {
                System.IO.Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Test seam for <c>BaseItem.GetMediaStreams()</c> (virtual): overriding the streams
    /// lets the tests exercise the REAL codec probe + routing decision (under the test
    /// host a plain item's probe degrades to unknown codec and keeps the static URL).
    /// </summary>
    private sealed class EpisodeWithStreams : MediaBrowser.Controller.Entities.TV.Episode
    {
        private readonly List<MediaStream> _streams;

        public EpisodeWithStreams(string name, Guid id, params MediaStream[] streams)
        {
            Name = name;
            Id = id;
            _streams = streams.ToList();
        }

        public override IReadOnlyList<MediaStream> GetMediaStreams() => _streams;
    }

    private static MediaStream Stream(MediaStreamType type, string codec) => new() { Type = type, Codec = codec };

    private SessionInfo CreateSessionWithNowPlaying(BaseItem item)
    {
        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();
        session.FullNowPlayingItem = item;
        return session;
    }

    private static Context CreateContext(string token, long offsetMs)
    {
        var context = TestHelpers.CreateTestContext();
        context.AudioPlayer = new PlaybackState
        {
            Token = token,
            OffsetInMilliseconds = offsetMs,
            PlayerActivity = "IDLE"
        };
        return context;
    }

    private ResumeIntentHandler CreateHandler() => new(
        _fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory,
        _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object,
        _queueManager);

    private static AudioPlayerPlayDirective SinglePlayDirective(SkillResponse response)
        => Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());

    [Fact]
    public async Task Resume_Eac3Episode_ViaAudioPlayerContext_DropsStreamRelativeOffset()
    {
        var id = Guid.NewGuid();
        var episode = new EpisodeWithStreams(
            "Ribs",
            id,
            Stream(MediaStreamType.Video, "h264"),
            Stream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(episode);

        // Fallback 1: the device reports a stream-relative offset (previous playback
        // started at absolute 20:00 via the transcode; the device counts from 0).
        var context = CreateContext(id.ToString(), 300000);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/audio.m3u8?token=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task Resume_Eac3Episode_ViaSessionPlayState_DropsDeviceDerivedTicks()
    {
        var id = Guid.NewGuid();
        var episode = new EpisodeWithStreams(
            "Ribs",
            id,
            Stream(MediaStreamType.Video, "h264"),
            Stream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(episode);

        // Fallback 2: the persisted play state carries the DEVICE offset in the real
        // writers' shape (PlaybackStoppedEventHandler), which is output-timeline-
        // relative for a transcode-routed item; the gate must drop it (restart), not
        // mint a false ?start=.
        var context = CreateContext(id.ToString(), 0);
        session.PlayState!.PositionTicks = TimeSpan.FromMinutes(20).Ticks;

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/audio.m3u8?token=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task Resume_Eac3Episode_ViaDeviceQueue_DropsDeviceDerivedTicks()
    {
        var id = Guid.NewGuid();
        var episode = new EpisodeWithStreams(
            "Ribs",
            id,
            Stream(MediaStreamType.Video, "h264"),
            Stream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(episode);

        // Fallback 3: context offset 0 (cleared after pause); the DeviceQueue position
        // is written from the device offset by PlaybackStoppedEventHandler, so it is
        // output-timeline-relative for a transcode-routed item and must be dropped.
        var queue = _queueManager.GetOrCreateQueue("test-device");
        queue.CurrentItemId = id.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromMinutes(20).Ticks;

        var context = CreateContext(id.ToString(), 0);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/audio.m3u8?token=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task Resume_AacEpisode_ViaAudioPlayerContext_KeepsStaticUrlAndOffset()
    {
        var id = Guid.NewGuid();
        var episode = new EpisodeWithStreams(
            "FreeCommerce",
            id,
            Stream(MediaStreamType.Video, "h264"),
            Stream(MediaStreamType.Audio, "aac"));
        var session = CreateSessionWithNowPlaying(episode);

        var context = CreateContext(id.ToString(), 300000);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/Audio/{id}/stream?static=true&api_key=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("video-audio", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(300000, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task Resume_Eac3Movie_ViaAudioPlayerContext_DropsStreamRelativeOffset()
    {
        var id = Guid.NewGuid();
        var movie = new EpisodeWithStreamsTestMovie(
            "Test Movie",
            id,
            Stream(MediaStreamType.Video, "h264"),
            Stream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(movie);

        var context = CreateContext(id.ToString(), 300000);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/audio.m3u8?token=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>Movie twin of the episode stream seam (ResolveAudioLaunchSource matches Movie and Episode).</summary>
    private sealed class EpisodeWithStreamsTestMovie : MediaBrowser.Controller.Entities.Movies.Movie
    {
        private readonly List<MediaStream> _streams;

        public EpisodeWithStreamsTestMovie(string name, Guid id, params MediaStream[] streams)
        {
            Name = name;
            Id = id;
            _streams = streams.ToList();
        }

        public override IReadOnlyList<MediaStream> GetMediaStreams() => _streams;
    }
}
