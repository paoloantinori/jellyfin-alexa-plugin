using System;
using Alexa.NET.Assertions;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class SsmlResponseTests
{
    [Fact]
    public void TellSsml_WrapsInSpeakTags()
    {
        var response = BaseHandler.TellSsml("Hello world");

        var speech = response.Tells<SsmlOutputSpeech>();
        Assert.Equal("<speak>Hello world</speak>", speech.Ssml);
    }

    [Fact]
    public void AskSsml_WithStrings_ReturnsOpenSession()
    {
        var response = BaseHandler.AskSsml("Main prompt", "Reprompt text");

        var mainSpeech = response.Asks<SsmlOutputSpeech>();
        Assert.Equal("<speak>Main prompt</speak>", mainSpeech.Ssml);
        var repromptSpeech = Assert.IsType<SsmlOutputSpeech>(response.Response.Reprompt.OutputSpeech);
        Assert.Equal("<speak>Reprompt text</speak>", repromptSpeech.Ssml);
    }

    [Fact]
    public void AskSsml_WithRepromptObject_ReturnsOpenSession()
    {
        var reprompt = new Reprompt("plain text");
        var response = BaseHandler.AskSsml("SSML prompt", reprompt);

        response.Asks<SsmlOutputSpeech>();
        Assert.IsType<PlainTextOutputSpeech>(response.Response.Reprompt.OutputSpeech);
    }

    [Fact]
    public void GetSsml_ReturnsNull_WhenKeyMissing()
    {
        string? result = BaseHandler.GetSsml("NonExistentKey12345", "en-US");
        Assert.Null(result);
    }

    [Fact]
    public void GetSsml_ReturnsFormattedSsml_WhenKeyExists()
    {
        string? result = BaseHandler.GetSsml("NowPlayingSsml", "en-US", "Test Song");
        Assert.NotNull(result);
        Assert.Contains("Test Song", result);
        Assert.Contains("emphasis", result);
    }

    [Fact]
    public void GetSsml_TrackByArtistSsml_ContainsBreakTag()
    {
        string? result = BaseHandler.GetSsml("TrackByArtistSsml", "en-US", "Song", "Artist");
        Assert.NotNull(result);
        Assert.Contains("Song", result);
        Assert.Contains("Artist", result);
        Assert.Contains("break", result);
    }

    [Fact]
    public void GetSsml_DisambiguatePromptSsml_ContainsEmphasis()
    {
        string? result = BaseHandler.GetSsml("DisambiguatePromptSsml", "en-US", "Track Name");
        Assert.NotNull(result);
        Assert.Contains("Track Name", result);
        Assert.Contains("emphasis", result);
    }

    [Fact]
    public void GetSsml_ItalianLocale_ReturnsSsml()
    {
        string? result = BaseHandler.GetSsml("NowPlayingSsml", "it-IT", "Brano Test");
        Assert.NotNull(result);
        Assert.Contains("Brano Test", result);
        Assert.Contains("emphasis", result);
    }

    [Fact]
    public void GetSsml_FallsBackToEnUs_WhenLocaleMissing()
    {
        // Locale without SSML keys should fall back to en-US
        string? result = BaseHandler.GetSsml("NowPlayingSsml", "ja-JP", "Test");
        Assert.NotNull(result);
        Assert.Contains("Test", result);
    }
}
