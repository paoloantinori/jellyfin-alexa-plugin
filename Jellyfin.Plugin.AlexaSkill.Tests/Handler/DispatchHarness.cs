using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.EntryPoints;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using SkillRequest = global::Alexa.NET.Request.SkillRequest;
using AlexaSession = global::Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Routing-level test harness (JF-433). Existing unit tests call HandleAsync directly,
/// bypassing CanHandle, the registration order, and the controller's force-route layer.
/// This harness drives requests through the SAME selection semantics as the live dispatch.
/// See DispatchRoutingTests for coverage.
/// </summary>
/// <remarks>
/// Handler enumeration and selection are the PRODUCTION units: this harness calls
/// <see cref="Registrator.RegisteredHandlerTypes"/> and
/// <see cref="HandlerSelector.Select(IEnumerable{BaseHandler}, SkillRequest)"/>, the
/// same units AlexaSkillController dispatches through (JF-452), so a controller-side
/// routing edit cannot stay green here. Only handler construction is harness-local
/// (reflection + dependency overrides).
/// Execution (<see cref="DispatchAsync"/>) is handler-level only: the real RequestPipeline
/// runs (which owns the warming-exception translation), but with NO request/response
/// interceptors, NO 6-second timeout token, and NO controller error envelope; those live
/// outside the routing layer this harness pins.
/// </remarks>
public sealed class DispatchHarness : IDisposable
{
    /// <summary>
    /// The session-attribute key the production force-route matches, held as the
    /// wire-format LITERAL. Production selection (HandlerSelector, used by both the
    /// controller and this harness) reads FindSongIntentHandler.SessionDataKey;
    /// DispatchRoutingTests pins this literal to that constant so the wire format
    /// cannot drift.
    /// </summary>
    internal const string FindSongSessionKey = "FindSongSessionData";

    private readonly Dictionary<Type, object> _dependencyOverrides = new();
    private readonly Dictionary<Type, object> _sharedDependencies = new();
    private readonly RequestPipeline _pipeline;
    private readonly string _queueDataDir = Path.Combine(Path.GetTempPath(), "dispatch-harness-queues-" + Guid.NewGuid());
    private List<BaseHandler>? _handlers;

