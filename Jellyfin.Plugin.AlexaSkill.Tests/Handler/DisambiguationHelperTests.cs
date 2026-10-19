using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Newtonsoft.Json;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class DisambiguationHelperTests
{
    [Fact]
    public void HasDisambiguationState_NullAttributes_ReturnsFalse()
    {
        Assert.False(DisambiguationHelper.HasDisambiguationState(null));
    }

    [Fact]
    public void HasDisambiguationState_EmptyAttributes_ReturnsFalse()
    {
        var attrs = new Dictionary<string, object>();
        Assert.False(DisambiguationHelper.HasDisambiguationState(attrs));
    }

    [Fact]
    public void HasDisambiguationState_WithValidState_ReturnsTrue()
    {
        var attrs = new Dictionary<string, object>
        {
            ["disambig_matches"] = "[]",
            ["disambig_type"] = "song"
        };
        Assert.True(DisambiguationHelper.HasDisambiguationState(attrs));
    }

    [Fact]
    public void HasDisambiguationState_MissingMatches_ReturnsFalse()
    {
        var attrs = new Dictionary<string, object>
        {
            ["disambig_type"] = "song"
        };
        Assert.False(DisambiguationHelper.HasDisambiguationState(attrs));
    }

    [Fact]
    public void HasDisambiguationState_MissingType_ReturnsFalse()
    {
        var attrs = new Dictionary<string, object>
        {
            ["disambig_matches"] = "[]"
        };
        Assert.False(DisambiguationHelper.HasDisambiguationState(attrs));
    }

    [Fact]
    public void AskFirstMatch_ReturnsAskResponse()
    {
        var matches = new List<(Guid, string)>
        {
            (Guid.NewGuid(), "Test Song"),
            (Guid.NewGuid(), "Other Song")
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US");

        Assert.NotNull(response);
        response.Asks();
        Assert.Contains("Test Song", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public void AskFirstMatch_SetsSessionAttributes()
    {
        var id = Guid.NewGuid();
        var matches = new List<(Guid, string)>
        {
            (id, "Test Song")
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US");

        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("disambig_matches"));
        Assert.Equal(0, response.SessionAttributes["disambig_index"]);
        Assert.Equal("song", response.SessionAttributes["disambig_type"]);

        var storedMatches = JsonConvert.DeserializeObject<List<DisambiguationHelper.MatchInfo>>(
            response.SessionAttributes["disambig_matches"].ToString()!);
        Assert.Single(storedMatches);
        Assert.Equal(id.ToString(), storedMatches[0].Id);
        Assert.Equal("Test Song", storedMatches[0].Name);
    }

    // JF-735: the state truncation at FirstMatchStateCap is DELIBERATE and
    // this pin locks it; the full defense, the enforced caller census, and the
    // JF-743 filing live on that constant's doc. Do not widen the state
    // without re-weighing there. (This 2-tuple overload has zero production
    // callers; every call site uses the ArtUrl overload. It is pinned here
    // and by AskFirstMatch_OriginalOverload_StillWorks below as public
    // helper surface; removing it would be a separate cleanup.)
    [Fact]
    public void AskFirstMatch_TruncatesStateAtThree_DeliberateWeakTailCap()
    {
        var matches = new List<(Guid, string)>
        {
            (Guid.NewGuid(), "Song 1"),
            (Guid.NewGuid(), "Song 2"),
            (Guid.NewGuid(), "Song 3"),
            (Guid.NewGuid(), "Song 4"),
            (Guid.NewGuid(), "Song 5")
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US");

        var storedMatches = DisambiguationHelper.ReadState(response.SessionAttributes)!.Value.Matches;
        Assert.Equal(3, storedMatches.Count);
        Assert.Equal("Song 1", storedMatches[0].Name);
        Assert.Equal("Song 2", storedMatches[1].Name);
        Assert.Equal("Song 3", storedMatches[2].Name);
    }

    // JF-735: the same deliberate cap on the ArtUrl overload, the shape every
    // production caller uses (the caller ROSTER is enforced by
    // AskFirstMatchCallerCensusTests, which pools both overloads' tokens and
    // tracks types, not overload choice); same defense as the tuple
    // overload's pin above.
    [Fact]
    public void AskFirstMatch_WithArtUrls_TruncatesStateAtThree_DeliberateWeakTailCap()
    {
        var matches = new List<(Guid, string, string?)>
        {
            (Guid.NewGuid(), "Song 1", null),
            (Guid.NewGuid(), "Song 2", null),
            (Guid.NewGuid(), "Song 3", null),
            (Guid.NewGuid(), "Song 4", null),
            (Guid.NewGuid(), "Song 5", null)
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US");

        var storedMatches = DisambiguationHelper.ReadState(response.SessionAttributes)!.Value.Matches;
        Assert.Equal(3, storedMatches.Count);
        Assert.Equal("Song 1", storedMatches[0].Name);
        Assert.Equal("Song 2", storedMatches[1].Name);
        Assert.Equal("Song 3", storedMatches[2].Name);
    }

    [Fact]
    public void AskNextMatch_ReturnsAskResponse()
    {
        var matches = new List<DisambiguationHelper.MatchInfo>
        {
            new() { Id = Guid.NewGuid().ToString(), Name = "First" },
            new() { Id = Guid.NewGuid().ToString(), Name = "Second" }
        };

        var response = DisambiguationHelper.AskNextMatch(matches, 1, "song", "en-US");

        Assert.NotNull(response);
        response.Asks();
        Assert.Contains("Second", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public void AskNextMatch_IncrementsIndex()
    {
        var matches = new List<DisambiguationHelper.MatchInfo>
        {
            new() { Id = Guid.NewGuid().ToString(), Name = "First" },
            new() { Id = Guid.NewGuid().ToString(), Name = "Second" },
            new() { Id = Guid.NewGuid().ToString(), Name = "Third" }
        };

        var response = DisambiguationHelper.AskNextMatch(matches, 2, "album", "en-US");

        Assert.NotNull(response.SessionAttributes);
        Assert.Equal(2, response.SessionAttributes["disambig_index"]);
        Assert.Equal("album", response.SessionAttributes["disambig_type"]);
    }

    [Fact]
    public void NoMoreMatches_ReturnsTellResponse()
    {
        var response = DisambiguationHelper.NoMoreMatches("en-US");

        Assert.NotNull(response);
        var speech = response.Tells<PlainTextOutputSpeech>();
        Assert.Contains("no more matches", speech.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadState_ValidAttributes_ReturnsState()
    {
        var id = Guid.NewGuid();
        var matchInfo = new DisambiguationHelper.MatchInfo { Id = id.ToString(), Name = "Test Item" };
        var attrs = new Dictionary<string, object>
        {
            ["disambig_matches"] = JsonConvert.SerializeObject(new List<DisambiguationHelper.MatchInfo> { matchInfo }),
            ["disambig_index"] = 0,
            ["disambig_type"] = "video"
        };

        var result = DisambiguationHelper.ReadState(attrs);

        Assert.NotNull(result);
        Assert.Single(result!.Value.Matches);
        Assert.Equal(id.ToString(), result.Value.Matches[0].Id);
        Assert.Equal("Test Item", result.Value.Matches[0].Name);
        Assert.Equal(0, result.Value.Index);
        Assert.Equal("video", result.Value.MediaType);
    }

    [Fact]
    public void ReadState_NullAttributes_ReturnsNull()
    {
        var result = DisambiguationHelper.ReadState(null);
        Assert.Null(result);
    }

    [Fact]
    public void ReadState_MissingKeys_ReturnsNull()
    {
        var attrs = new Dictionary<string, object>
        {
            ["disambig_index"] = 0
        };

        var result = DisambiguationHelper.ReadState(attrs);
        Assert.Null(result);
    }

    [Fact]
    public void ReadState_WithIndex_ReturnsCorrectIndex()
    {
        var matchInfo = new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = "Item" };
        var attrs = new Dictionary<string, object>
        {
            ["disambig_matches"] = JsonConvert.SerializeObject(new List<DisambiguationHelper.MatchInfo> { matchInfo }),
            ["disambig_index"] = 2,
            ["disambig_type"] = "album"
        };

        var result = DisambiguationHelper.ReadState(attrs);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Value.Index);
        Assert.Equal("album", result.Value.MediaType);
    }

    [Fact]
    public void AskFirstMatch_WithArtUrls_AndAplContext_AttachesCarouselDirective()
    {
        var matches = new List<(Guid, string, string?)>
        {
            (Guid.NewGuid(), "Test Song", "http://example.com/art1.jpg"),
            (Guid.NewGuid(), "Other Song", "http://example.com/art2.jpg")
        };

        var context = TestHelpers.CreateContextWithApl();
        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US", context);

        Assert.NotNull(response);
        response.Asks();
        Assert.Contains("Test Song", TestHelpers.GetSpeechText(response));

        // Verify carousel directive is attached
        Assert.NotEmpty(response.Response.Directives);
        Assert.Contains(response.Response.Directives, d => d is AplRenderDocumentDirective);
    }

    [Fact]
    public void AskFirstMatch_WithArtUrls_NullContext_NoCarouselDirective()
    {
        var matches = new List<(Guid, string, string?)>
        {
            (Guid.NewGuid(), "Test Song", "http://example.com/art1.jpg")
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US", context: null);

        Assert.NotNull(response);
        response.Asks();

        // No carousel when context is null
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void AskFirstMatch_WithArtUrls_NonAplContext_NoCarouselDirective()
    {
        var matches = new List<(Guid, string, string?)>
        {
            (Guid.NewGuid(), "Test Song", "http://example.com/art1.jpg")
        };

        var context = TestHelpers.CreateContextWithoutApl();
        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US", context);

        Assert.NotNull(response);
        response.Asks();

        // No carousel when device does not support APL
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void AskFirstMatch_WithArtUrls_SetsSessionAttributesWithArtUrl()
    {
        var id = Guid.NewGuid();
        var artUrl = "http://example.com/art.jpg";
        var matches = new List<(Guid, string, string?)>
        {
            (id, "Test Song", artUrl)
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US");

        Assert.NotNull(response.SessionAttributes);
        var storedMatches = JsonConvert.DeserializeObject<List<DisambiguationHelper.MatchInfo>>(
            response.SessionAttributes["disambig_matches"].ToString()!);
        Assert.Single(storedMatches);
        Assert.Equal(id.ToString(), storedMatches[0].Id);
        Assert.Equal("Test Song", storedMatches[0].Name);
        Assert.Equal(artUrl, storedMatches[0].ArtUrl);
    }

    [Fact]
    public void AskFirstMatch_OriginalOverload_StillWorks()
    {
        var matches = new List<(Guid, string)>
        {
            (Guid.NewGuid(), "Test Song")
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US");

        Assert.NotNull(response);
        response.Asks();
        Assert.Contains("Test Song", TestHelpers.GetSpeechText(response));

        // No carousel for original overload (no context parameter)
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void AskFirstMatch_WithArtUrls_NullArtUrls_StillAttachesCarousel()
    {
        var matches = new List<(Guid, string, string?)>
        {
            (Guid.NewGuid(), "Test Song", null),
            (Guid.NewGuid(), "Other Song", null)
        };

        var context = TestHelpers.CreateContextWithApl();
        var response = DisambiguationHelper.AskFirstMatch(matches, "song", "en-US", context);

        Assert.NotNull(response);
        // Carousel should still be attached even with null art URLs
        // (items will just display without images)
        Assert.NotEmpty(response.Response.Directives);
    }

    // ========== JF-707: the multi-artist ask caps its SPOKEN list, not its cycling list ==========

    [Fact]
    public void AskMultipleArtists_FiveMatches_SpeaksFirstThree_StoresAllFive()
    {
        // JF-684's shared-first-word catalog synonyms can make one spoken word
        // resolve several same-first-word library artists (the JF-690 ER gate is
        // the unbounded caller), so the ask speaks at most
        // MultipleArtistsSpeakCap names. The cycling state keeps every match:
        // no resolved artist becomes unaskable. The below-cap shape (the pair
        // callers) is pinned end-to-end by the both-in-library legs of the
        // MusicianMultiValueEr suites.
        var matches = CreateArtistMatches(5);

        var response = DisambiguationHelper.AskMultipleArtists(matches, "en-US");

        response.Asks();
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("Artist 1", speech);
        Assert.Contains("Artist 2", speech);
        Assert.Contains("Artist 3", speech);
        Assert.DoesNotContain("Artist 4", speech);
        Assert.DoesNotContain("Artist 5", speech);

        // The FULL list rides the session state in rank order (winner first),
        // ids included: the builder-level pin must hold both halves of the
        // stored contract, not just the names (code-review round 2).
        var state = DisambiguationHelper.ReadState(response.SessionAttributes);
        Assert.NotNull(state);
        Assert.Equal(matches.Select(m => m.Id), state!.Value.Matches.Select(m => m.Id));
        Assert.Equal(matches.Select(m => m.Name), state.Value.Matches.Select(m => m.Name));
        Assert.Equal(0, state.Value.Index);
        Assert.Equal(DisambiguationHelper.MediaTypeArtist, state.Value.MediaType);
    }

    // ========== JF-880: the TOTAL count leads the ask (the JF-729 overflow hint is retired) ==========

    [Theory]
    [InlineData(4)]  // the most common overflow: 1 unspoken
    [InlineData(5)]
    public void AskMultipleArtists_AboveCap_LeadsWithTotalCount(int count)
    {
        // JF-880 (maintainer call 2026-10-13): the speech opens with the TOTAL
        // candidate count ("I found 5 artists: ..."), so the capped name list
        // never reads as an exhaustive found-set; the unspoken remainder is
        // implied by the total, and the old trailing hint is gone entirely.
        var response = DisambiguationHelper.AskMultipleArtists(CreateArtistMatches(count), "en-US");

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains($"I found {count} artists", speech);
        // No absence pin on the retired hint's words: the mechanism is gone
        // from code and locale keys; a bare-word DoesNotContain would
        // false-fail legitimate future rewordings (the retired pin's own
        // discipline).
    }

    [Theory]
    [InlineData(2)]  // the pair-callers shape (JF-420 gate, JF-652 kana ties)
    [InlineData(3)]  // exactly at the cap
    public void AskMultipleArtists_AtOrBelowCap_AlsoLeadsWithTotal(int count)
    {
        // The lead count is unconditional (JF-880): the {1} arg rides the main
        // key at every count, so the below-cap shape differs from the pre-JF-880
        // speech only by the numeral ("multiple" became the count).
        var response = DisambiguationHelper.AskMultipleArtists(CreateArtistMatches(count), "en-US");

        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains($"I found {count} artists", speech);
    }

    /// <summary>Ranked candidate list "Artist 1..N" for the multi-artist ask
    /// pins (the one shape every AskMultipleArtists test builds; JF-729 hoisted
    /// the third hand-rolled copy).</summary>
    private static List<DisambiguationHelper.MatchInfo> CreateArtistMatches(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = $"Artist {i}" })
            .ToList();
    }

    // ========== ResolvePick (candidate-names picker; moved from FindSongIntentHandlerTests, JF-524) ==========

    private static List<string> CreateTestCandidateNames(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => $"Song {i + 1}")
            .ToList();
    }

    [Fact]
    public void ResolvePick_ByNumber_ReturnsCorrectIndex()
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick("1", candidateNames, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_ByNumberTwo_ReturnsIndex1()
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick("2", candidateNames, "en-US");
        Assert.Equal(1, result);
    }

    [Fact]
    public void ResolvePick_ByOrdinalOne_ReturnsIndex0()
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick("one", candidateNames, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_ByOrdinalTwo_ReturnsIndex1()
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick("two", candidateNames, "en-US");
        Assert.Equal(1, result);
    }

    // ========== JF-396: cardinal + ordinal pick words for es/pt/fr/nl ==========

    [Theory]
    [InlineData("dos", 1)]          // es cardinal
    [InlineData("tres", 2)]
    [InlineData("cuatro", 3)]
    [InlineData("segundo", 1)]      // es ordinal
    [InlineData("la tercera", 2)]
    [InlineData("el cuarto", 3)]
    [InlineData("dois", 1)]         // pt cardinal
    [InlineData("três", 2)]
    [InlineData("o terceiro", 2)]   // pt ordinal
    [InlineData("a quarta", 3)]
    [InlineData("deuxième", 1)]     // fr ordinal
    [InlineData("le premier", 0)]
    [InlineData("le troisième", 2)]
    [InlineData("le quatrième", 3)]
    [InlineData("tweede", 1)]       // nl ordinal
    [InlineData("derde", 2)]
    [InlineData("vierde", 3)]
    [InlineData("eerste", 0)]       // nl eerste contains erste, was already matched
    public void ResolvePick_EsPtFrNl_Answers_ResolveCorrectIndex(string input, int expected)
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick(input, candidateNames, "es-ES");
        Assert.Equal(expected, result);
    }

    // Guards: existing en/it behavior unchanged after the refactor
    [Theory]
    [InlineData("the second one", 1)]
    [InlineData("il terzo", 2)]
    [InlineData("zweite", 1)]
    [InlineData("un", 0)]
    [InlineData("quattro", 3)]
    [InlineData("no", null)]        // negative answers are NOT picks (JF-395 exit path)
    [InlineData("banana", null)]    // non-matching word is not a pick
    public void ResolvePick_ExistingWords_Unchanged(string input, int? expected)
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick(input, candidateNames, "en-US");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePick_ByPartialTitle_ReturnsMatchingIndex()
    {
        var candidateNames = new List<string> { "Hey Jude", "Let It Be" };

        var result = DisambiguationHelper.ResolvePick("jude", candidateNames, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_NoMatch_ReturnsNull()
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick("something unrelated", candidateNames, "en-US");
        Assert.Null(result);
    }

    [Fact]
    public void ResolvePick_TheFirstOne_ReturnsIndex0()
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick("the first one", candidateNames, "en-US");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_IlPrimo_ReturnsIndex0()
    {
        var candidateNames = CreateTestCandidateNames(4);
        var result = DisambiguationHelper.ResolvePick("il primo", candidateNames, "it-IT");
        Assert.Equal(0, result);
    }

    [Fact]
    public void ResolvePick_EmptyInput_ReturnsNull()
    {
        var candidateNames = CreateTestCandidateNames(2);
        Assert.Null(DisambiguationHelper.ResolvePick("", candidateNames, "en-US"));
        Assert.Null(DisambiguationHelper.ResolvePick("   ", candidateNames, "en-US"));
    }

    [Fact]
    public void ResolvePick_EmptyCandidates_ReturnsNull()
    {
        var candidateNames = new List<string>();
        Assert.Null(DisambiguationHelper.ResolvePick("1", candidateNames, "en-US"));
    }

    // Review regression: a multi-token answer matching a candidate TITLE must win over
    // the ordinal stem it contains ("Second Chance" is a title, not rank 2).
    [Fact]
    public void ResolvePick_TitleWithOrdinalWord_TitleWinsOverRank()
    {
        var candidateNames = new List<string> { "First Cut Is the Deepest", "Second Chance" };

        // "second chance" must pick the TITLE at index 1 because it is the second
        // candidate, NOT because "second" maps to rank 1: verify with the title at a
        // different position too.
        var result = DisambiguationHelper.ResolvePick("second chance", candidateNames, "en-US");
        Assert.Equal(1, result);

        var reordered = new List<string> { candidateNames[1], candidateNames[0] };
        var result2 = DisambiguationHelper.ResolvePick("second chance", reordered, "en-US");
        Assert.Equal(0, result2); // the title, wherever it sits
    }

    // JF-909: the Italian feminine ordinals and the ASR's digit-ordinal rendering
    // ("la 1a" for the spoken "il primo"; device evidence 2026-10-19, corr 9941f5d3).
    [Theory]
    [InlineData("la prima", 0)]
    [InlineData("il primo", 0)]
    [InlineData("la 1a", 0)]
    [InlineData("il 1o", 0)]
    [InlineData("la 1", 0)]
    [InlineData("la seconda", 1)]
    [InlineData("il secondo", 1)]
    [InlineData("la 2a", 1)]
    [InlineData("la terza", 2)]
    [InlineData("la 3a", 2)]
    [InlineData("la quarta", 3)]
    [InlineData("la 4a", 3)]
    [InlineData("1", 0)]
    [InlineData("2", 1)]
    public void JF909_OrdinalWordAndDigitForms_ResolveAsRanks(string input, int expectedRank)
    {
        var candidates = new List<string> { "The Idiot Kings", "I'm Livin' on Baby Food", "Third Wheel", "Fourth Street" };
        var result = DisambiguationHelper.ResolvePick(input, candidates, "it-IT");
        Assert.Equal(expectedRank, result);
    }

    [Fact]
    public void JF909_TitleContainingPrima_StillMatchesAsTitle()
    {
        // The title-before-ordinal order must hold: a candidate TITLE containing
        // "prima" wins over the rank the stem would otherwise assign.
        var candidates = new List<string> { "Prima Donna", "Other Song" };
        var result = DisambiguationHelper.ResolvePick("prima donna", candidates, "it-IT");
        Assert.Equal(0, result); // the title
    }
}
