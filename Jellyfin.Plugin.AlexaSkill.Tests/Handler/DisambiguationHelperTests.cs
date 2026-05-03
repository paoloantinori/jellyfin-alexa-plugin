using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Newtonsoft.Json;
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
        Assert.NotNull(response.Response);
        Assert.False(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
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
        Assert.False(response.Response.ShouldEndSession);
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
        Assert.True(response.Response.ShouldEndSession);
        Assert.NotNull(response.Response.OutputSpeech);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
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
}
