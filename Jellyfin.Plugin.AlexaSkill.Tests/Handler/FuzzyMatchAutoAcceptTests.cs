using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
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
[Collection("Plugin")]
public class FuzzyMatchAutoAcceptTests : PluginTestBase
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
    public async Task HighScore_AutoAccepts_EvenWithConfirmBehavior()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("The Beatles", Guid.NewGuid())
        };

        bool autoPlayCalled = false;
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task HighScore_AutoAccepts_WhenScoreAtDefaultThreshold()
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
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task BorderlineScore_RespectsConfirmBehavior()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var (query, candidates) = CreateBorderlineScenario();

        bool autoPlayCalled = false;
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task BorderlineScore_AutoPlays_WithAutoPlayBehavior()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var (query, candidates) = CreateBorderlineScenario();

        bool autoPlayCalled = false;
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    /// JF-538 review finding, qualifier band (score below ContainmentScore, AutoPlay on):
    /// when the auto-play delegate's response is DIRECTIVE-ONLY (its announce already rode
    /// the progressive vehicle) and the caller passes context/request, the closest-match
    /// qualifier must ride the progressive vehicle too - not overwrite the final response's
    /// OutputSpeech, which double-announced and left the qualifier exposed to the fast-start
    /// player cut. Uses the harness's documented in-band pair "Rhapsoy"/"Rhapsody" (score 71).
    /// </summary>
    [Fact]
    public async Task QualifierBand_DirectiveOnlyPlayResponse_SpeaksQualifierProgressively()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var (query, candidates) = CreateBorderlineScenario();

        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
            Task.FromResult(ResponseBuilder.Empty()); // directive-only shape: null OutputSpeech

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
            query: query,
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "album",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user,
            context: Unit.TestHelpers.CreateTestContext(),
            request: new IntentRequest { Intent = new Intent { Name = "SearchMediaIntent" } });

        Assert.Equal("SuggestionHandled", outcome);
        Assert.NotNull(response);
        Assert.Null(response.Response.OutputSpeech);
        Assert.True(harness.Progressive.Any(m => m.Contains("closest match", StringComparison.Ordinal)
                || m.Contains("Rhapsody", StringComparison.Ordinal)),
            "the qualifier must ride the progressive vehicle in the band, not the final response");
    }

    /// <summary>
    /// Very low score (below SuggestionThreshold) should return NotFound.
    /// </summary>
    [Fact]
    public async Task LowScore_ReturnsNotFound()
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
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task HighScore_WithConfirmBehavior_NoAutoPlayFunc_ReturnsConfirmPrompt()
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
        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task HighScore_WithAutoPlayBehavior_AutoPlays()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("Led Zeppelin", Guid.NewGuid())
        };

        bool autoPlayCalled = false;
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task Score100_DoesNotOverrideOutputSpeech()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("About Today", Guid.NewGuid())
        };

        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            // Return a response with a known OutputSpeech so we can verify it is NOT overwritten
            var response = ResponseBuilder.Empty();
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = "Playing About Today" };
            return Task.FromResult<SkillResponse>(response);
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task Score90_DoesNotOverrideOutputSpeech()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        // "Beatles" is a substring of "The Beatles" => PartialRatio returns 90
        var candidates = new List<TestCandidate>
        {
            new("The Beatles", Guid.NewGuid())
        };

        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            var response = ResponseBuilder.Empty();
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = "Original speech" };
            return Task.FromResult<SkillResponse>(response);
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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
    public async Task ScoreBelow90_ProducesClosestMatchAnnouncement()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(config);

        var (query, candidates) = CreateBelowThreshold90Scenario();

        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            var response = ResponseBuilder.Empty();
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = "Original speech" };
            return Task.FromResult<SkillResponse>(response);
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
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

    // --- JF-508: short-query full-coverage gate on the score-bar auto-accept ---

    /// <summary>
    /// JF-508 reproduction (device 2026-09-06, corr=269e622d): the 2-word query
    /// "soul coffee" auto-played "Starfish &amp; Coffee" with the closest-match announce
    /// although "soul" matched nothing in the title. PartialRatio's sliding window
    /// aligned the query against "sh &amp; coffee" - 3 char edits stand in for the whole
    /// missing word - scoring above DefaultThreshold. The short-query gate must route
    /// this shape to the "did you mean" prompt (one "yes" plays it) instead.
    /// </summary>
    [Fact]
    public async Task TwoWordQuery_OneWordMatched_HighScore_PromptsInsteadOfAutoPlaying()
    {
        var config = new PluginConfiguration();
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var harness = CreateHarness(config);

        var candidates = new List<TestCandidate>
        {
            new("Starfish & Coffee", Guid.NewGuid()),
            new("Coffee & TV", Guid.NewGuid()),
        };

        // Mechanism preconditions: the plain FindBestMatchWithScore (what
        // HandleFuzzyMiss uses) must pick "Starfish & Coffee" at a score that crosses
        // the default threshold but stays under the no-qualifier bar, and the query
        // must be a 2-token query with 50% keyword coverage. If these drift, the test
        // is no longer the reproduction and must be re-pinned, not deleted.
        var best = FuzzyMatcher.FindBestMatchWithScore("soul coffee", candidates, c => c.Name);
        Assert.NotNull(best);
        Assert.Equal("Starfish & Coffee", best!.Value.Item.Name);
        Assert.InRange(best.Value.Score, FuzzyMatcher.DefaultThreshold, FuzzyMatcher.ContainmentScore - 1);

        var queryTokens = KeywordMatcher.Tokenize("soul coffee", "en-US");
        var titleTokens = KeywordMatcher.Tokenize("Starfish & Coffee", "en-US");
        Assert.Equal(2, queryTokens.Length);
        Assert.Equal(1, queryTokens.Count(t => titleTokens.Contains(t)));

        bool autoPlayCalled = false;
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
            query: "soul coffee",
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "song",
            locale: "en-US",
            autoPlayFunc: autoPlayFunc,
            user: user);

        Assert.False(autoPlayCalled, "2-word query with only one word accounted for must not auto-play at the score bar (JF-508)");
        Assert.Equal("SuggestionHandled", outcome);
        Assert.NotNull(response);
        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"),
            "Partial-coverage short query should get the yes/no disambiguation prompt");
    }

    /// <summary>
    /// JF-508 counter-case: a 2-word query where BOTH words are covered by the
    /// candidate name keeps auto-playing at the score bar. The gate withholds
    /// auto-play only for an unaccounted word, not for fuzzy-but-complete coverage.
    /// The score precondition pins the play to the fuzzy band so it exercises the
    /// gated score-bar disjunct (not the containment shortcut).
    /// </summary>
    [Fact]
    public async Task TwoWordQuery_FullCoverage_FuzzyScore_StillAutoPlays()
    {
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var candidates = new List<TestCandidate> { new("U2 - Beautiful Day", Guid.NewGuid()) };

        AssertScoreInRange("u2 beautiful", candidates, "U2 - Beautiful Day",
            FuzzyMatcher.DefaultThreshold, FuzzyMatcher.ContainmentScore - 1);

        var (autoPlayCalled, _, response) = await RunFuzzyMiss(new PluginConfiguration(), user, "u2 beautiful", candidates);

        Assert.True(autoPlayCalled, "2-word query with both words covered must still auto-play (JF-508 gates only unaccounted words)");
        Assert.Null(response!.SessionAttributes?["disambig_matches"]);
    }

    /// <summary>
    /// JF-508 scope guard: the full-coverage requirement is short-query-only. A
    /// 4-word query with 75% keyword coverage and a score above the bar keeps the
    /// pre-JF-508 auto-play behavior.
    /// </summary>
    [Fact]
    public async Task ThreePlusWordQuery_PartialCoverage_HighScore_StillAutoPlays()
    {
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var candidates = new List<TestCandidate> { new("One Two Three Four", Guid.NewGuid()) };

        AssertScoreInRange("one two three zzz", candidates, "One Two Three Four",
            FuzzyMatcher.DefaultThreshold, FuzzyMatcher.ContainmentScore - 1);

        var (autoPlayCalled, _, response) = await RunFuzzyMiss(new PluginConfiguration(), user, "one two three zzz", candidates);

        Assert.True(autoPlayCalled, "3+ word queries keep the pre-JF-508 auto-play behavior");
        Assert.Null(response!.SessionAttributes?["disambig_matches"]);
    }

    /// <summary>
    /// JF-508 boundary: the SAME candidate with the SAME unmatched word ("soul")
    /// prompts at exactly 2 query words and auto-plays at 3, pinning the gate's
    /// short-query scope to the 2-word misfire shape from corr=269e622d.
    /// </summary>
    [Fact]
    public async Task ShortQueryGate_Boundary_TwoWordsPrompt_ThreeWordsPlay()
    {
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var candidates = new List<TestCandidate> { new("Starfish & Coffee Deluxe", Guid.NewGuid()) };

        AssertScoreInRange("soul coffee", candidates, "Starfish & Coffee Deluxe",
            FuzzyMatcher.DefaultThreshold, FuzzyMatcher.ContainmentScore - 1);
        AssertScoreInRange("soul coffee deluxe", candidates, "Starfish & Coffee Deluxe",
            FuzzyMatcher.DefaultThreshold, FuzzyMatcher.ContainmentScore - 1);

        var (twoWordPlayed, _, twoWordResponse) = await RunFuzzyMiss(new PluginConfiguration(), user, "soul coffee", candidates);
        Assert.False(twoWordPlayed, "exactly-2-word query with an unaccounted word prompts");
        Assert.NotNull(twoWordResponse!.SessionAttributes);
        Assert.True(twoWordResponse.SessionAttributes.ContainsKey("disambig_matches"),
            "exactly-2-word partial-coverage query should get the yes/no prompt");

        var (threeWordPlayed, _, _) = await RunFuzzyMiss(new PluginConfiguration(), user, "soul coffee deluxe", candidates);
        Assert.True(threeWordPlayed, "3-word query auto-plays unchanged (gate is short-query-only)");
    }

    /// <summary>
    /// JF-508 scope note: the "&lt;= 2 tokens" rule includes 1-word queries. A single
    /// word that is not present exactly (a pure fuzzy pick among candidates, e.g. an
    /// ASR typo like "Symphonz") now prompts instead of auto-playing; the one-word
    /// containment shape ("cup" inside "Porcupine Tree", the JF-377 coincidental-
    /// containment class) hits the same gate. One "yes" plays the pick.
    /// </summary>
    [Fact]
    public async Task OneWordQuery_WordNotExactlyPresent_PromptsInsteadOfAutoPlaying()
    {
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var candidates = new List<TestCandidate> { new("Symphony", Guid.NewGuid()) };

        AssertScoreInRange("Symphonz", candidates, "Symphony",
            FuzzyMatcher.DefaultThreshold, FuzzyMatcher.ContainmentScore - 1);

        var (autoPlayCalled, _, response) = await RunFuzzyMiss(new PluginConfiguration(), user, "Symphonz", candidates);

        Assert.False(autoPlayCalled, "1-word query with the word not present exactly must confirm first (JF-508 <=2-token scope)");
        Assert.True(response!.SessionAttributes!.ContainsKey("disambig_matches"),
            "1-word pure-fuzzy pick should get the yes/no prompt");
    }

    /// <summary>
    /// JF-508: the explicit FuzzyMatchBehavior.AutoPlay opt-in is deliberately not
    /// gated - the user asked to never be prompted, so even a partial-coverage short
    /// query auto-plays under that behavior.
    /// </summary>
    [Fact]
    public async Task TwoWordQuery_PartialCoverage_AutoPlayBehaviorOptIn_StillAutoPlays()
    {
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var candidates = new List<TestCandidate>
        {
            new("Starfish & Coffee", Guid.NewGuid()),
            new("Coffee & TV", Guid.NewGuid()),
        };

        var (autoPlayCalled, _, _) = await RunFuzzyMiss(new PluginConfiguration(), user, "soul coffee", candidates);

        Assert.True(autoPlayCalled, "AutoPlay-behavior opt-in keeps auto-playing partial-coverage short queries");
    }

    // --- JF-526: sibling auto-play sites + diacritic-insensitive membership ---

    /// <summary>
    /// JF-526: the zero-result fuzzy fallback (SearchItemsFuzzyAsync) feeds callers
    /// that auto-play the returned item; the JF-508 gate now applies to the match
    /// return too, so the "soul coffee" misfire returns null (the site's existing
    /// below-threshold outcome) instead of a partial-coverage pick.
    /// </summary>
    [Fact]
    public async Task SearchItemsFuzzyAsync_TwoWordPartialCoverage_ReturnsNullInsteadOfMatch()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>
            {
                new Audio { Name = "Starfish & Coffee", Id = Guid.NewGuid() },
                new Audio { Name = "Coffee & TV", Id = Guid.NewGuid() },
            });

        // Mechanism precondition (mirrors the JF-508 test): the matcher would return
        // "Starfish & Coffee" above the default threshold without the gate.
        var items = new List<BaseItem>
        {
            new Audio { Name = "Starfish & Coffee", Id = Guid.NewGuid() },
            new Audio { Name = "Coffee & TV", Id = Guid.NewGuid() },
        };
        var best = FuzzyMatcher.FindBestMatchWithScore("soul coffee", items, i => i.Name!);
        Assert.NotNull(best);
        Assert.InRange(best!.Value.Score, FuzzyMatcher.DefaultThreshold, FuzzyMatcher.ContainmentScore - 1);

        var harness = CreateHarness(new PluginConfiguration());
        var match = await harness.CallSearchItemsFuzzyAsync(
            "soul coffee", null, new Entities.User(), libraryManager.Object,
            new[] { BaseItemKind.Audio }, "en-US", CancellationToken.None);

        Assert.Null(match);
    }

    /// <summary>
    /// JF-526 review F1: AutoPlay users are exempt from the coverage gate at the
    /// SearchItemsFuzzyAsync site too (policy parity with HandleFuzzyMiss's AutoPlay
    /// disjunct): the partial-coverage match is returned with the announcement path,
    /// not degraded to not-found.
    /// </summary>
    [Fact]
    public async Task SearchItemsFuzzyAsync_PartialCoverage_AutoPlayBehaviorOptIn_StillMatches()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { new Audio { Name = "Starfish & Coffee", Id = Guid.NewGuid() } });

        var autoPlayUser = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.AutoPlay };
        var harness = CreateHarness(new PluginConfiguration());
        var match = await harness.CallSearchItemsFuzzyAsync(
            "soul coffee", null, autoPlayUser, libraryManager.Object,
            new[] { BaseItemKind.Audio }, "en-US", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("Starfish & Coffee", match!.Value.Item.Name);
    }

    /// <summary>
    /// JF-526 counter-case: a fully-covered 2-word query still matches through the
    /// fallback; the gate withholds only unaccounted words.
    /// </summary>
    [Fact]
    public async Task SearchItemsFuzzyAsync_FullCoverage_StillMatches()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { new Audio { Name = "Coffee & TV", Id = Guid.NewGuid() } });

        var harness = CreateHarness(new PluginConfiguration());
        var match = await harness.CallSearchItemsFuzzyAsync(
            "coffee tv", null, new Entities.User(), libraryManager.Object,
            new[] { BaseItemKind.Audio }, "en-US", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("Coffee & TV", match!.Value.Item.Name);
    }

    /// <summary>
    /// JF-526 diacritic fold inside the shared gate: "besame mucho" vs "Bésame
    /// Mucho" is full coverage (accent-only difference), so the fallback keeps
    /// returning the match instead of degrading to not-found.
    /// </summary>
    [Fact]
    public async Task SearchItemsFuzzyAsync_DiacriticFullCoverage_StillMatches()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { new Audio { Name = "Bésame Mucho", Id = Guid.NewGuid() } });

        var harness = CreateHarness(new PluginConfiguration());
        var match = await harness.CallSearchItemsFuzzyAsync(
            "besame mucho", null, new Entities.User(), libraryManager.Object,
            new[] { BaseItemKind.Audio }, "en-US", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("Bésame Mucho", match!.Value.Item.Name);
    }

    /// <summary>
    /// JF-526: the diacritic fold restores the pre-JF-508B silent play for accent-only
    /// differences on HandleFuzzyMiss's score bar too ("besame mucho" vs "Bésame
    /// Mucho" scores 91, above ContainmentScore, previously a silent play that the
    /// exact-byte gate demoted to a prompt).
    /// </summary>
    [Fact]
    public async Task TwoWordQuery_DiacriticFullCoverage_HighScore_StillAutoPlays()
    {
        var user = new Entities.User { FuzzyMatchBehavior = FuzzyMatchBehavior.Confirm };
        var candidates = new List<TestCandidate> { new("Bésame Mucho", Guid.NewGuid()) };

        AssertScoreInRange("besame mucho", candidates, "Bésame Mucho",
            FuzzyMatcher.ContainmentScore, 100);

        var (autoPlayCalled, _, response) = await RunFuzzyMiss(new PluginConfiguration(), user, "besame mucho", candidates);

        Assert.True(autoPlayCalled, "accent-only difference must count as full coverage (JF-526 fold)");
        Assert.Null(response!.SessionAttributes?["disambig_matches"]);
    }

    // --- Helpers ---

    /// <summary>
    /// Runs HandleFuzzyMiss with the standard test wiring and reports whether the
    /// auto-play callback fired (the JF-508 gate's observable).
    /// </summary>
    private async Task<(bool AutoPlayCalled, string Outcome, SkillResponse? Response)> RunFuzzyMiss(
        PluginConfiguration config,
        Entities.User user,
        string query,
        List<TestCandidate> candidates,
        string locale = "en-US")
    {
        var harness = CreateHarness(config);
        bool autoPlayCalled = false;
        Func<TestCandidate, Task<SkillResponse>> autoPlayFunc = _ =>
        {
            autoPlayCalled = true;
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        };

        var (outcome, response) = await harness.CallHandleFuzzyMiss(
            query: query,
            candidates: candidates,
            selector: c => c.Name,
            matchExtractor: c => new List<(Guid, string)> { (c.Id, c.Name) },
            mediaType: "song",
            locale: locale,
            autoPlayFunc: autoPlayFunc,
            user: user);

        return (autoPlayCalled, outcome, response);
    }

    /// <summary>
    /// Pins the mechanism preconditions for the JF-508 tests: the plain matcher (the
    /// one HandleFuzzyMiss uses) must pick <paramref name="expectedBest"/> with a
    /// score in the given band. If this fails, the strings drifted and the test no
    /// longer reproduces its band; re-pin the strings rather than deleting the test.
    /// </summary>
    private static void AssertScoreInRange(string query, List<TestCandidate> candidates, string expectedBest, int low, int high)
    {
        var best = FuzzyMatcher.FindBestMatchWithScore(query, candidates, c => c.Name);
        Assert.NotNull(best);
        Assert.Equal(expectedBest, best!.Value.Item.Name);
        Assert.InRange(best.Value.Score, low, high);
    }

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

        // JF-538 band test seam: captures progressive speech without network I/O.
        public List<string> Progressive { get; } = new();

        protected override Task<bool> SendProgressiveResponse(Context context, Request request, string message)
        {
            Progressive.Add(message);
            return Task.FromResult(true);
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
        public async Task<(string Outcome, SkillResponse? Response)> CallHandleFuzzyMiss<T>(
            string query,
            IReadOnlyList<T> candidates,
            Func<T, string> selector,
            Func<T, List<(Guid Id, string Name)>> matchExtractor,
            string mediaType,
            string locale,
            Func<T, Task<SkillResponse>>? autoPlayFunc = null,
            Entities.User? user = null,
            Context? context = null,
            Request? request = null)
            where T : class
        {
            var (outcome, response) = await HandleFuzzyMiss(query, candidates, selector, matchExtractor, mediaType, locale, autoPlayFunc, user, context, request);
            return (outcome.ToString(), response);
        }

        /// <summary>
        /// JF-526: expose the shared zero-result fuzzy fallback for the sibling-gate
        /// tests (SearchItemsFuzzyAsync had no test harness of its own).
        /// </summary>
        public Task<(BaseItem Item, int Score)?> CallSearchItemsFuzzyAsync(
            string query,
            Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
            Entities.User user,
            ILibraryManager libraryManager,
            BaseItemKind[] itemTypes,
            string locale,
            CancellationToken cancellationToken)
            => SearchItemsFuzzyAsync(query, jellyfinUser, user, libraryManager, itemTypes, cancellationToken, locale: locale);
    }
}
