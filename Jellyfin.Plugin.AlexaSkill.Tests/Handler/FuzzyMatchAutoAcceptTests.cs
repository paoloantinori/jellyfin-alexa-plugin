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
/// Tests for the auto-accept behavior in HandleFuzzyMiss.
/// Scores >= DefaultThreshold (60) should auto-accept regardless of FuzzyMatchBehavior.
/// Only borderline scores (SuggestionThreshold..DefaultThreshold) consult the config.
/// </summary>
public class FuzzyMatchAutoAcceptTests
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly ILoggerFactory _loggerFactory;

    // Candidate name chosen so that "symphny" scores in the borderline range [40, 60)
    // via PartialRatio's sliding-window against "symphony".
    // "symphny" (7 chars) vs "symphony" (8 chars): window "symphon" vs "symphny" => distance 2,
    // score = (7-2)*100/7 = 71. Still too high.
    // Try longer words where partial matches degrade faster.
    // "Concerto Grosso in G Minor" - we use "Concerto Gros" (partial truncation with typo)
    // Or simpler: use "Pink Floy" (8 chars) vs "Pink Floyd" (9 chars): distance 1, score (8-1)*100/8 = 87
    // Better: use a query that partially overlaps but has significant edits.
    // "Rhapsoy" (7 chars) vs "Rhapsody" (8 chars): distance 2, score (7-2)*100/7 = 71
    // We need something more mangled. Let's try a very different substring.
    // "Bohemian Rhapsody" vs "Bohemian Rhaps" - contains, so 90.
    //
    // The key insight: PartialRatio uses a sliding window of the shorter string length.
    // For borderline scores, we need the shorter string to have ~40% of chars different.
    // With a 5-char window: 2 differences => (5-2)*100/5 = 60 (exactly threshold).
    // With a 6-char window: 2 differences => (6-2)*100/6 = 66. 3 diffs => (6-3)*100/6 = 50.
    //
    // So we need a query that's short enough (5-7 chars) with 2-3 chars different
    // from the best window in the candidate.
    // Candidate: "Supernatural" (12 chars). Query: "suprnatu" (8 chars).
    // Window "supernatu" (9) vs "suprnatu" (8): shorter="suprnatu", windows in "supernatural":
    //   "supernatu" (9 chars) vs "suprnatu" (8 chars) - different lengths, won't align.
    // Actually PartialRatio uses shorter length as window. So window is 8 chars.
    // Sliding 8-char windows over "supernatural": "supernat", "upernatu", "pernatur", "ernatura", "rnatural"
    // "suprnatu" vs "supernat": s-u-p-r-n-a-t-u vs s-u-p-e-r-n-a-t => distance 3 (r->e, a->r, t->n, u->a, wait let me count)
    // Actually: s=s(0), u=u(0), p=p(0), r!=e(1), n!=r(1), a!=n(1), t!=a(1), u!=t(1) = distance 5. Score = (8-5)*100/8 = 37. Too low.
    //
    // Let's just use "Mtalica" (7 chars) vs "Metallica" (9 chars):
    // Windows: "Metallic" (8) vs "Mtalica" (7): window size 7.
    // Sliding 7-char windows: "Metalli", "etallic", "tallica"
    // "Mtalica" vs "Metalli": M=M, t!=e, a!=t, l!=a, i!=l, c!=l, a!=i => distance 6. Score = (7-6)*100/7 = 14
    // "Mtalica" vs "etallic": M!=e, t=t, a=a, l=l, i=i, c=c, a!=c => wait that's too many
    // Hmm. This is getting complicated. Let me use a totally different approach.
    //
    // FINAL APPROACH: Use the actual FuzzyMatcher at test initialization time to find
    // a suitable query from a pool of candidates with different name lengths.

    public FuzzyMatchAutoAcceptTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    /// <summary>
    /// Exact match (score 100) with Confirm behavior should auto-play, not ask for confirmation.
    /// </summary>
    [Fact]
    public void HighScore_AutoAccepts_EvenWithConfirmBehavior()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("The Beatles", Guid.NewGuid())
        };

        bool autoPlayCalled = false;
        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return ResponseBuilder.Empty();
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "The Beatles", // exact match => score 100
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.True(autoPlayCalled, "autoPlayFunc should be called for high-confidence match even with Confirm behavior");
        Assert.Null(response!.SessionAttributes?["disambig_matches"]);
    }

    /// <summary>
    /// Score at or above DefaultThreshold (60) should auto-accept with Confirm behavior.
    /// </summary>
    [Fact]
    public void HighScore_AutoAccepts_WhenScoreAtDefaultThreshold()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        // "Beatles" is a substring of "The Beatles" => PartialRatio returns 90 >= DefaultThreshold
        var candidates = new List<TestCandidate>
        {
            new("The Beatles", Guid.NewGuid())
        };

        bool autoPlayCalled = false;
        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return ResponseBuilder.Empty();
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "Beatles",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.True(autoPlayCalled, "autoPlayFunc should be called for score >= DefaultThreshold with Confirm behavior");
        Assert.Null(response!.SessionAttributes?["disambig_matches"]);
    }

    /// <summary>
    /// Borderline score (between SuggestionThreshold and DefaultThreshold) with Confirm behavior
    /// should NOT auto-accept. It should return a confirmation prompt instead.
    /// Uses a query/candidate pair verified to produce a borderline score at test time.
    /// </summary>
    [Fact]
    public void BorderlineScore_RespectsConfirmBehavior()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var (query, candidates) = CreateBorderlineScenario();

        bool autoPlayCalled = false;
        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return ResponseBuilder.Empty();
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: query,
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.False(autoPlayCalled, "autoPlayFunc should NOT be called for borderline score with Confirm behavior");
        Assert.NotNull(response);
        // Confirm path sets session attributes for disambiguation
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"),
            "Confirm path should set disambig_matches session attribute");
    }

    /// <summary>
    /// Borderline score with AutoPlay behavior should auto-play the match.
    /// </summary>
    [Fact]
    public void BorderlineScore_AutoPlays_WithAutoPlayBehavior()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var (query, candidates) = CreateBorderlineScenario();

        bool autoPlayCalled = false;
        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return ResponseBuilder.Empty();
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: query,
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.True(autoPlayCalled, "autoPlayFunc should be called for borderline score with AutoPlay behavior");
    }

    /// <summary>
    /// Very low score (below SuggestionThreshold) should return NotFound.
    /// </summary>
    [Fact]
    public void LowScore_ReturnsNotFound()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("Metallica", Guid.NewGuid()),
            new("AC/DC", Guid.NewGuid())
        };

        bool autoPlayCalled = false;
        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return ResponseBuilder.Empty();
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "xyzabc123",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.False(autoPlayCalled, "autoPlayFunc should NOT be called for low score");
        Assert.Equal("NotFound", outcome);
        Assert.Null(response);
    }

    /// <summary>
    /// High score with Confirm behavior but no autoPlayFunc should fall through to confirm prompt.
    /// When autoAccept is true but autoPlayFunc is null, the code skips the auto-play block
    /// and falls through to the confirm prompt path.
    /// </summary>
    [Fact]
    public void HighScore_WithConfirmBehavior_NoAutoPlayFunc_ReturnsConfirmPrompt()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("The Beatles", Guid.NewGuid())
        };

        // Score >= DefaultThreshold, autoAccept = true, but autoPlayFunc is null
        // Falls through to confirm prompt since the auto-play block requires autoPlayFunc != null
        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "The Beatles",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: null,
            user: user);

        Assert.NotNull(response);
        // Confirm path sets session attributes
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"),
            "Should fall through to confirm prompt and set disambig_matches");
        // Confirm path uses Ask (should not end session)
        Assert.False(response.Response.ShouldEndSession);
    }

    /// <summary>
    /// High score with AutoPlay behavior should auto-play when autoPlayFunc is provided.
    /// Sanity check that AutoPlay behavior works at all score levels.
    /// </summary>
    [Fact]
    public void HighScore_WithAutoPlayBehavior_AutoPlays()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("Led Zeppelin", Guid.NewGuid())
        };

        bool autoPlayCalled = false;
        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return ResponseBuilder.Empty();
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "Led Zeppelin",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.True(autoPlayCalled, "autoPlayFunc should be called for exact match with AutoPlay behavior");
        // Verify the response has the announcement speech (not disambiguation session attrs)
        Assert.Null(response!.SessionAttributes?["disambig_matches"]);
    }

    /// <summary>
    /// Exact match (score 100) should NOT produce "closest match" announcement.
    /// The OutputSpeech should remain as-is from the play response.
    /// </summary>
    [Fact]
    public void Score100_DoesNotOverrideOutputSpeech()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("About Today", Guid.NewGuid())
        };

        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            // Return a response with a known OutputSpeech so we can verify it is NOT overwritten
            var response = ResponseBuilder.Empty();
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = "Playing About Today" };
            return response;
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "About Today",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "song",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.NotNull(response);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal("Playing About Today", speech.Text);
        Assert.DoesNotContain("closest match", speech.Text);
    }

    /// <summary>
    /// Score 90 (high-confidence but not exact) should NOT produce "closest match" announcement.
    /// </summary>
    [Fact]
    public void Score90_DoesNotOverrideOutputSpeech()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        // "Beatles" is a substring of "The Beatles" => PartialRatio returns 90
        var candidates = new List<TestCandidate>
        {
            new("The Beatles", Guid.NewGuid())
        };

        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            var response = ResponseBuilder.Empty();
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = "Original speech" };
            return response;
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: "Beatles",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.NotNull(response);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal("Original speech", speech.Text);
        Assert.DoesNotContain("closest match", speech.Text);
    }

    /// <summary>
    /// Score below 90 should still produce "closest match" announcement.
    /// Uses a dynamically discovered query/candidate pair scoring in [DefaultThreshold, 90).
    /// </summary>
    [Fact]
    public void ScoreBelow90_ProducesClosestMatchAnnouncement()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var (query, candidates) = CreateBelowThreshold90Scenario();

        Func<TestCandidate, SkillResponse> autoPlayFunc = _ =>
        {
            var response = ResponseBuilder.Empty();
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = "Original speech" };
            return response;
        };

        var (outcome, response) = harness.CallHandleFuzzyMiss(
            query: query,
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.NotNull(response);
        Assert.NotEqual("Original speech", response.Response.OutputSpeech?.ToString());
        // The announcement may be SSML or plain text depending on locale resources
        string? speechText = response.Response.OutputSpeech switch
        {
            PlainTextOutputSpeech plain => plain.Text,
            SsmlOutputSpeech ssml => ssml.Ssml,
            _ => null,
        };
        Assert.NotNull(speechText);
        Assert.Contains("closest match", speechText, StringComparison.OrdinalIgnoreCase);
    }

    // --- Helpers ---

    /// <summary>
    /// Creates a query/candidate pair that produces a borderline fuzzy score
    /// (between SuggestionThreshold and DefaultThreshold). Uses a brute-force search
    /// over candidate names of various lengths with increasing edit distances.
    /// </summary>
    private static (string Query, List<TestCandidate> Candidates) CreateBorderlineScenario()
    {
        // Pool of candidate names to try. We need a pair where the fuzzy score lands
        // in the [SuggestionThreshold, DefaultThreshold) = [40, 60) range.
        // We'll try multiple candidate names and query mutations until we find one.
        string[] candidateNames =
        [
            "Symphony",
            "Orchestra",
            "Concerto",
            "Sonata",
            "Serenade",
            "Nocturne",
            "Overture",
        ];

        // Mutations to try: progressively more characters replaced/removed
        foreach (string candidate in candidateNames)
        {
            for (int mutations = 1; mutations <= candidate.Length - 2; mutations++)
            {
                // Replace the last N characters with 'z' to create a fuzzy query
                string query = candidate[..^mutations] + new string('z', mutations);

                var candidates = new List<TestCandidate> { new(candidate, Guid.NewGuid()) };
                var scoreResult = FuzzyMatcher.FindBestMatchWithScore(query, candidates, c => c.Name);

                if (scoreResult.HasValue
                    && scoreResult.Value.Score >= FuzzyMatcher.SuggestionThreshold
                    && scoreResult.Value.Score < FuzzyMatcher.DefaultThreshold)
                {
                    return (query, candidates);
                }
            }
        }

        // Fallback: try a known long candidate with heavy mutation
        var fallbackCandidate = new List<TestCandidate> { new("Alicia Keys", Guid.NewGuid()) };
        foreach (string q in new[] { "Alicia Kyz", "Alcia Keys", "Alicia Kys" })
        {
            var s = FuzzyMatcher.FindBestMatchWithScore(q, fallbackCandidate, c => c.Name);
            if (s.HasValue && s.Value.Score >= FuzzyMatcher.SuggestionThreshold && s.Value.Score < FuzzyMatcher.DefaultThreshold)
            {
                return (q, fallbackCandidate);
            }
        }

        throw new Xunit.Sdk.XunitException(
            "Could not find any query/candidate pair producing a borderline score. " +
            "This is a test infrastructure issue, not a code bug.");
    }

    /// <summary>
    /// Creates a query/candidate pair that produces a fuzzy score in [DefaultThreshold, 90).
    /// This tests that the "closest match" announcement is still produced for non-near-exact matches.
    /// Uses character mutations to find a score in the target range.
    /// </summary>
    private static (string Query, List<TestCandidate> Candidates) CreateBelowThreshold90Scenario()
    {
        // Use candidate names where character mutations produce scores between 60 and 89.
        string[] candidateNames =
        [
            "The Beatles",
            "Led Zeppelin",
            "Pink Floyd",
            "Rolling Stones",
            "Alicia Keys",
        ];

        foreach (string candidate in candidateNames)
        {
            // Try replacing characters at different positions with 'z'
            for (int pos = 0; pos < candidate.Length; pos++)
            {
                for (int count = 1; count <= Math.Min(3, candidate.Length - pos); count++)
                {
                    if (candidate[pos] == ' ')
                    {
                        continue; // skip spaces
                    }

                    string query = candidate.Remove(pos, count).Insert(pos, new string('z', count));

                    var candidates = new List<TestCandidate> { new(candidate, Guid.NewGuid()) };
                    var scoreResult = FuzzyMatcher.FindBestMatchWithScore(query, candidates, c => c.Name);

                    if (scoreResult.HasValue
                        && scoreResult.Value.Score >= FuzzyMatcher.DefaultThreshold
                        && scoreResult.Value.Score < FuzzyMatcher.ContainmentScore)
                    {
                        return (query, candidates);
                    }
                }
            }
        }

        throw new Xunit.Sdk.XunitException(
            "Could not find any query/candidate pair producing a score in [DefaultThreshold, ContainmentScore). " +
            "This is a test infrastructure issue, not a code bug.");
    }

    private TestableBaseHandler CreateHarness(PluginConfiguration config)
    {
        return new TestableBaseHandler(_sessionManagerMock.Object, config, _loggerFactory);
    }

    /// <summary>
    /// Test candidate record representing a media item with a name and ID.
    /// </summary>
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

        /// <summary>
        /// Expose HandleFuzzyMiss for direct testing.
        /// Returns the outcome as a string since FuzzyMissOutcome is a protected enum.
        /// </summary>
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
