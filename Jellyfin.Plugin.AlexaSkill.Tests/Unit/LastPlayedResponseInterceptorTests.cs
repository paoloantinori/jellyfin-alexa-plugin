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
        _tempDir = Path.Combine(Path.GetTempPath(), $"lp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
}
