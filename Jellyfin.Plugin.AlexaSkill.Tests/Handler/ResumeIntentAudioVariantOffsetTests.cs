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
/// JF-507 critical-review fix + the JF-520 tail adoption, re-scoped by JF-522.
/// Only fallback 1 (the AudioPlayer context offset, Amazon-written) is
/// stream-relative by platform contract: it goes through the shared
/// PlaybackLaunchBuilder.ResolveResumedAudioLaunch rebase (launch-scoped base + offset,
/// drop to a 0-restart when no scope is recorded).
/// Fallbacks 2-3 (session PlayState.PositionTicks,
/// DeviceQueue.CurrentPositionTicks) are persisted ITEM-ABSOLUTE since the JF-522
/// writer fix (the stop event composes the stream's launch base at write time), so
/// they pass through unchanged; pre-JF-522 leftovers mint early, never past the
/// true position. Raw-static launches (audio items, Echo-decodable video) keep the
/// caller's offset unchanged on every fallback. The resolve mints the new base
/// into the launch-scope store, so the next resume cycle composes from it.
/// </summary>
[Collection("Plugin")]
public class ResumeIntentAudioVariantOffsetTests : PluginTestBase, IDisposable
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();
    private readonly DeviceQueueManager _queueManager;
    private readonly string _tempDir;

    public ResumeIntentAudioVariantOffsetTests()
    {
        _tempDir = TestHelpers.CreateRegisteredTempDir("resume-audio-variant-tests");
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
    public async Task Resume_Eac3Episode_ViaSessionPlayState_MintsItemAbsolutePosition()
    {
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(episode);

        // Fallback 2 (JF-522 re-pin): the persisted play state carries an ITEM-ABSOLUTE
        // position under the writer contract (the stop event composes the stream's
        // launch base at write time), so the tail mints it directly. The raw-regime
        // version of this test pinned the DROP (the position was output-timeline-
        // relative and a mint would have been false).
        var context = CreateContext(id.ToString(), 0);
        session.PlayState!.PositionTicks = TimeSpan.FromMinutes(20).Ticks;

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMinutes(20).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task Resume_Eac3Episode_ViaDeviceQueue_MintsItemAbsolutePosition()
    {
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        var session = CreateSessionWithNowPlaying(episode);

        // Fallback 3 (JF-522 re-pin): context offset 0 (cleared after pause); the
        // DeviceQueue position is persisted ITEM-ABSOLUTE by the stop event, so the
        // tail mints it directly. The raw-regime version pinned the DROP.
        var queue = _queueManager.GetOrCreateQueue("test-device");
        queue.CurrentItemId = id.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromMinutes(20).Ticks;
        _fx.LibraryManager.Setup(m => m.GetItemById(id)).Returns(episode);

        var context = CreateContext(id.ToString(), 0);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMinutes(20).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Fact]
    public async Task Resume_Eac3Episode_ViaDeviceQueue_WithEmptyContext_MintsTranscodeUrl()
    {
        // Live-incident shape (2026-09-22) + review finding: the one-shot resume after
        // PlaybackStopped has a null token AND a null session item, and the tail only
        // probes codecs when it holds a real BaseItem. The queue item is materialized
        // through the library, so the EAC3 episode routes to the audio-only transcode
        // instead of the raw static /Audio URL the Echo cannot decode.
        var id = Guid.NewGuid();
        var episode = new TestHelpers.TestEpisodeWithStreams(
            "Ribs",
            id,
            TestHelpers.TestStream(MediaStreamType.Video, "h264"),
            TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));
        _fx.LibraryManager.Setup(m => m.GetItemById(id)).Returns(episode);

        var session = TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);
        session.PlayState = new PlayerStateInfo();
        // FullNowPlayingItem stays null (the post-stop shape).

        var queue = _queueManager.GetOrCreateQueue("test-device");
        queue.CurrentItemId = id.ToString();
        queue.CurrentPositionTicks = TimeSpan.FromMinutes(20).Ticks;

        var context = CreateContext(null!, 0);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/audio.m3u8?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain($"stream?static=true&api_key=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
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
    /// JF-520 spec case, tail side (seeding re-pinned by JF-522): the launch-scoped
    /// base B (20:00) plus a device-derived context offset O (5:00) mints ?start=B+O
    /// (25:00, item-absolute) on the transcode URL, directive offset 0, and the
    /// confirm's directive records the NEW base (25:00) in the launch-scope store so
    /// the next resume cycle composes from it (pins the helper's read-before-record
    /// ordering: the minted start and the post-directive scope agree, i.e. the
    /// resolver did not read back its own write).
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
        _queueManager.RecordLaunchBase("test-device", id.ToString(), (long)TimeSpan.FromMinutes(20).TotalMilliseconds, enqueued: false);
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
        Assert.Equal(expectedMs, _queueManager.GetActiveLaunchBase("test-device", id.ToString()));
    }

    /// <summary>
    /// JF-522, DeviceQueue-sourced item_id path (fallback 3): the queue's item pointer
    /// feeds the resume, its persisted position is ITEM-ABSOLUTE under the writer
    /// contract, and the confirm's directive records the minted position as the item's
    /// new launch base - proving the fallback-3 classification flip is not
    /// context-offset-only and the chokepoint recording fires on this path too.
    /// </summary>
    [Fact]
    public async Task Resume_Eac3Episode_ViaDeviceQueue_MintsPositionAndRecordsLaunchBase()
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
        _fx.LibraryManager.Setup(m => m.GetItemById(id)).Returns(episode);

        var context = CreateContext(id.ToString(), 0);

        var response = await CreateHandler().HandleAsync(
            new IntentRequest { Intent = new Intent { Name = "AMAZON.ResumeIntent" } },
            context,
            TestHelpers.CreateTestUser(),
            session,
            CancellationToken.None);

        long expectedMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMilliseconds(expectedMs).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(expectedMs, _queueManager.GetActiveLaunchBase("test-device", id.ToString()));
    }
}
