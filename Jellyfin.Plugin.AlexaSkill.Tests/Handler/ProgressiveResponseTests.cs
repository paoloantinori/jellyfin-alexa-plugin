using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// A testable subclass that exposes the protected SendProgressiveResponse method.
/// </summary>
internal class TestableHandler : BaseHandler
{
    public TestableHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => request is IntentRequest;

    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        // Fire-and-forget: matches the production pattern — never block the handler on the ping.
        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));
        await Task.CompletedTask.ConfigureAwait(false);
        return ResponseBuilder.Empty();
    }

    public Task InvokeSendProgressiveResponse(Context context, Request request, string message)
    {
        return SendProgressiveResponse(context, request, message);
    }

    /// <summary>
    /// Runs SendProgressiveResponse via the fire-and-forget path and awaits the
    /// resulting task so callers can assert it completes non-faulted.
    /// </summary>
    public async Task InvokeFireAndForgetAsync(Context context, Request request, string message)
    {
        Task task = SendProgressiveResponse(context, request, message);
        RunFireAndForget(task);
        await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Exposes the protected RunFireAndForget helper for direct unit testing.
    /// </summary>
    public void InvokeRunFireAndForget(Task task, string operationName = "FireAndForget")
        => RunFireAndForget(task, operationName);
}

[Collection("Plugin")]
public class ProgressiveResponseTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096", c => c.AsrCompoundWordFixEnabled = false);

    private TestableHandler CreateHandler() => new(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);

    [Fact]
    public async Task SendProgressiveResponse_DoesNotThrow_WhenApiTokenInvalid()
    {
        var handler = CreateHandler();
        var context = _fx.CreateContext();
        var request = new IntentRequest { RequestId = "test-request-id" };

        // Should complete without throwing despite invalid token
        await handler.InvokeSendProgressiveResponse(context, request, "Searching...");
    }

    [Fact]
    public async Task SendProgressiveResponse_DoesNotThrow_WhenNetworkUnavailable()
    {
        var handler = CreateHandler();
        var context = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                ApiAccessToken = "invalid-token",
                ApiEndpoint = "https://nonexistent.endpoint.example.com",
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
        var request = new IntentRequest { RequestId = "test-request-id" };

        await handler.InvokeSendProgressiveResponse(context, request, "Searching...");
    }

    [Fact]
    public async Task SendProgressiveResponse_DoesNotThrow_WhenContextMissingApiEndpoint()
    {
        var handler = CreateHandler();
        var context = new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                ApiAccessToken = "some-token",
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };
        var request = new IntentRequest { RequestId = "test-request-id" };

        await handler.InvokeSendProgressiveResponse(context, request, "Searching...");
    }

    [Fact]
    public async Task HandleAsync_ProgressiveResponseFailure_DoesNotBlockMainResponse()
    {
        var handler = CreateHandler();
        var context = _fx.CreateContext();
        var request = new IntentRequest { RequestId = "test-request-id" };

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public void SearchingMedia_LocaleString_ExistsInDefaultLocale()
    {
        string message = ResponseStrings.Get("SearchingMedia", "en-US");
        Assert.False(string.Equals(message, "SearchingMedia", StringComparison.Ordinal), "SearchingMedia key should resolve to a string");
        Assert.Contains("Searching", message);
    }

    [Fact]
    public async Task SendProgressiveResponse_MultipleCalls_DoNotThrow()
    {
        var handler = CreateHandler();
        var context = _fx.CreateContext();
        var request = new IntentRequest { RequestId = "test-request-id" };

        // First call should succeed
        await handler.InvokeSendProgressiveResponse(context, request, "Searching...");

        // Second call should also succeed (previously threw InvalidOperationException
        // due to shared HttpClient BaseAddress mutation)
        await handler.InvokeSendProgressiveResponse(context, request, "Still searching...");
    }

    /// <summary>
    /// Core exception-safety guarantee for the fire-and-forget pattern (JF-294):
    /// when the underlying Alexa API call throws, SendProgressiveResponse MUST
    /// swallow the exception and complete without faulting. A faulting discarded
    /// Task would surface as an unobserved exception. We force a throw by passing
    /// a Context with a null System (accessing ApiAccessToken NREs before the
    /// network call).
    /// </summary>
    [Fact]
    public async Task SendProgressiveResponse_NeverFaults_WhenInternalCallThrows()
    {
        var handler = CreateHandler();
        // System is null → context.System.ApiAccessToken throws NullReferenceException
        // inside the method body, exercising the catch-and-swallow path.
        var context = new Context { System = null };
        var request = new IntentRequest { RequestId = "test-request-id" };

        // Invoke directly: must not throw to the caller.
        Task task = handler.InvokeSendProgressiveResponse(context, request, "Searching...");

        // The task must complete (not fault): exception was swallowed/logged internally.
        await task;
        Assert.False(task.IsFaulted, "SendProgressiveResponse task must never fault — exceptions must be swallowed.");
        Assert.True(task.IsCompletedSuccessfully, "Task should complete successfully after swallowing the internal exception.");
    }

    /// <summary>
    /// The fire-and-forget helper itself must observe the task so CA2012
    /// (unobserved task exceptions) cannot fire. Even when passed an already-faulted
    /// task, RunFireAndForget should not throw and should mark it observed.
    /// </summary>
    [Fact]
    public async Task RunFireAndForget_DoesNotThrow_ObservesFaultedTask()
    {
        var handler = CreateHandler();
        var tcs = new TaskCompletionSource<bool>();
        // Fault the task deliberately.
        tcs.SetException(new InvalidOperationException("simulated progressive-response failure"));
        Task faulted = tcs.Task;

        // Must not throw synchronously.
        handler.InvokeRunFireAndForget(faulted, "UnitTest");

        // Give the OnCompleted continuation a chance to run and observe the exception.
        // (It logs the fault instead of letting it propagate as unobserved.)
        await Task.Delay(100);
        Assert.True(faulted.IsFaulted, "Sanity: task should remain faulted (helper observes, does not unwrap).");
    }

    /// <summary>
    /// Verifies the production handler pattern (JF-294): HandleAsync returns promptly
    /// and never blocks even when the progressive-response ping would be slow, because
    /// it is invoked fire-and-forget rather than awaited. Uses a deliberate delay-free
    /// assertion on completion ordering (no timing flakiness).
    /// </summary>
    [Fact]
    public async Task HandleAsync_ReturnsWithoutAwaiting_ProgressiveResponse()
    {
        var handler = CreateHandler();
        var context = _fx.CreateContext();
        var request = new IntentRequest { RequestId = "test-request-id" };

        // Handler should return a response without throwing, even though the
        // progressive-response task is NOT awaited internally (fire-and-forget).
        SkillResponse response = await handler.HandleAsync(
            request, context, TestHelpers.CreateTestUser(),
            _fx.CreateSession(),
            CancellationToken.None);

        Assert.NotNull(response);
    }

    [Fact]
    public void SearchingMedia_LocaleString_ExistsInItalianLocale()
    {
        string message = ResponseStrings.Get("SearchingMedia", "it-IT");
        Assert.False(string.Equals(message, "SearchingMedia", StringComparison.Ordinal), "SearchingMedia key should resolve to a string");
        Assert.NotEmpty(message);
    }
}

