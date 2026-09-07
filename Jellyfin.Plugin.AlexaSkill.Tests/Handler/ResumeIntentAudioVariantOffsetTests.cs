using System;
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
/// JF-507 critical-review fix + the JF-520 tail adoption. All three fallback offsets
/// are DEVICE-DERIVED (the AudioPlayer context offset directly;
/// PlayState.PositionTicks and DeviceQueue.CurrentPositionTicks via the writers at
/// PlaybackStoppedEventHandler/PlaybackStartedEventHandler, which persist the device
/// offset without adding the transcode base), so for a transcode-routed item they are
/// all relative to the previous playback's OUTPUT timeline, which starts at that
/// stream's seek point. Corrected contract per fallback (JF-520, via the shared
/// BaseHandler.ResolveResumedAudioLaunch):
/// - Fallback 1 (AudioPlayer context offset) -> ?start = recorded base + offset
///   (item-absolute), directive offset 0; NO recorded base -> restart at 0.
/// - Fallback 2 (session PlayState.PositionTicks) -> same rebase/drop rule.
/// - Fallback 3 (DeviceQueue.CurrentPositionTicks) -> same rebase/drop rule.
/// Raw-static launches (audio items, Echo-decodable video) keep the caller's offset
/// unchanged on every fallback. The rebase mints the new base into the ledger, so
/// the next resume cycle composes from it.
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
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
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
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
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
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
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
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "FreeCommerce",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "aac"));
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
        var movie = new TestHelpers.TestMovieWithStreams(
            "Test Movie",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
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

    // ========== JF-520: the tail adopts the offer path's base+offset rebase ==========

    /// <summary>
    /// JF-520 spec case, tail side: a recorded base B (20:00) plus a device-derived
    /// context offset O (5:00) mints ?start=B+O (25:00, item-absolute) on the transcode
    /// URL, directive offset 0, and the resolve records the NEW base (25:00) in the
    /// ledger so the next resume cycle composes from it (pins the helper's
    /// read-before-resolve ordering: the minted start and the post-mint ledger base
    /// agree, i.e. the resolve did not read back its own write).
    /// </summary>
    [Fact]
    public async Task Resume_Eac3Episode_ViaAudioPlayerContext_RebasesAgainstRecordedBase()
    {
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(episode);

        // The previous playback launched at absolute 20:00 via the transcode; the
        // device has counted 5:00 of stream time since.
        _queueManager.RecordAudioTranscodeBase("test-device", id.ToString(), TimeSpan.FromMinutes(20).Ticks / TimeSpan.TicksPerMillisecond);
        var context = CreateContext(id.ToString(), 300000);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        long expectedMs = (long)TimeSpan.FromMinutes(25).TotalMilliseconds;
        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMilliseconds(expectedMs).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(expectedMs, _queueManager.GetAudioTranscodeBase("test-device", id.ToString()));
    }

    /// <summary>
    /// JF-520, DeviceQueue-sourced item_id path (fallback 3): the queue's item pointer
    /// feeds the resume, and the rebase composes with it the same way (base+offset
    /// minted, ledger advanced), proving the tail's adoption is not context-offset-only.
    /// </summary>
    [Fact]
    public async Task Resume_Eac3Episode_ViaDeviceQueue_RebasesAgainstRecordedBase()
    {
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(episode);

        var queue = _queueManager.GetOrCreateQueue("test-device");
        queue.CurrentItemId = id.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromMinutes(5).Ticks;
        _queueManager.RecordAudioTranscodeBase("test-device", id.ToString(), TimeSpan.FromMinutes(20).Ticks / TimeSpan.TicksPerMillisecond);

        var context = CreateContext(id.ToString(), 0);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        long expectedMs = (long)TimeSpan.FromMinutes(25).TotalMilliseconds;
        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMilliseconds(expectedMs).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(expectedMs, _queueManager.GetAudioTranscodeBase("test-device", id.ToString()));
    }
}
