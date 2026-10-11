using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AlexaSession = Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for LastPlayedResponseInterceptor: records the last-played item per device for
/// VIDEO content (movies/episodes via /Videos/ URLs). Confirms audio/audiobook VideoApp
/// URLs are skipped (handled by BaseHandler) and edge cases no-op cleanly.
/// </summary>
public class LastPlayedResponseInterceptorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DeviceQueueManager _queueManager;
    private readonly LastPlayedResponseInterceptor _interceptor;
    private readonly ILoggerFactory _loggerFactory;

    public LastPlayedResponseInterceptorTests()
    {
        _tempDir = TestHelpers.CreateRegisteredTempDir("lp-test");
        _loggerFactory = LoggerFactory.Create(b => b.AddDebug());
        _queueManager = new DeviceQueueManager(_tempDir, _loggerFactory.CreateLogger<DeviceQueueManager>());
        _interceptor = new LastPlayedResponseInterceptor(
            _queueManager, _loggerFactory.CreateLogger<LastPlayedResponseInterceptor>());
    }

    public void Dispose()
    {
        _queueManager.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static RequestContext CreateContext(SkillResponse? response, string deviceId = "test-device")
    {
        var request = new IntentRequest { Type = "IntentRequest" };
        var alexaContext = new Context
        {
            System = new AlexaSystem
            {
                User = new User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = deviceId }
            }
        };

        var handler = new Mock<BaseHandler>(
            Mock.Of<ISessionManager>(),
            new PluginConfiguration(),
            LoggerFactory.Create(b => { }));

        var ctx = new RequestContext(request, alexaContext, new AlexaSession { New = false }, handler.Object)
        {
            Response = response
        };
        return ctx;
    }

    private static SkillResponse ResponseWithDirective(IDirective directive)
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                Directives = new List<IDirective> { directive }
            }
        };
    }

    private static VideoAppLaunchDirective VideoApp(string source)
        => new() { VideoItem = new Jellyfin.Plugin.AlexaSkill.Alexa.Directive.VideoItem { Source = source } };

    [Fact]
    public async Task ProcessAsync_VideosUrl_RecordsGuid()
    {
        string guid = Guid.NewGuid().ToString();
        var ctx = CreateContext(ResponseWithDirective(
            VideoApp($"https://jellyfin.example/Videos/{guid}/stream?static=true&api_key=x")));

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Equal(guid, _queueManager.GetLastPlayedItemId("test-device"));
    }

    /// <summary>
    /// JF-498: an episode/movie launched through the HLS remux endpoint (audio codec
    /// without an Echo decoder) is still a video play and must be recorded, or
    /// continue-watching/start-over would silently miss every EAC3 episode.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_EpisodeRemuxUrl_RecordsGuid()
    {
        string guid = Guid.NewGuid().ToString();
        var ctx = CreateContext(ResponseWithDirective(
            VideoApp($"https://jellyfin.example/alexaskill/api/video-audio/episode/{guid}/stream.m3u8?token=abc.def")));

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Equal(guid, _queueManager.GetLastPlayedItemId("test-device"));
    }

    [Fact]
    public async Task ProcessAsync_VideoAudioUrl_DoesNotRecord()
    {
        // Audio routed through VideoApp (NativeControlsForAudio) — handled by BaseHandler.
        string guid = Guid.NewGuid().ToString();
        var ctx = CreateContext(ResponseWithDirective(
            VideoApp($"https://jellyfin.example/alexaskill/api/video-audio/{guid}/stream.m3u8")));

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Null(_queueManager.GetLastPlayedItemId("test-device"));
    }

    [Fact]
    public async Task ProcessAsync_AudiobookUrl_DoesNotRecord()
    {
        // Audiobook concat URL carries the book ID — handled by BaseHandler (chapter precision).
        string parent = Guid.NewGuid().ToString();
        var ctx = CreateContext(ResponseWithDirective(
            VideoApp($"https://jellyfin.example/alexaskill/api/video-audio/audiobook/{parent}/stream.m3u8")));

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Null(_queueManager.GetLastPlayedItemId("test-device"));
    }

    [Fact]
    public async Task ProcessAsync_NonVideoDirective_DoesNotRecord()
    {
        var ctx = CreateContext(ResponseWithDirective(new StopDirective()));

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Null(_queueManager.GetLastPlayedItemId("test-device"));
    }

    [Fact]
    public async Task ProcessAsync_VideoSourceWithoutGuid_DoesNotRecord()
    {
        var ctx = CreateContext(ResponseWithDirective(VideoApp("https://jellyfin.example/Videos/not-a-guid/stream")));

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Null(_queueManager.GetLastPlayedItemId("test-device"));
    }

    [Fact]
    public async Task ProcessAsync_NullResponse_NoOp()
    {
        var ctx = CreateContext(null);

        await _interceptor.ProcessAsync(ctx, CancellationToken.None); // must not throw

        Assert.Null(_queueManager.GetLastPlayedItemId("test-device"));
    }

    [Fact]
    public async Task ProcessAsync_EmptyDeviceId_NoOp()
    {
        string guid = Guid.NewGuid().ToString();
        var ctx = CreateContext(ResponseWithDirective(
            VideoApp($"https://jellyfin.example/Videos/{guid}/stream")), deviceId: "");

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Null(_queueManager.GetLastPlayedItemId(""));
    }

    [Fact]
    public async Task ProcessAsync_FirstVideosDirectiveWins()
    {
        string first = Guid.NewGuid().ToString();
        string second = Guid.NewGuid().ToString();
        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                Directives = new List<IDirective>
                {
                    VideoApp($"https://jellyfin.example/Videos/{first}/stream"),
                    VideoApp($"https://jellyfin.example/Videos/{second}/stream")
                }
            }
        };
        var ctx = CreateContext(response);

        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Equal(first, _queueManager.GetLastPlayedItemId("test-device"));
    }

    /// <summary>
    /// JF-858 remux wiring: the EPISODE-REMUX source is served by the plugin's own
    /// segment endpoint under the same item GUID, so the interceptor links the
    /// stream beside its ledger record and a segment observation refreshes the
    /// entry (the EAC3 movie past its first hour keeps a fresh stamp). The NATIVE
    /// /Videos arm records NO link (Jellyfin's endpoint is not observable
    /// plugin-side), pinned by the twin below.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_EpisodeRemuxUrl_LinksStreamForSegmentRefresh_JF858()
    {
        string guid = Guid.NewGuid().ToString();
        var ctx = CreateContext(ResponseWithDirective(
            VideoApp($"https://jellyfin.example/alexaskill/api/video-audio/episode/{guid}/stream.m3u8?token=t")));
        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        TestHelpers.BackdateLastPlayedStamp(
            _queueManager, "test-device",
            DeviceQueueManager.LastPlayedStampRefreshInterval + TimeSpan.FromMinutes(5));
        _queueManager.RefreshLastPlayedStampForStream(guid);

        Assert.True(
            DeviceQueueManager.IsWithinLastPlayedRecencyWindow(_queueManager.GetLastPlayedSnapshot("test-device").WrittenAt),
            "the remux entry's stamp is back inside the window (the link routed the observation)");
    }

    /// <summary>
    /// JF-858 wiring boundary: the NATIVE /Videos source records the ledger entry
    /// but NO stream link - a segment observation for that GUID refreshes nothing.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_NativeVideosUrl_RecordsNoStreamLink_JF858()
    {
        string guid = Guid.NewGuid().ToString();
        var ctx = CreateContext(ResponseWithDirective(
            VideoApp($"https://jellyfin.example/Videos/{guid}/stream?static=true&api_key=x")));
        await _interceptor.ProcessAsync(ctx, CancellationToken.None);

        TestHelpers.BackdateLastPlayedStamp(
            _queueManager, "test-device",
            DeviceQueueManager.LastPlayedStampRefreshInterval + TimeSpan.FromMinutes(5));
        DateTime backdated = _queueManager.GetQueue("test-device")!.LastPlayedWrittenAt!.Value;
        _queueManager.RefreshLastPlayedStampForStream(guid);

        Assert.Equal(backdated, _queueManager.GetQueue("test-device")!.LastPlayedWrittenAt);
    }
}
