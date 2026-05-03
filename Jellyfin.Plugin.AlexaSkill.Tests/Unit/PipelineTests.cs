using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using AlexaSession = Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PipelineTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly Guid _testUserId;

    public PipelineTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddDebug());
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _testUserId = Guid.NewGuid();

        // Plugin.Instance is accessed by BaseHandler.HandleRequestAsync during auth.
        // Set it up with our test config so the pipeline can run through the full handler chain.
        EnsurePluginInstance();
    }

    /// <summary>
    /// Sets Plugin.Instance to a mock plugin with our test configuration.
    /// The Plugin constructor sets Plugin.Instance = this.
    /// IApplicationPaths must be fully mocked so that BasePlugin.Configuration
    /// can load without hitting null paths.
    /// </summary>
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
        // Return a default PluginConfiguration when deserializing
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(new PluginConfiguration());

        var userManager = new Mock<MediaBrowser.Controller.Library.IUserManager>();

        // Create a real Plugin instance. Its constructor sets Plugin.Instance = this.
        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            _loggerFactory,
            userManager.Object);

        // BaseHandler.HandleRequestAsync reads Plugin.Instance.Configuration.ServerAddress.
        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }

    // --- Helper: BaseHandler stub that bypasses the auth chain ---

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

        // Override both HandleAsync overloads. The base HandleRequestAsync calls
        // the 6-param overload (with sessionAttributes) after auth succeeds.
        // Must use fully-qualified Entities.User to disambiguate from Alexa.NET.Request.User.
        public override Task<SkillResponse> HandleAsync(Request request, Context context, Jellyfin.Plugin.AlexaSkill.Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        {
            return _handleFunc();
        }

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Jellyfin.Plugin.AlexaSkill.Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
        {
            return _handleFunc();
        }

        public override bool CanHandle(Request request) => true;
    }

    /// <summary>
    /// Creates a Context with a valid access token matching a registered test user.
    /// </summary>
    private Context CreateAuthenticatableContext()
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = _testUserId.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
    }

    /// <summary>
    /// Registers a test user in the config so GetUserById succeeds, and sets up
    /// the session manager mock to return a dummy session for the user's token.
    /// </summary>
    private void SetUpAuthChain()
    {
        var testUser = new Jellyfin.Plugin.AlexaSkill.Entities.User
        {
            Id = _testUserId,
            InvocationName = "test",
            JellyfinToken = "jellyfin-test-token"
        };
        _config.AddUser(testUser);

        var sessionInfo = TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
        _sessionManagerMock
            .Setup(sm => sm.GetSessionByAuthenticationToken(
                testUser.JellyfinToken,
                "test-device",
                It.IsAny<string>()))
            .ReturnsAsync(sessionInfo);
    }

    private static Request CreateSkillRequest(string type = "IntentRequest")
    {
        // Alexa.NET Request.Type is a plain string property set during JSON deserialization.
        // The concrete subclasses (IntentRequest, LaunchRequest, etc.) do not auto-set Type
        // in their constructors, so we must set it explicitly for testing.
        var request = type switch
        {
            "IntentRequest" => (Request)new IntentRequest(),
            "LaunchRequest" => new LaunchRequest(),
            "SessionEndedRequest" => new SessionEndedRequest(),
            _ => new IntentRequest()
        };
        request.Type = type;
        return request;
    }

    private static AlexaSession CreateSession(Dictionary<string, object>? attributes = null)
    {
        return new AlexaSession
        {
            Attributes = attributes ?? new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// Creates a handler with auth chain set up, for pipeline tests that will
    /// actually invoke the handler through HandleRequestAsync.
    /// </summary>
    private StubHandler CreateHandler(Func<Task<SkillResponse>> handleFunc)
    {
        SetUpAuthChain();
        return new StubHandler(_sessionManagerMock.Object, _config, _loggerFactory, handleFunc);
    }

    /// <summary>
    /// Creates a handler for tests that don't exercise the full pipeline's auth chain
    /// (e.g., RequestContext construction tests where the handler is just a placeholder).
    /// </summary>
    private StubHandler CreatePlaceholderHandler()
    {
        return new StubHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory,
            () => Task.FromResult(ResponseBuilder.Empty()));
    }

    // =====================================================================
    // RequestContext tests
    // =====================================================================

    [Fact]
    public void RequestContext_Construction_SetsProperties()
    {
        var request = CreateSkillRequest("IntentRequest");
        var context = CreateAuthenticatableContext();
        var session = CreateSession();
        var handler = CreatePlaceholderHandler();

        var ctx = new RequestContext(request, context, session, handler);

        Assert.Same(request, ctx.SkillRequest);
        Assert.Same(context, ctx.AlexaContext);
        Assert.Same(session, ctx.AlexaSession);
        Assert.Same(handler, ctx.Handler);
        Assert.Null(ctx.Response);
    }

    [Fact]
    public void RequestContext_RequestType_ReturnsRequestTypeString()
    {
        var request = CreateSkillRequest("IntentRequest");
        var ctx = new RequestContext(request, CreateAuthenticatableContext(), null, CreatePlaceholderHandler());

        Assert.Equal("IntentRequest", ctx.RequestType);
    }

    [Fact]
    public void RequestContext_RequestType_LaunchRequest_ReturnsCorrectType()
    {
        var request = CreateSkillRequest("LaunchRequest");
        var ctx = new RequestContext(request, CreateAuthenticatableContext(), null, CreatePlaceholderHandler());

        Assert.Equal("LaunchRequest", ctx.RequestType);
    }

    [Fact]
    public void RequestContext_RequestType_SessionEndedRequest_ReturnsCorrectType()
    {
        var request = CreateSkillRequest("SessionEndedRequest");
        var ctx = new RequestContext(request, CreateAuthenticatableContext(), null, CreatePlaceholderHandler());

        Assert.Equal("SessionEndedRequest", ctx.RequestType);
    }

    [Fact]
    public void RequestContext_Response_Settable()
    {
        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CreatePlaceholderHandler());

        var response = ResponseBuilder.Tell("hello");
        ctx.Response = response;

        Assert.Same(response, ctx.Response);
    }

    [Fact]
    public void RequestContext_StartedAt_DefaultAndSettable()
    {
        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CreatePlaceholderHandler());

        Assert.Equal(default(DateTimeOffset), ctx.StartedAt);

        var now = DateTimeOffset.UtcNow;
        ctx.StartedAt = now;
        Assert.Equal(now, ctx.StartedAt);
    }

    [Fact]
    public void RequestContext_AlexaSession_CanBeNull()
    {
        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CreatePlaceholderHandler());

        Assert.Null(ctx.AlexaSession);
    }

    // =====================================================================
    // RequestPipeline tests
    // =====================================================================

    [Fact]
    public async Task Pipeline_NoInterceptors_HandlerRunsAndResponseReturned()
    {
        var expectedResponse = ResponseBuilder.Tell("handled");
        var handler = CreateHandler(() => Task.FromResult(expectedResponse));
        var pipeline = new RequestPipeline(
            Array.Empty<IRequestInterceptor>(),
            Array.Empty<IResponseInterceptor>(),
            _loggerFactory.CreateLogger<RequestPipeline>());

        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CancellationToken.None);

        Assert.Same(expectedResponse, result);
    }

    [Fact]
    public async Task Pipeline_RequestInterceptorReturnsFalse_ShortCircuits()
    {
        var shortCircuitResponse = ResponseBuilder.Tell("blocked");
        var handlerCalled = false;

        var handler = CreateHandler(() =>
        {
            handlerCalled = true;
            return Task.FromResult(ResponseBuilder.Empty());
        });

        var interceptor = new Mock<IRequestInterceptor>();
        interceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Returns<RequestContext, CancellationToken>((ctx, _) =>
            {
                ctx.Response = shortCircuitResponse;
                return Task.FromResult(false);
            });

        var pipeline = new RequestPipeline(
            new[] { interceptor.Object },
            Array.Empty<IResponseInterceptor>(),
            _loggerFactory.CreateLogger<RequestPipeline>());

        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CancellationToken.None);

        Assert.Same(shortCircuitResponse, result);
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task Pipeline_RequestInterceptorReturnsTrue_HandlerRuns()
    {
        var expectedResponse = ResponseBuilder.Tell("proceed");
        var handlerCalled = false;
        var handler = CreateHandler(() =>
        {
            handlerCalled = true;
            return Task.FromResult(expectedResponse);
        });

        var interceptor = new Mock<IRequestInterceptor>();
        interceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(true));

        var pipeline = new RequestPipeline(
            new[] { interceptor.Object },
            Array.Empty<IResponseInterceptor>(),
            _loggerFactory.CreateLogger<RequestPipeline>());

        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CancellationToken.None);

        Assert.True(handlerCalled);
        Assert.Same(expectedResponse, result);
    }

    [Fact]
    public async Task Pipeline_MultipleRequestInterceptors_AllRunInOrder()
    {
        var order = new List<int>();

        var interceptor1 = new Mock<IRequestInterceptor>();
        interceptor1
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(1))
            .Returns(Task.FromResult(true));

        var interceptor2 = new Mock<IRequestInterceptor>();
        interceptor2
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add(2))
            .Returns(Task.FromResult(true));

        var handler = CreateHandler(() =>
        {
            order.Add(3);
            return Task.FromResult(ResponseBuilder.Empty());
        });

        var pipeline = new RequestPipeline(
            new[] { interceptor1.Object, interceptor2.Object },
            Array.Empty<IResponseInterceptor>(),
            _loggerFactory.CreateLogger<RequestPipeline>());

        await pipeline.ExecuteAsync(handler, CreateSkillRequest(), CreateAuthenticatableContext(), null, CancellationToken.None);

        Assert.Equal(new List<int> { 1, 2, 3 }, order);
    }

    [Fact]
    public async Task Pipeline_ResponseInterceptors_RunAfterHandler()
    {
        var executionOrder = new List<string>();

        var handler = CreateHandler(() =>
        {
            executionOrder.Add("handler");
            return Task.FromResult(ResponseBuilder.Empty());
        });

        var responseInterceptor = new Mock<IResponseInterceptor>();
        responseInterceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => executionOrder.Add("response-interceptor"))
            .Returns(Task.CompletedTask);

        var pipeline = new RequestPipeline(
            Array.Empty<IRequestInterceptor>(),
            new[] { responseInterceptor.Object },
            _loggerFactory.CreateLogger<RequestPipeline>());

        await pipeline.ExecuteAsync(handler, CreateSkillRequest(), CreateAuthenticatableContext(), null, CancellationToken.None);

        Assert.Equal(new List<string> { "handler", "response-interceptor" }, executionOrder);
    }

    [Fact]
    public async Task Pipeline_ResponseInterceptors_RunInReverseOrder()
    {
        var order = new List<string>();

        var ri1 = new Mock<IResponseInterceptor>();
        ri1.Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("ri1"))
            .Returns(Task.CompletedTask);

        var ri2 = new Mock<IResponseInterceptor>();
        ri2.Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("ri2"))
            .Returns(Task.CompletedTask);

        var ri3 = new Mock<IResponseInterceptor>();
        ri3.Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("ri3"))
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(() => Task.FromResult(ResponseBuilder.Empty()));

        var pipeline = new RequestPipeline(
            Array.Empty<IRequestInterceptor>(),
            new[] { ri1.Object, ri2.Object, ri3.Object },
            _loggerFactory.CreateLogger<RequestPipeline>());

        await pipeline.ExecuteAsync(handler, CreateSkillRequest(), CreateAuthenticatableContext(), null, CancellationToken.None);

        // Reverse order: ri3, ri2, ri1
        Assert.Equal(new List<string> { "ri3", "ri2", "ri1" }, order);
    }

    [Fact]
    public async Task Pipeline_ResponseInterceptorException_DoesNotFailPipeline()
    {
        var handler = CreateHandler(() => Task.FromResult(ResponseBuilder.Tell("ok")));
        var secondInterceptorCalled = false;

        var failingInterceptor = new Mock<IResponseInterceptor>();
        failingInterceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var secondInterceptor = new Mock<IResponseInterceptor>();
        secondInterceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => secondInterceptorCalled = true)
            .Returns(Task.CompletedTask);

        // failingInterceptor registered first, runs last (reverse order). Second runs first.
        var pipeline = new RequestPipeline(
            Array.Empty<IRequestInterceptor>(),
            new[] { failingInterceptor.Object, secondInterceptor.Object },
            _loggerFactory.CreateLogger<RequestPipeline>());

        // Should not throw
        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(secondInterceptorCalled, "Second response interceptor should still run after first one fails");
    }

    [Fact]
    public async Task Pipeline_NullHandlerResponse_ReturnsResponseBuilderEmpty()
    {
        var handler = CreateHandler(() => Task.FromResult<SkillResponse>(null!));

        var pipeline = new RequestPipeline(
            Array.Empty<IRequestInterceptor>(),
            Array.Empty<IResponseInterceptor>(),
            _loggerFactory.CreateLogger<RequestPipeline>());

        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CancellationToken.None);

        Assert.NotNull(result);
        // ResponseBuilder.Empty() produces a response with an empty ResponseBody
        Assert.NotNull(result.Response);
    }

    [Fact]
    public async Task Pipeline_ShortCircuitWithNullResponse_ReturnsResponseBuilderEmpty()
    {
        var interceptor = new Mock<IRequestInterceptor>();
        interceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Returns<RequestContext, CancellationToken>((ctx, _) =>
            {
                ctx.Response = null;
                return Task.FromResult(false);
            });

        var handler = CreateHandler(() => Task.FromResult(ResponseBuilder.Empty()));

        var pipeline = new RequestPipeline(
            new[] { interceptor.Object },
            Array.Empty<IResponseInterceptor>(),
            _loggerFactory.CreateLogger<RequestPipeline>());

        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result.Response);
    }

    // =====================================================================
    // LoggingRequestInterceptor tests
    // =====================================================================

    [Fact]
    public async Task LoggingRequestInterceptor_SetsStartedAt()
    {
        var before = DateTimeOffset.UtcNow;
        var interceptor = new LoggingRequestInterceptor(_loggerFactory.CreateLogger<LoggingRequestInterceptor>());
        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CreatePlaceholderHandler());

        await interceptor.ProcessAsync(ctx, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        Assert.True(ctx.StartedAt >= before, "StartedAt should be >= time before ProcessAsync");
        Assert.True(ctx.StartedAt <= after, "StartedAt should be <= time after ProcessAsync");
    }

    [Fact]
    public async Task LoggingRequestInterceptor_ReturnsTrue()
    {
        var interceptor = new LoggingRequestInterceptor(_loggerFactory.CreateLogger<LoggingRequestInterceptor>());
        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CreatePlaceholderHandler());

        var result = await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.True(result);
    }

    // =====================================================================
    // LoggingResponseInterceptor tests
    // =====================================================================

    [Fact]
    public async Task LoggingResponseInterceptor_DoesNotThrow()
    {
        var interceptor = new LoggingResponseInterceptor(_loggerFactory.CreateLogger<LoggingResponseInterceptor>());
        var ctx = new RequestContext(
            CreateSkillRequest("IntentRequest"),
            CreateAuthenticatableContext(),
            null,
            CreatePlaceholderHandler())
        {
            StartedAt = DateTimeOffset.UtcNow
        };

        // Should complete without exception
        await interceptor.ProcessAsync(ctx, CancellationToken.None);
    }

    [Fact]
    public async Task LoggingResponseInterceptor_WithElapsedInterval_LogsCorrectly()
    {
        var interceptor = new LoggingResponseInterceptor(_loggerFactory.CreateLogger<LoggingResponseInterceptor>());
        var ctx = new RequestContext(
            CreateSkillRequest("IntentRequest"),
            CreateAuthenticatableContext(),
            null,
            CreatePlaceholderHandler())
        {
            StartedAt = DateTimeOffset.UtcNow.AddMilliseconds(-500)
        };

        // Should not throw even with a significant elapsed time
        await interceptor.ProcessAsync(ctx, CancellationToken.None);
    }

    // =====================================================================
    // SessionAttributesInterceptor tests
    // =====================================================================

    [Fact]
    public async Task SessionAttributesInterceptor_MergesAttributesWhenResponseHasNone()
    {
        var interceptor = new SessionAttributesInterceptor(_loggerFactory.CreateLogger<SessionAttributesInterceptor>());

        var incomingAttrs = new Dictionary<string, object>
        {
            { "disambiguation_index", 2 },
            { "media_type", "Audio" }
        };

        var response = ResponseBuilder.Tell("test");
        // ResponseBuilder.Tell does not set SessionAttributes by default
        Assert.Null(response.SessionAttributes);

        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            CreateSession(incomingAttrs),
            CreatePlaceholderHandler());
        ctx.Response = response;

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.NotNull(ctx.Response.SessionAttributes);
        Assert.Equal(2, ctx.Response.SessionAttributes.Count);
        Assert.Equal(2, ctx.Response.SessionAttributes["disambiguation_index"]);
        Assert.Equal("Audio", ctx.Response.SessionAttributes["media_type"]);
    }

    [Fact]
    public async Task SessionAttributesInterceptor_DoesNotOverwriteExistingKeys()
    {
        var interceptor = new SessionAttributesInterceptor(_loggerFactory.CreateLogger<SessionAttributesInterceptor>());

        var incomingAttrs = new Dictionary<string, object>
        {
            { "shared_key", "incoming_value" },
            { "incoming_only", "present" }
        };

        var response = ResponseBuilder.Tell("test");
        response.SessionAttributes = new Dictionary<string, object>
        {
            { "shared_key", "existing_value" },
            { "response_only", "kept" }
        };

        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            CreateSession(incomingAttrs),
            CreatePlaceholderHandler());
        ctx.Response = response;

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Equal(3, ctx.Response.SessionAttributes.Count);
        Assert.Equal("existing_value", ctx.Response.SessionAttributes["shared_key"]);
        Assert.Equal("present", ctx.Response.SessionAttributes["incoming_only"]);
        Assert.Equal("kept", ctx.Response.SessionAttributes["response_only"]);
    }

    [Fact]
    public async Task SessionAttributesInterceptor_NoopsWhenSessionHasNoAttributes()
    {
        var interceptor = new SessionAttributesInterceptor(_loggerFactory.CreateLogger<SessionAttributesInterceptor>());

        // Empty attributes dictionary
        var session = CreateSession(new Dictionary<string, object>());
        var response = ResponseBuilder.Tell("test");
        response.SessionAttributes = new Dictionary<string, object> { { "existing", "value" } };

        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            session,
            CreatePlaceholderHandler());
        ctx.Response = response;

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        // Should not have added anything
        Assert.Single(ctx.Response.SessionAttributes);
        Assert.Equal("value", ctx.Response.SessionAttributes["existing"]);
    }

    [Fact]
    public async Task SessionAttributesInterceptor_NoopsWhenSessionIsNull()
    {
        var interceptor = new SessionAttributesInterceptor(_loggerFactory.CreateLogger<SessionAttributesInterceptor>());

        var response = ResponseBuilder.Tell("test");
        response.SessionAttributes = new Dictionary<string, object> { { "existing", "value" } };

        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null, // null session
            CreatePlaceholderHandler());
        ctx.Response = response;

        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Single(ctx.Response.SessionAttributes);
        Assert.Equal("value", ctx.Response.SessionAttributes["existing"]);
    }

    [Fact]
    public async Task SessionAttributesInterceptor_NoopsWhenResponseIsNull()
    {
        var interceptor = new SessionAttributesInterceptor(_loggerFactory.CreateLogger<SessionAttributesInterceptor>());

        var incomingAttrs = new Dictionary<string, object> { { "key", "value" } };

        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            CreateSession(incomingAttrs),
            CreatePlaceholderHandler());
        ctx.Response = null; // null response

        // Should not throw
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.Response);
    }

    [Fact]
    public async Task SessionAttributesInterceptor_NoopsWhenResponseResponseBodyIsNull()
    {
        var interceptor = new SessionAttributesInterceptor(_loggerFactory.CreateLogger<SessionAttributesInterceptor>());

        var incomingAttrs = new Dictionary<string, object> { { "key", "value" } };

        // SkillResponse with null Response body
        var response = new SkillResponse { Version = "1.0", Response = null };

        var ctx = new RequestContext(
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            CreateSession(incomingAttrs),
            CreatePlaceholderHandler());
        ctx.Response = response;

        // Should not throw
        await interceptor.ProcessAsync(ctx, CancellationToken.None);

        Assert.Null(ctx.Response.SessionAttributes);
    }

    // =====================================================================
    // Integration-style: full pipeline with real interceptors
    // =====================================================================

    [Fact]
    public async Task FullPipeline_LoggingAndSessionInterceptors_WorkTogether()
    {
        var incomingAttrs = new Dictionary<string, object>
        {
            { "state", "disambiguating" }
        };

        var handlerResponse = ResponseBuilder.Tell("picking");
        var handlerCalled = false;

        var handler = CreateHandler(() =>
        {
            handlerCalled = true;
            return Task.FromResult(handlerResponse);
        });

        var pipeline = new RequestPipeline(
            new IRequestInterceptor[]
            {
                new LoggingRequestInterceptor(_loggerFactory.CreateLogger<LoggingRequestInterceptor>())
            },
            new IResponseInterceptor[]
            {
                new LoggingResponseInterceptor(_loggerFactory.CreateLogger<LoggingResponseInterceptor>()),
                new SessionAttributesInterceptor(_loggerFactory.CreateLogger<SessionAttributesInterceptor>())
            },
            _loggerFactory.CreateLogger<RequestPipeline>());

        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest("IntentRequest"),
            CreateAuthenticatableContext(),
            CreateSession(incomingAttrs),
            CancellationToken.None);

        Assert.True(handlerCalled);
        Assert.Same(handlerResponse, result);

        // SessionAttributesInterceptor (registered second, runs first in reverse)
        // should have merged session attributes into the response
        Assert.NotNull(result.SessionAttributes);
        Assert.Equal("disambiguating", result.SessionAttributes["state"]);
    }

    [Fact]
    public async Task FullPipeline_ShortCircuitPreventsHandlerAndResponseInterceptors()
    {
        var responseInterceptorCalled = false;
        var handlerCalled = false;

        var blockingInterceptor = new Mock<IRequestInterceptor>();
        blockingInterceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Returns<RequestContext, CancellationToken>((ctx, _) =>
            {
                ctx.Response = ResponseBuilder.Tell("blocked");
                return Task.FromResult(false);
            });

        var responseInterceptor = new Mock<IResponseInterceptor>();
        responseInterceptor
            .Setup(i => i.ProcessAsync(It.IsAny<RequestContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => responseInterceptorCalled = true)
            .Returns(Task.CompletedTask);

        var handler = CreateHandler(() =>
        {
            handlerCalled = true;
            return Task.FromResult(ResponseBuilder.Empty());
        });

        var pipeline = new RequestPipeline(
            new[] { blockingInterceptor.Object },
            new[] { responseInterceptor.Object },
            _loggerFactory.CreateLogger<RequestPipeline>());

        var result = await pipeline.ExecuteAsync(
            handler,
            CreateSkillRequest(),
            CreateAuthenticatableContext(),
            null,
            CancellationToken.None);

        Assert.False(handlerCalled, "Handler should not be called on short-circuit");
        Assert.False(responseInterceptorCalled, "Response interceptors should not run on short-circuit");
        Assert.Equal("blocked", ((PlainTextOutputSpeech)result.Response.OutputSpeech).Text);
    }
}
