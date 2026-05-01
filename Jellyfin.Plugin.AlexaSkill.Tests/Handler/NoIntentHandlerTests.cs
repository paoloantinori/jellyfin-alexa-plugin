using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class NoIntentHandlerTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;

    public NoIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private NoIntentHandler CreateHandler()
    {
        return new NoIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _loggerFactory);
    }

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static IntentRequest CreateNoIntentRequest()
    {
        return new IntentRequest
        {
            Intent = new Intent { Name = "AMAZON.NoIntent" }
        };
    }

    private static Context CreateContext() => TestHelpers.CreateTestContext();

    private Dictionary<string, object> CreateDisambiguationAttrs(
        List<DisambiguationHelper.MatchInfo> matches,
        int index,
        string type)
    {
        return new Dictionary<string, object>
        {
            ["disambig_matches"] = JsonConvert.SerializeObject(matches),
            ["disambig_index"] = index,
            ["disambig_type"] = type
        };
    }

    [Fact]
    public void CanHandle_NoIntent_ReturnsTrue()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "AMAZON.NoIntent" } };
        Assert.True(handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_OtherIntent_ReturnsFalse()
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = "PlaySongIntent" } };
        Assert.False(handler.CanHandle(request));
    }

    [Fact]
    public async Task HandleAsync_NoSessionAttributes_ReturnsUnexpectedResponse()
    {
        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateNoIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_NoDisambiguationState_ReturnsUnexpectedResponse()
    {
        var handler = CreateHandler();
        var emptyAttrs = new Dictionary<string, object>();

        var response = await handler.HandleAsync(
            CreateNoIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            emptyAttrs,
            CancellationToken.None);

        Assert.NotNull(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("not sure what you", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_HasNextMatch_ReturnsAskResponse()
    {
        var match1 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "First Song" };
        var match2 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Second Song" };
        var attrs = CreateDisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo> { match1, match2 },
            0,
            "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateNoIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        // Should return an Ask (not Tell) with the next match name
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.False(response.Response.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("Second Song", speech.Text);
    }

    [Fact]
    public async Task HandleAsync_NoMoreMatches_ReturnsNoMoreMatches()
    {
        var match1 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Only Song" };
        var attrs = CreateDisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo> { match1 },
            0,
            "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateNoIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        // Should return a Tell with "no more matches"
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("no more matches", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_LastMatch_ReturnsNoMoreMatches()
    {
        var match1 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Song 1" };
        var match2 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Song 2" };
        var match3 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Song 3" };
        var attrs = CreateDisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo> { match1, match2, match3 },
            2,
            "album");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateNoIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        // Index 2 is the last of 3 items; next index 3 is out of bounds
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.True(response.Response.ShouldEndSession);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("no more matches", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_HasNextMatch_UpdatesSessionAttributes()
    {
        var match1 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "First Song" };
        var match2 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Second Song" };
        var match3 = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Third Song" };
        var attrs = CreateDisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo> { match1, match2, match3 },
            0,
            "song");

        var handler = CreateHandler();
        var response = await handler.HandleAsync(
            CreateNoIntentRequest(),
            CreateContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.NotNull(response.SessionAttributes);
        Assert.Equal(1, response.SessionAttributes["disambig_index"]);
        Assert.Equal("song", response.SessionAttributes["disambig_type"]);
    }
}