    public DispatchHarness()
    {
        Config = new PluginConfiguration();
        TestHelpers.SetServerAddress(Config, "https://dispatch-harness.example.com");
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });
        SessionManager = new Mock<ISessionManager>();
        _pipeline = new RequestPipeline(
            Array.Empty<IRequestInterceptor>(),
            Array.Empty<IResponseInterceptor>(),
            LoggerFactory.CreateLogger<RequestPipeline>());
    }

    /// <summary>
    /// The shared configuration instance used by all handlers.
    /// </summary>
    public PluginConfiguration Config { get; }

    /// <summary>
    /// The shared session manager mock.
    /// </summary>
    public Mock<ISessionManager> SessionManager { get; }

    /// <summary>
    /// The shared logger factory.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Enumerates handler types in registration order. Delegates to the production
    /// enumeration (<see cref="Registrator.RegisteredHandlerTypes"/>) so the harness
    /// order cannot drift from DI registration order (JF-452).
    /// </summary>
    public static IReadOnlyList<Type> RegisteredHandlerTypes()
        => Registrator.RegisteredHandlerTypes();

    /// <summary>
    /// All registered handlers, constructed in registration order.
    /// </summary>
    public IReadOnlyList<BaseHandler> Handlers
    {
        get
        {
            if (_handlers != null)
                return _handlers;

            _handlers = RegisteredHandlerTypes().Select(CreateHandler).ToList();
            return _handlers;
        }
    }

    /// <summary>
    /// Overrides a dependency for handler construction. Must be called before the first
    /// access to Handlers; throws otherwise.
    /// </summary>
    public void OverrideDependency<TDependency>(TDependency instance)
        where TDependency : class
    {
        if (_handlers is not null)
        {
            throw new InvalidOperationException(
                "Cannot override dependencies after handlers have been constructed. " +
                "Call OverrideDependency before accessing Handlers.");
        }
        _dependencyOverrides[typeof(TDependency)] = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    /// <summary>
    /// Selects the handler for the given request via the PRODUCTION selection unit
    /// (<see cref="HandlerSelector.Select(IEnumerable{BaseHandler}, SkillRequest)"/>,
    /// the same path AlexaSkillController routes through: force-route + CanHandle
    /// loop). Does NOT execute the handler.
    /// </summary>
    /// <param name="request">The skill request containing Request, Context, and Session.</param>
    /// <returns>The routing decision: selected handler, whether force-routed, and null response.</returns>
    public RoutingDecision Select(SkillRequest request)
    {
        HandlerSelection selection = HandlerSelector.Select(Handlers, request);
        return new RoutingDecision(selection.Handler, selection.ForceRouted);
    }

    /// <summary>
    /// Selects the handler and executes it through the real RequestPipeline.
    /// </summary>
    /// <param name="request">The skill request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The routing decision with the handler's response.</returns>
    public async Task<RoutingDecision> DispatchAsync(SkillRequest request, CancellationToken cancellationToken = default)
    {
        RoutingDecision decision = Select(request);
        if (decision.Handler == null)
        {
            return decision;
        }

        SkillResponse response = await _pipeline.ExecuteAsync(
            decision.Handler,
            request.Request,
            request.Context,
            request.Session,
            cancellationToken).ConfigureAwait(false);

        return decision with { Response = response };
    }

    /// <summary>
    /// Enables full execution path: wires Plugin.Instance, adds a test user to Config
    /// (shared with all handlers), and configures the shared session manager mock to
    /// return a session. Must be called before DispatchAsync.
    /// </summary>
    /// <returns>The test user entity (its Id matches Context.System.User.AccessToken).</returns>
    public Entities.User EnableExecution()
    {
        TestHelpers.EnsurePluginInstance(
            Config,
            LoggerFactory,
            _ => { },
            "dispatch-harness");

        var user = TestHelpers.CreateTestUser();
        Config.Users.Add(user);

        // The same mock instance is injected into every handler (see ResolveDependency);
        // configuring it here makes HandleRequestAsync session resolution succeed.
        SessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()))
            .ReturnsAsync(TestHelpers.CreateTestSession(SessionManager.Object, LoggerFactory));

        return user;
    }

    /// <summary>
    /// Builds the Context matching an <see cref="EnableExecution"/> user for execution
    /// tests: the access token is the user id, and the device declares AudioPlayer
    /// support so the shape matches what a real Echo sends (the production capability
    /// interceptor refuses IntentRequests from devices without AudioPlayer).
    /// </summary>
    public static Context CreateExecutionContext(Entities.User user, string deviceId = "test-device")
        => new()
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = user.Id.ToString() },
                Device = new Device
                {
                    DeviceID = deviceId,
                    SupportedInterfaces = new Dictionary<string, object> { ["AudioPlayer"] = new { } }
                }
            }
        };

    /// <summary>
    /// Creates a SkillRequest from a Request, optionally with Context and Session attributes.
    /// Helper for test readability.
    /// </summary>
    public static SkillRequest CreateSkillRequest(
        Request request,
        Context? context = null,
        Dictionary<string, object>? sessionAttributes = null)
    {
        var skillRequest = new SkillRequest
        {
            Request = request,
            Context = context ?? TestHelpers.CreateTestContext()
        };

        if (sessionAttributes != null)
        {
            skillRequest.Session = new AlexaSession { Attributes = sessionAttributes };
        }

        return skillRequest;
    }

    private BaseHandler CreateHandler(Type handlerType)
    {
        ConstructorInfo[] ctors = handlerType.GetConstructors();
        if (ctors.Length == 0)
        {
            throw new InvalidOperationException(
                $"DispatchHarness cannot construct {handlerType.Name}: it has no public constructor.");
        }

        ConstructorInfo ctor = ctors
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        try
        {
            object[] args = ctor.GetParameters()
                .Select(p => ResolveDependency(p.ParameterType))
                .ToArray();

            return (BaseHandler)ctor.Invoke(args);
        }
        catch (Exception ex) when (ex is TargetInvocationException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"DispatchHarness cannot construct {handlerType.Name} (constructor parameter resolution " +
                $"failed; use OverrideDependency for the unresolvable dependency type): {ex.Message}", ex);
        }
    }

    private object ResolveDependency(Type dependencyType)
    {
        if (_dependencyOverrides.TryGetValue(dependencyType, out object? overridden))
        {
            return overridden;
        }

        if (_sharedDependencies.TryGetValue(dependencyType, out object? shared))
        {
            return shared;
        }

        object resolved = dependencyType switch
        {
            _ when dependencyType == typeof(PluginConfiguration) => Config,
            _ when dependencyType == typeof(ILoggerFactory) => LoggerFactory,
            _ when dependencyType == typeof(ISessionManager) => SessionManager.Object,
            _ when dependencyType == typeof(DeviceQueueManager) => new DeviceQueueManager(
                _queueDataDir,
                LoggerFactory.CreateLogger<DeviceQueueManager>()),
            _ when dependencyType.IsInterface || dependencyType.IsAbstract => CreateInterfaceMock(dependencyType),
            _ => throw new NotSupportedException(
                $"DispatchHarness cannot construct dependency {dependencyType.FullName}. " +
                $"Use OverrideDependency<{dependencyType.Name}>() or extend the harness dependency map.")
        };

        _sharedDependencies[dependencyType] = resolved;
        return resolved;
    }

    private object CreateInterfaceMock(Type interfaceType)
    {
        var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(interfaceType))!;

        // By default, Moq returns false/0/null for bools/ints. The warming gates check IsReady
        // and IsDisabled (false by default), which would make a default mock appear "warming".
        // Configure index mocks as ready so default dispatch is NOT warming.
        if (interfaceType == typeof(IArtistIndex))
        {
            ((Mock<IArtistIndex>)mock).SetupGet(i => i.IsReady).Returns(true);
        }
        if (interfaceType == typeof(ISongNgramIndex))
        {
            ((Mock<ISongNgramIndex>)mock).SetupGet(i => i.IsReady).Returns(true);
        }

        return ((Mock)mock).Object;
    }

    public void Dispose()
    {
        if (_sharedDependencies.TryGetValue(typeof(DeviceQueueManager), out object? queueManagerObj))
        {
            if (queueManagerObj is DeviceQueueManager queueManager)
            {
                queueManager.Dispose();
            }
        }

        try
        {
            if (Directory.Exists(_queueDataDir))
            {
                Directory.Delete(_queueDataDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }
}

/// <summary>
/// Result of a routing selection or dispatch operation.
/// </summary>
/// <param name="Handler">The selected handler, or null if no handler claimed the request.</param>
/// <param name="ForceRouted">True if the selection was via the FindSongSessionData force-route.</param>
/// <param name="Response">The handler's response (only present after DispatchAsync).</param>
public sealed record RoutingDecision(BaseHandler? Handler, bool ForceRouted, SkillResponse? Response = null);