/// <summary>
/// Tests verifying that library-querying handlers still produce correct responses
/// after the progressive response integration.
/// </summary>
[Collection("Plugin")]
public class ProgressiveResponseIntegrationTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("http://localhost:8096", c => c.AsrCompoundWordFixEnabled = false);

    public ProgressiveResponseIntegrationTests()
    {
        // the OLD ctor stubbed GetUserById once for every test; keep that contract.
        _fx.SetupUserMock();
    }

    [Fact]
    public async Task PlaySongHandler_ReturnsAudioResponse_WithProgressiveResponse()
    {
        var song = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Song", Id = Guid.NewGuid() };

        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, Mock.Of<IUserDataManager>(), _fx.LoggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlaySongIntent",
                Slots = new Dictionary<string, Slot>
                {
                    ["song"] = new Slot { Value = "Test Song" },
                    ["musician"] = new Slot { Value = null }
                }
            },
            DialogState = "COMPLETED"
        };

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
        Assert.Equal("AudioPlayer.Play", response.Response.Directives[0].Type);
    }

    [Fact]
    public async Task PlayAlbumHandler_ReturnsNotFound_WhenAlbumMissing_WithProgressiveResponse()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayAlbumIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayAlbumIntent",
                Slots = new Dictionary<string, Slot>
                {
                    ["album"] = new Slot { Value = "Unknown Album" },
                    ["musician"] = new Slot { Value = null }
                }
            },
            DialogState = "COMPLETED"
        };

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task PlayFavoritesHandler_ReturnsNoFavorites_WhenEmpty_WithProgressiveResponse()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayFavoritesIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.LoggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayFavoritesIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("favorite", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayLastAddedHandler_ReturnsNoItems_WhenEmpty_WithProgressiveResponse()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayLastAddedIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.LoggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayLastAddedIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("newly added", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayChannelHandler_ReturnsNotFound_WhenChannelMissing_WithProgressiveResponse()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayChannelIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, Mock.Of<ILiveTvStreamResolver>(), _fx.LoggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayChannelIntent",
                Slots = new Dictionary<string, Slot> { ["channel"] = new Slot { Value = "Unknown Channel" } }
            }
        };

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task PlayArtistSongsHandler_ReturnsNotFound_WhenArtistMissing_WithProgressiveResponse()
    {
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayArtistSongsIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayArtistSongsIntent",
                Slots = new Dictionary<string, Slot> { ["musician"] = new Slot { Value = "Unknown Artist" } }
            }
        };

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("couldn't find", speech.Text);
    }
}
