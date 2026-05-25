using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using User = Jellyfin.Plugin.AlexaSkill.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for per-user fuzzy match configuration (FuzzyMatchBehavior and FuzzyMatchThreshold).
/// Uses a test handler that exposes the protected FuzzyMatch and HandleFuzzyMiss methods.
/// </summary>
[Collection("Plugin")]
public class FuzzyMatchConfigurationTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TestFuzzyHandler _handler;

    public FuzzyMatchConfigurationTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _config = new PluginConfiguration();
        _loggerFactory = LoggerFactory.Create(b => { });
        _handler = new TestFuzzyHandler(_sessionManagerMock.Object, _config, _loggerFactory);
        TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            cfg => { },
            "alexa-fuzzy-match-test");
    }

    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    // --- FuzzyMatch threshold tests ---

    [Fact]
    public void FuzzyMatch_ReturnsMatch_WhenScoreAboveUserThreshold()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Name = "Abbey Road" },
        };

        // "Abby Road" is a close typo of "Abbey Road" — should score well above 50
        var user = new User { FuzzyMatchThreshold = 50 };
        var result = _handler.TestFuzzyMatch("Abby Road", candidates, c => c.Name, user);

        Assert.NotNull(result);
        Assert.Equal("Abbey Road", result.Name);
    }

    [Fact]
    public void FuzzyMatch_ReturnsNull_WhenScoreBelowUserThreshold()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Name = "Abbey Road" },
        };

        // Set a very high threshold so even a close match is rejected
        var user = new User { FuzzyMatchThreshold = 99 };
        var result = _handler.TestFuzzyMatch("Abby Road", candidates, c => c.Name, user);

        Assert.Null(result);
    }

    [Fact]
    public void FuzzyMatch_UsesDefaultThreshold_WhenUserIsNull()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Name = "Abbey Road" },
        };

        // Null user should use default threshold of 60
        var result = _handler.TestFuzzyMatch("Abby Road", candidates, c => c.Name, null);

        Assert.NotNull(result);
        Assert.Equal("Abbey Road", result.Name);
    }

    [Fact]
    public void FuzzyMatch_LowThreshold_AcceptsPoorMatches()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Name = "The Dark Side of the Moon" },
        };

        // "Dark Side" is a partial match — will have a moderate score
        var user = new User { FuzzyMatchThreshold = 20 };
        var result = _handler.TestFuzzyMatch("Dark Side", candidates, c => c.Name, user);

        Assert.NotNull(result);
    }

    [Fact]
    public void FuzzyMatch_HighThreshold_RejectsGoodMatches()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Name = "Abbey Road" },
        };

        // Even a very close match should be rejected with threshold 100 (only exact matches)
        var user = new User { FuzzyMatchThreshold = 100 };
        var result = _handler.TestFuzzyMatch("Abby Road", candidates, c => c.Name, user);

        Assert.Null(result);
    }

    [Fact]
    public void FuzzyMatch_ExactMatch_AlwaysAccepted_EvenWithHighThreshold()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Name = "Abbey Road" },
        };

        var user = new User { FuzzyMatchThreshold = 100 };
        var result = _handler.TestFuzzyMatch("Abbey Road", candidates, c => c.Name, user);

        Assert.NotNull(result);
        Assert.Equal("Abbey Road", result.Name);
    }

    // --- HandleFuzzyMiss behavior tests ---

    [Fact]
    public void HandleFuzzyMiss_AutoPlay_ReturnsPlayResponse_WhenCloseMatch()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Id = Guid.NewGuid(), Name = "Abbey Road" },
        };

        var user = new User
        {
            FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay,
            FuzzyMatchThreshold = 60,
            FuzzySuggestionThreshold = 40,
        };

        SkillResponse autoPlayResponse = ResponseBuilder.Tell("Playing Abbey Road");

        var (outcome, response) = _handler.TestHandleFuzzyMiss(
            "Abby Road",
            candidates,
            c => c.Name,
            best => new List<(Guid, string)> { (best.Id, best.Name) },
            "album",
            "en-US",
            best => autoPlayResponse,
            user);

        Assert.True(outcome);
        Assert.NotNull(response);
        // Should end session — it's a play response, not a question
        Assert.True(response.Response.ShouldEndSession);
    }

    [Fact]
    public void HandleFuzzyMiss_AutoPlay_AnnouncementIncludesMatchInfo()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Id = Guid.NewGuid(), Name = "Abbey Road" },
        };

        var user = new User
        {
            FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay,
            FuzzyMatchThreshold = 60,
            FuzzySuggestionThreshold = 40,
        };

        SkillResponse autoPlayResponse = ResponseBuilder.Tell("Playing Abbey Road");

        var (outcome, response) = _handler.TestHandleFuzzyMiss(
            "Abby Road",
            candidates,
            c => c.Name,
            best => new List<(Guid, string)> { (best.Id, best.Name) },
            "album",
            "en-US",
            best => autoPlayResponse,
            user);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Abbey Road", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleFuzzyMiss_Confirm_ReturnsDisambiguationPrompt_WhenCloseMatch()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Id = Guid.NewGuid(), Name = "Abbey Road" },
        };

        var user = new User
        {
            FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm,
            FuzzyMatchThreshold = 80, // High threshold so "Abby Road" < 80 => fuzzy miss
            FuzzySuggestionThreshold = 40,
        };

        var (outcome, response) = _handler.TestHandleFuzzyMiss(
            "Abby Road",
            candidates,
            c => c.Name,
            best => new List<(Guid, string)> { (best.Id, best.Name) },
            "album",
            "en-US",
            best => ResponseBuilder.Tell("Playing " + best.Name),
            user);

        Assert.True(outcome);
        Assert.NotNull(response);
        // Confirm mode should NOT end session (waiting for yes/no)
        Assert.False(response.Response.ShouldEndSession);

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Abbey Road", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleFuzzyMiss_Confirm_PromptContainsDidYouMeanOrSimilar()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Id = Guid.NewGuid(), Name = "Abbey Road" },
        };

        var user = new User
        {
            FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm,
            FuzzyMatchThreshold = 80,
            FuzzySuggestionThreshold = 40,
        };

        var (outcome, response) = _handler.TestHandleFuzzyMiss(
            "Abby Road",
            candidates,
            c => c.Name,
            best => new List<(Guid, string)> { (best.Id, best.Name) },
            "album",
            "en-US",
            best => ResponseBuilder.Tell("Playing " + best.Name),
            user);

        Assert.NotNull(response);
        string speech = TestHelpers.GetSpeechText(response);
        // The locale string is "I didn't find '{0}'. Did you mean '{1}'?"
        Assert.Contains("mean", speech, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleFuzzyMiss_Confirm_SetsDisambiguationSessionAttributes()
    {
        var candidateId = Guid.NewGuid();
        var candidates = new List<TestCandidate>
        {
            new() { Id = candidateId, Name = "Abbey Road" },
        };

        var user = new User
        {
            FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm,
            FuzzyMatchThreshold = 80,
            FuzzySuggestionThreshold = 40,
        };

        var (outcome, response) = _handler.TestHandleFuzzyMiss(
            "Abby Road",
            candidates,
            c => c.Name,
            best => new List<(Guid, string)> { (best.Id, best.Name) },
            "album",
            "en-US",
            best => ResponseBuilder.Tell("Playing " + best.Name),
            user);

        Assert.NotNull(response);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
        Assert.True(response.SessionAttributes.ContainsKey("disambig_index"));
        Assert.True(response.SessionAttributes.ContainsKey("disambig_type"));
        Assert.Equal("album", response.SessionAttributes["disambig_type"]);
    }

    [Fact]
    public void HandleFuzzyMiss_ReturnsNotFound_WhenBelowSuggestionThreshold()
    {
        var candidates = new List<TestCandidate>
        {
            new() { Id = Guid.NewGuid(), Name = "Completely Different Album Title" },
        };

        var user = new User
        {
            FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay,
            FuzzyMatchThreshold = 60,
            FuzzySuggestionThreshold = 80, // Very high suggestion threshold
        };

        var (outcome, response) = _handler.TestHandleFuzzyMiss(
            "XYZ",
            candidates,
            c => c.Name,
            best => new List<(Guid, string)> { (best.Id, best.Name) },
            "album",
            "en-US",
            best => ResponseBuilder.Tell("Playing " + best.Name),
            user);

        Assert.False(outcome);
        Assert.Null(response);
    }

    // --- Threshold boundary tests ---

    [Fact]
    public void FuzzyMatch_DefaultUserThreshold_IsSixty()
    {
        var user = new User();
        Assert.Equal(60, user.FuzzyMatchThreshold);
    }

    [Fact]
    public void FuzzyMatch_DefaultSuggestionThreshold_IsForty()
    {
        var user = new User();
        Assert.Equal(40, user.FuzzySuggestionThreshold);
    }

    [Fact]
    public void FuzzyMatch_DefaultBehavior_IsConfirm()
    {
        var user = new User();
        Assert.Equal(FuzzyMatchBehavior.Confirm, user.FuzzyMatchBehavior);
    }

    [Fact]
    public void FuzzyMatcher_GetDefaultThreshold_UsesUserValue()
    {
        var user = new User { FuzzyMatchThreshold = 75 };
        Assert.Equal(75, FuzzyMatcher.GetDefaultThreshold(user));
    }

    [Fact]
    public void FuzzyMatcher_GetDefaultThreshold_FallsBackToConstant_WhenUserIsNull()
    {
        Assert.Equal(FuzzyMatcher.DefaultThreshold, FuzzyMatcher.GetDefaultThreshold(null));
    }

    [Fact]
    public void FuzzyMatcher_GetSuggestionThreshold_UsesUserValue()
    {
        var user = new User { FuzzySuggestionThreshold = 50 };
        Assert.Equal(50, FuzzyMatcher.GetSuggestionThreshold(user));
    }

    [Fact]
    public void FuzzyMatcher_GetSuggestionThreshold_FallsBackToConstant_WhenUserIsNull()
    {
        Assert.Equal(FuzzyMatcher.SuggestionThreshold, FuzzyMatcher.GetSuggestionThreshold(null));
    }

    // --- Test helper types ---

    /// <summary>
    /// Simple test candidate for fuzzy matching tests.
    /// </summary>
    private class TestCandidate
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Minimal concrete handler to expose protected FuzzyMatch and HandleFuzzyMiss for testing.
    /// Maps FuzzyMissOutcome to bool for accessibility: true=SuggestionHandled, false=NotFound.
    /// </summary>
    private class TestFuzzyHandler : BaseHandler
    {
        public TestFuzzyHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory) { }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, User user, SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Tell("test"));

        public TestCandidate? TestFuzzyMatch(string query, IEnumerable<TestCandidate> candidates, Func<TestCandidate, string> selector, User? user, int threshold = -1)
            => FuzzyMatch(query, candidates, selector, user, threshold);

        /// <summary>
        /// Exposes HandleFuzzyMiss for testing. Returns (handled: true=SuggestionHandled, false=NotFound, response).
        /// </summary>
        public (bool Handled, SkillResponse? Response) TestHandleFuzzyMiss(
            string query,
            IReadOnlyList<TestCandidate> candidates,
            Func<TestCandidate, string> selector,
            Func<TestCandidate, List<(Guid Id, string Name)>> matchExtractor,
            string mediaType,
            string locale,
            Func<TestCandidate, SkillResponse>? autoPlayFunc = null,
            User? user = null)
        {
            var (outcome, response) = HandleFuzzyMiss(query, candidates, selector, matchExtractor, mediaType, locale, autoPlayFunc, user);
            return (outcome == FuzzyMissOutcome.SuggestionHandled, response);
        }
    }
}
