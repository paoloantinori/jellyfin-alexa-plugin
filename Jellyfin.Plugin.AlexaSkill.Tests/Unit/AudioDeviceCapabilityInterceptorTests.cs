using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AlexaSession = Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class AudioDeviceCapabilityInterceptorTests : PluginTestBase
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly Guid _testUserId;

    public AudioDeviceCapabilityInterceptorTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddDebug());
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _testUserId = Guid.NewGuid();
        EnsurePluginInstance();
    }

    private void EnsurePluginInstance()
    {
        if (Plugin.Instance != null)
        {
            return;
        }

        var tmpDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alexa-skill-test-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(tmpDir);

        var appPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(System.IO.Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());

        var userManager = new Mock<MediaBrowser.Controller.Library.IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            _loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }

    private class StubHandler : BaseHandler
    {
        private readonly Func<Task<SkillResponse>> _handleFunc;

        public StubHandler(
            ISessionManager sessionManager,
            PluginConfiguration config,
            ILoggerFactory loggerFactory,
            Func<Task<SkillResponse>> handleFunc)
            : base(sessionManager, config, loggerFactory)
        {
            _handleFunc = handleFunc;
        }

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Jellyfin.Plugin.AlexaSkill.Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => _handleFunc();

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Jellyfin.Plugin.AlexaSkill.Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
            => _handleFunc();

        public override bool CanHandle(Request request) => true;
    }

    private static IntentRequest CreateIntentRequest(string locale = "en-US")
    {
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            Locale = locale
        };
        request.Type = "IntentRequest";
        return request;
    }

    private static LaunchRequest CreateLaunchRequest()
    {
        var request = new LaunchRequest { Locale = "en-US" };
        request.Type = "LaunchRequest";
        return request;
    }

    private static Context CreateContextWithAudioPlayer()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>
                    {
                        { "AudioPlayer", new { } }
                    }
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    private static Context CreateContextWithoutAudioPlayer()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    private static Context CreateContextWithNullSupportedInterfaces()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = null
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    private static Context CreateContextWithNullDevice()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                Device = null,
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    private StubHandler CreatePlaceholderHandler()
    {
        return new StubHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            () => Task.FromResult(ResponseBuilder.Empty()));
    }

    // =====================================================================
    // Tests
    // =====================================================================

    [Fact]
    public async Task NoAudioPlayer_ReturnsFalse_ShortCircuits()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateIntentRequest();
        var context = CreateContextWithoutAudioPlayer();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        bool result = await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.False(result, "Should short-circuit (return false) when AudioPlayer is not supported");
        Assert.NotNull(requestContext.Response);
        Assert.NotNull(requestContext.Response.Response.OutputSpeech);
    }

    [Fact]
    public async Task NoAudioPlayer_SetsLocalizedMessage()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateIntentRequest("en-US");
        var context = CreateContextWithoutAudioPlayer();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.NotNull(requestContext.Response);
        var speech = Assert.IsType<PlainTextOutputSpeech>(requestContext.Response.Response.OutputSpeech);
        Assert.Contains("audio playback", speech.Text);
    }

    [Fact]
    public async Task NoAudioPlayer_ItalianLocale_ReturnsItalianMessage()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateIntentRequest("it-IT");
        var context = CreateContextWithoutAudioPlayer();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.NotNull(requestContext.Response);
        var speech = Assert.IsType<PlainTextOutputSpeech>(requestContext.Response.Response.OutputSpeech);
        Assert.Contains("riproduzione audio", speech.Text);
    }

    [Fact]
    public async Task HasAudioPlayer_ReturnsTrue_ContinuesPipeline()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateIntentRequest();
        var context = CreateContextWithAudioPlayer();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        bool result = await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.True(result, "Should continue pipeline when AudioPlayer is supported");
        Assert.Null(requestContext.Response);
    }

    [Fact]
    public async Task NullSupportedInterfaces_ReturnsFalse_ShortCircuits()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateIntentRequest();
        var context = CreateContextWithNullSupportedInterfaces();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        bool result = await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.False(result, "Should short-circuit when SupportedInterfaces is null");
        Assert.NotNull(requestContext.Response);
    }

    [Fact]
    public async Task NullDevice_ReturnsFalse_ShortCircuits()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateIntentRequest();
        var context = CreateContextWithNullDevice();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        bool result = await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.False(result, "Should short-circuit when Device is null");
        Assert.NotNull(requestContext.Response);
    }

    [Fact]
    public async Task LaunchRequest_SkipsCheck_ReturnsTrue()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateLaunchRequest();
        // Even without AudioPlayer, LaunchRequest should pass through
        var context = CreateContextWithoutAudioPlayer();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        bool result = await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.True(result, "Should pass through LaunchRequest without checking AudioPlayer");
        Assert.Null(requestContext.Response);
    }

    [Fact]
    public async Task IntentWithAudioPlayer_FullPipeline_HandlerRuns()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var handler = CreatePlaceholderHandler();

        var request = CreateIntentRequest();
        var context = CreateContextWithAudioPlayer();
        var requestContext = new RequestContext(request, context, null, handler);

        bool result = await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.True(result);
        Assert.Null(requestContext.Response);
    }

    [Fact]
    public async Task IntentWithoutAudioPlayer_FullPipeline_HandlerSkipped()
    {
        var handlerCalled = false;
        var handler = new StubHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            () =>
            {
                handlerCalled = true;
                return Task.FromResult(ResponseBuilder.Empty());
            });

        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var pipeline = new RequestPipeline(
            new[] { interceptor },
            Array.Empty<IResponseInterceptor>(),
            _loggerFactory.CreateLogger<RequestPipeline>());

        var request = CreateIntentRequest();
        var context = CreateContextWithoutAudioPlayer();

        var result = await pipeline.ExecuteAsync(handler, request, context, null, CancellationToken.None);

        Assert.False(handlerCalled, "Handler should not be called when AudioPlayer is not supported");
        Assert.NotNull(result.Response.OutputSpeech);
        var speech = Assert.IsType<PlainTextOutputSpeech>(result.Response.OutputSpeech);
        Assert.Contains("audio playback", speech.Text);
    }

    [Fact]
    public async Task NoAudioPlayer_EndsSession()
    {
        var interceptor = new AudioDeviceCapabilityInterceptor(
            _loggerFactory.CreateLogger<AudioDeviceCapabilityInterceptor>());

        var request = CreateIntentRequest();
        var context = CreateContextWithoutAudioPlayer();
        var handler = CreatePlaceholderHandler();

        var requestContext = new RequestContext(request, context, null, handler);

        await interceptor.ProcessAsync(requestContext, CancellationToken.None);

        Assert.NotNull(requestContext.Response);
        Assert.True(requestContext.Response.Response.ShouldEndSession);
    }
}
