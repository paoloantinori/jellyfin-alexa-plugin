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
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Tests for null/empty guards in HandleFuzzyMiss{T}.
/// Verifies the method returns (NotFound, null) instead of throwing NullReferenceException
/// when called with null or empty candidates, null matchExtractor results, etc.
/// </summary>
public class HandleFuzzyMissNullGuardTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly ILoggerFactory _loggerFactory;

    public HandleFuzzyMissNullGuardTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    [Fact]
    public void NullCandidates_ReturnsNotFound()
    {
        var config = new PluginConfiguration();
        var harness = CreateHarness(config);

        var (outcome, response) = harness.CallHandleFuzzyMiss<TestCandidate>(
            query: "anything",
            candidates: null!,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US");

        Assert.Equal("NotFound", outcome);
        Assert.Null(response);
    }

    [Fact]
    public void EmptyCandidates_ReturnsNotFound()
    {
        var config = new PluginConfiguration();
        var harness = CreateHarness(config);

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "anything",
            candidates: new List<TestCandidate>(),
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US");

        Assert.Equal("NotFound", outcome);
        Assert.Null(response);
    }

    [Fact]
    public void ValidCandidates_WithExactMatch_AutoPlays()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("Radiohead", Guid.NewGuid()),
        };

        bool autoPlayCalled = false;
        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return ResponseBuilder.Empty();
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "Radiohead",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "artist",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.True(autoPlayCalled, "autoPlayFunc should be called for exact match");
        Assert.Equal("SuggestionHandled", outcome);
    }

    [Fact]
    public void MatchExtractorReturningNull_DoesNotThrow()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("Daft Punk", Guid.NewGuid()),
        };

        // matchExtractor returns null to simulate the edge case
        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "Daft Punk",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: _ => null!,
            mediaType: "artist",
            locale: "en-US",
            autoPlayFunc: null,
            user: user);

        // Should not throw; falls through to confirm path with empty matches
        Assert.NotNull(response);
        Assert.Equal("SuggestionHandled", outcome);
    }

    private TestableBaseHandler CreateHarness(PluginConfiguration config)
    {
        return new TestableBaseHandler(_sessionManagerMock.Object, config, _loggerFactory);
    }

    private record TestCandidate(string Name, Guid Id);

    /// <summary>
    /// Testable subclass that exposes the protected HandleFuzzyMiss method.
    /// </summary>
    private class TestableBaseHandler : BaseHandler
    {
        public TestableBaseHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(
            Request request, Context context, Entities.User user,
            SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Empty());

        public (string Outcome, SkillResponse? Response) CallHandleFuzzyMiss<T>(
            string query,
            IReadOnlyList<T> candidates,
            Func<T, string> selector,
            Func<T, List<(Guid Id, string Name)>> matchExtractor,
            string mediaType,
            string locale,
            Func<T, SkillResponse>? autoPlayFunc = null,
            Entities.User? user = null)
            where T : class
        {
            var (outcome, response) = HandleFuzzyMiss(query, candidates, selector, matchExtractor, mediaType, locale, autoPlayFunc, user);
            return (outcome.ToString(), response);
        }
    }
}
