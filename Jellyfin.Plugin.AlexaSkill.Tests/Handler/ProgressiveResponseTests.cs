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
        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);
        return ResponseBuilder.Empty();
    }

    public Task InvokeSendProgressiveResponse(Context context, Request request, string message)
    {
        return SendProgressiveResponse(context, request, message);
    }
}

public class ProgressiveResponseTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public ProgressiveResponseTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private TestableHandler CreateHandler() => new(_sessionManagerMock.Object, _config, _loggerFactory);

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public async Task SendProgressiveResponse_DoesNotThrow_WhenApiTokenInvalid()
    {
        var handler = CreateHandler();
        var context = CreateContext();
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
        var context = CreateContext();
        var request = new IntentRequest { RequestId = "test-request-id" };

        SkillResponse response = await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory), CancellationToken.None);

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
        var context = CreateContext();
        var request = new IntentRequest { RequestId = "test-request-id" };

        // First call should succeed
        await handler.InvokeSendProgressiveResponse(context, request, "Searching...");

        // Second call should also succeed (previously threw InvalidOperationException
        // due to shared HttpClient BaseAddress mutation)
        await handler.InvokeSendProgressiveResponse(context, request, "Still searching...");
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
public class ProgressiveResponseIntegrationTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public ProgressiveResponseIntegrationTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _userManagerMock
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));
        _config = new PluginConfiguration { AsrCompoundWordFixEnabled = false };
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Context CreateContext() => TestHelpers.CreateTestContext();

    [Fact]
    public async Task PlaySongHandler_ReturnsAudioResponse_WithProgressiveResponse()
    {
        var song = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Song", Id = Guid.NewGuid() };

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        var handler = new PlaySongIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, Mock.Of<IUserDataManager>(), _loggerFactory);

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

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response.Directives);
        Assert.Single(response.Response.Directives);
        Assert.Equal("AudioPlayer.Play", response.Response.Directives[0].Type);
    }

    [Fact]
    public async Task PlayAlbumHandler_ReturnsNotFound_WhenAlbumMissing_WithProgressiveResponse()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayAlbumIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

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

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task PlayFavoritesHandler_ReturnsNoFavorites_WhenEmpty_WithProgressiveResponse()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayFavoritesIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayFavoritesIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("favorite", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayLastAddedHandler_ReturnsNoItems_WhenEmpty_WithProgressiveResponse()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayLastAddedIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayLastAddedIntent",
                Slots = new Dictionary<string, Slot>()
            }
        };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("newly added", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlayChannelHandler_ReturnsNotFound_WhenChannelMissing_WithProgressiveResponse()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayChannelIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayChannelIntent",
                Slots = new Dictionary<string, Slot> { ["channel"] = new Slot { Value = "Unknown Channel" } }
            }
        };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("couldn't find", speech.Text);
    }

    [Fact]
    public async Task PlayArtistSongsHandler_ReturnsNotFound_WhenArtistMissing_WithProgressiveResponse()
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = new PlayArtistSongsIntentHandler(
            _sessionManagerMock.Object, _config, _libraryManagerMock.Object, _userManagerMock.Object, _userDataManagerMock.Object, _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "PlayArtistSongsIntent",
                Slots = new Dictionary<string, Slot> { ["musician"] = new Slot { Value = "Unknown Artist" } }
            }
        };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);

        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("couldn't find", speech.Text);
    }
}
