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

    [Fact]
    public void AskFirstMatch_LimitsToThreeMatches()
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

        var storedMatches = JsonConvert.DeserializeObject<List<DisambiguationHelper.MatchInfo>>(
            response.SessionAttributes["disambig_matches"].ToString()!);
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
}
