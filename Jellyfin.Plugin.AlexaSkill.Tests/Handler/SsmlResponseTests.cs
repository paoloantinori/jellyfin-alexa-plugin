using System;
using System.Xml.Linq;
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

    [Fact]
    public void EscapeXml_AllReservedChars_Escaped()
    {
        Assert.Equal("a&amp;b&lt;c&gt;d&quot;e&apos;f", BaseHandler.EscapeXml("a&b<c>d\"e'f"));
    }

    [Fact]
    public void GetSsml_ReservedCharsInName_AreEscapedForValidSsml()
    {
        // JF-323: a name with SSML-reserved chars must be escaped before interpolation into
        // <speak>, else invalid SSML -> InvalidResponse. Call sites wrap names in EscapeXml.
        string name = "Rock & Roll <Live>";
        string? ssml = BaseHandler.GetSsml("NowPlayingSsml", "en-US", BaseHandler.EscapeXml(name));

        Assert.NotNull(ssml);
        Assert.Contains("Rock &amp; Roll &lt;Live&gt;", ssml);
        Assert.DoesNotContain("Rock & Roll <Live>", ssml);
    }

    [Fact]
    public void BuildOutputSpeech_SsmlPath_EscapesReservedChars()
    {
        // JF-350: the SSML path escapes reserved chars inside BuildOutputSpeech now
        // (callers pass raw names). Output must be well-formed SSML with "&amp;".
        var speech = BaseHandler.BuildOutputSpeech("NowPlayingSsml", "NowPlaying", "en-US", "Rock & Roll");

        var ssml = Assert.IsType<SsmlOutputSpeech>(speech);
        Assert.Contains("Rock &amp; Roll", ssml.Ssml);
        Assert.DoesNotContain("Rock & Roll", ssml.Ssml);
        XDocument.Parse(ssml.Ssml); // throws if the <speak> SSML is not well-formed XML
    }

    [Fact]
    public void BuildOutputSpeech_PlainFallback_KeepsRawAmpersand()
    {
        // JF-350: when the SSML key is missing, the plain-text fallback must keep the RAW
        // arg — the user must hear "&", not the SSML-escaped "&amp;".
        var speech = BaseHandler.BuildOutputSpeech("NonExistentSsmlKey12345", "NowPlaying", "en-US", "Tom & Jerry");

        var plain = Assert.IsType<PlainTextOutputSpeech>(speech);
        Assert.Contains("Tom & Jerry", plain.Text);
        Assert.DoesNotContain("&amp;", plain.Text);
    }
}
