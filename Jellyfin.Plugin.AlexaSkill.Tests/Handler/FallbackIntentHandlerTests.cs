using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

[Collection("Plugin")]
public class FallbackIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<MediaBrowser.Controller.Library.ILibraryManager> _libraryManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public FallbackIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private FallbackIntentHandler CreateHandler()
    {
        return new FallbackIntentHandler(_sessionManagerMock.Object, _config, _loggerFactory, _libraryManagerMock.Object);
    }

    private static Context CreateContext()
    {
        return TestHelpers.CreateTestContext();
    }

    private SessionInfo CreateSession()
    {
        return TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    }

    private static Entities.User CreateUser()
    {
        return TestHelpers.CreateTestUser();
    }

    [Fact]
    public void CanHandle_FallbackIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.AmazonFallback },
            RequestId = "test-req"
        };

        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_RepeatIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.RepeatIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherAmazonIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.NavigateHomeIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_CustomIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "PlaySongIntent" },
            RequestId = "test-req"
        };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_LaunchRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new LaunchRequest { RequestId = "test-req" };

        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_FallbackIntent_ReturnsCouldNotUnderstand()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.AmazonFallback },
            Locale = "en-US",
            RequestId = "test-req"
        };
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    [Fact]
    public async Task HandleAsync_UnsupportedAmazonIntent_ReturnsUnsupportedMessage()
    {
        var handler = CreateHandler();
        var request = new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.RepeatIntent" },
            Locale = "en-US",
            RequestId = "test-req"
        };
        var context = CreateContext();
        var user = CreateUser();
        var session = CreateSession();

        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Response?.OutputSpeech);
    }

    // ========== JF-397: state-aware fallback re-prompts instead of killing the session ==========

    private static IntentRequest FallbackRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = IntentNames.AmazonFallback },
            Locale = "en-US",
            RequestId = "diag-fallback"
        };
    }

    [Fact]
    public async Task Fallback_WithDisambiguationState_RePromptsCurrentMatch()
    {
        var handler = CreateHandler();
        var request = FallbackRequest();
        var attrs = new Dictionary<string, object>
        {
            ["disambig_matches"] = Newtonsoft.Json.JsonConvert.SerializeObject(new[]
            {
                new { id = Guid.NewGuid().ToString(), name = "Pink Floyd", artUrl = (string?)null },
                new { id = Guid.NewGuid().ToString(), name = "Pink", artUrl = (string?)null }
            }),
            ["disambig_index"] = 1,
            ["disambig_type"] = "artist"
        };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), attrs, CancellationToken.None);

        // Open session, re-asking the current (index 1) candidate
        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Pink", speech);
        Assert.DoesNotContain("Pink Floyd", speech);
    }

    [Fact]
    public async Task Fallback_WithPaginationState_RePromptsShowMore()
    {
        var handler = CreateHandler();
        var request = FallbackRequest();
        var attrs = new Dictionary<string, object>
        {
            ["pagination_state"] = Newtonsoft.Json.JsonConvert.SerializeObject(new { type = "Artist", itemIds = new[] { Guid.NewGuid().ToString() }, currentOffset = 0, pageSize = 5 })
        };

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), attrs, CancellationToken.None);

        Assert.False(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        string showMore = Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("ShowMorePrompt", "en-US");
        Assert.Contains(showMore, speech);
    }

    [Fact]
    public async Task Fallback_NoState_StillTellsCouldNotUnderstand()
    {
        // Guard: bare fallback (no conversational state) keeps the existing Tell behavior
        var handler = CreateHandler();
        var request = FallbackRequest();

        SkillResponse response = await handler.HandleAsync(request, CreateContext(), CreateUser(), CreateSession(), null, CancellationToken.None);

        Assert.True(response.Response.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        string couldNot = Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get("CouldNotUnderstand", "en-US");
        Assert.Equal(couldNot, speech);
    }
}
