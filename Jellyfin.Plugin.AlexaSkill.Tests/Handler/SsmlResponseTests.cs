using System;
using System.Xml.Linq;
using Alexa.NET.Assertions;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Xunit;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

public class SsmlResponseTests
{
    [Fact]
    public void TellSsml_WrapsInSpeakTags()
    {
        var response = SpeechBuilder.TellSsml("Hello world");

        var speech = response.Tells<SsmlOutputSpeech>();
        Assert.Equal("<speak>Hello world</speak>", speech.Ssml);
    }

    [Fact]
    public void AskSsml_WithStrings_ReturnsOpenSession()
    {
        var response = SpeechBuilder.AskSsml("Main prompt", "Reprompt text");

        var mainSpeech = response.Asks<SsmlOutputSpeech>();
        Assert.Equal("<speak>Main prompt</speak>", mainSpeech.Ssml);
        var repromptSpeech = Assert.IsType<SsmlOutputSpeech>(response.Response.Reprompt.OutputSpeech);
        Assert.Equal("<speak>Reprompt text</speak>", repromptSpeech.Ssml);
    }

    [Fact]
    public void AskSsml_WithRepromptObject_ReturnsOpenSession()
    {
        var reprompt = new Reprompt("plain text");
        var response = SpeechBuilder.AskSsml("SSML prompt", reprompt);

        response.Asks<SsmlOutputSpeech>();
        Assert.IsType<PlainTextOutputSpeech>(response.Response.Reprompt.OutputSpeech);
    }

    [Fact]
    public void GetSsml_ReturnsNull_WhenKeyMissing()
    {
        string? result = SpeechBuilder.GetSsml("NonExistentKey12345", "en-US");
        Assert.Null(result);
    }

    [Fact]
    public void GetSsml_ReturnsFormattedSsml_WhenKeyExists()
    {
        string? result = SpeechBuilder.GetSsml("NowPlayingSsml", "en-US", "Test Song");
        Assert.NotNull(result);
        Assert.Contains("Test Song", result);
        Assert.Contains("emphasis", result);
    }

    [Fact]
    public void GetSsml_TrackByArtistSsml_ContainsBreakTag()
    {
        string? result = SpeechBuilder.GetSsml("TrackByArtistSsml", "en-US", "Song", "Artist");
        Assert.NotNull(result);
        Assert.Contains("Song", result);
        Assert.Contains("Artist", result);
        Assert.Contains("break", result);
    }

    [Fact]
    public void GetSsml_DisambiguatePromptSsml_ContainsEmphasis()
    {
        string? result = SpeechBuilder.GetSsml("DisambiguatePromptSsml", "en-US", "Track Name");
        Assert.NotNull(result);
        Assert.Contains("Track Name", result);
        Assert.Contains("emphasis", result);
    }

    [Fact]
    public void GetSsml_ItalianLocale_ReturnsSsml()
    {
        string? result = SpeechBuilder.GetSsml("NowPlayingSsml", "it-IT", "Brano Test");
        Assert.NotNull(result);
        Assert.Contains("Brano Test", result);
        Assert.Contains("emphasis", result);
    }

    [Fact]
    public void GetSsml_FallsBackToEnUs_WhenLocaleMissing()
    {
        // Locale without SSML keys should fall back to en-US
        string? result = SpeechBuilder.GetSsml("NowPlayingSsml", "ja-JP", "Test");
        Assert.NotNull(result);
        Assert.Contains("Test", result);
    }

    [Fact]
    public void EscapeXml_AllReservedChars_Escaped()
    {
        Assert.Equal("a&amp;b&lt;c&gt;d&quot;e&apos;f", SpeechBuilder.EscapeXml("a&b<c>d\"e'f"));
    }

    [Fact]
    public void GetSsml_ReservedCharsInName_AreEscapedForValidSsml()
    {
        // JF-323: a name with SSML-reserved chars must be escaped before interpolation into
        // <speak>, else invalid SSML -> InvalidResponse. Call sites wrap names in EscapeXml.
        string name = "Rock & Roll <Live>";
        string? ssml = SpeechBuilder.GetSsml("NowPlayingSsml", "en-US", SpeechBuilder.EscapeXml(name));

        Assert.NotNull(ssml);
        Assert.Contains("Rock &amp; Roll &lt;Live&gt;", ssml);
        Assert.DoesNotContain("Rock & Roll <Live>", ssml);
    }

    [Fact]
    public void BuildOutputSpeech_SsmlPath_EscapesReservedChars()
    {
        // JF-350: the SSML path escapes reserved chars inside BuildOutputSpeech now
        // (callers pass raw names). Output must be well-formed SSML with "&amp;".
        var speech = SpeechBuilder.BuildOutputSpeech("NowPlayingSsml", "NowPlaying", "en-US", "Rock & Roll");

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
        var speech = SpeechBuilder.BuildOutputSpeech("NonExistentSsmlKey12345", "NowPlaying", "en-US", "Tom & Jerry");

        var plain = Assert.IsType<PlainTextOutputSpeech>(speech);
        Assert.Contains("Tom & Jerry", plain.Text);
        Assert.DoesNotContain("&amp;", plain.Text);
    }

    [Fact]
    public void BuildNowPlayingSpeech_AnnounceOn_ReturnsSpeech()
    {
        Assert.NotNull(SpeechBuilder.BuildNowPlayingSpeech("Test Song", "en-US", announceOn: true));
    }

    [Fact]
    public void BuildNowPlayingSpeech_AnnounceOff_ReturnsNull()
    {
        // When the announce setting is off, the helper suppresses the now-playing announce
        // (returns null -> no OutputSpeech on the launch response).
        Assert.Null(SpeechBuilder.BuildNowPlayingSpeech("Test Song", "en-US", announceOn: false));
    }

    [Fact]
    public void AskLocalized_SsmlKeyExists_ReturnsSsmlPromptWithPlainTextReprompt()
    {
        var response = SpeechBuilder.AskLocalized(
            "ResumePromptSsml", "ResumePrompt", "ResumeReprompt", "en-US", "Rock & Roll");

        Assert.False(response.Response.ShouldEndSession ?? true);
        var speech = Assert.IsType<SsmlOutputSpeech>(response.Response.OutputSpeech);
        // The SSML path escapes reserved chars in string args, exactly once
        // (JF-407 contract): "Rock & Roll" becomes "Rock &amp; Roll", never the
        // raw ampersand and never a double escape.
        Assert.Contains("Rock &amp; Roll", speech.Ssml);
        Assert.DoesNotContain("Rock & Roll", speech.Ssml);
        Assert.StartsWith("<speak>", speech.Ssml);
        Assert.True(System.Xml.Linq.XDocument.Parse(speech.Ssml) != null, "SSML must be well-formed XML");
        var reprompt = Assert.IsType<PlainTextOutputSpeech>(response.Response.Reprompt.OutputSpeech);
        Assert.Equal("Would you like to continue where you left off?", reprompt.Text);
    }

    [Fact]
    public void AskLocalized_MissingSsmlKey_FallsBackToPlainTextWithRawArgs()
    {
        var response = SpeechBuilder.AskLocalized(
            "NonExistentSsmlKey12345", "ResumePrompt", "ResumeReprompt", "en-US", "Tom & Jerry");

        Assert.False(response.Response.ShouldEndSession ?? true);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        // The plaintext path keeps args raw: a real ampersand is spoken, not the entity.
        Assert.Contains("Tom & Jerry", speech.Text);
        Assert.DoesNotContain("&amp;", speech.Text);
        var reprompt = Assert.IsType<PlainTextOutputSpeech>(response.Response.Reprompt.OutputSpeech);
        Assert.Equal("Would you like to continue where you left off?", reprompt.Text);
    }

    [Fact]
    public void TellLocalized_SsmlKeyExists_ReturnsSessionEndingSsmlTell_WithEscapedArgs()
    {
        var response = SpeechBuilder.TellLocalized("NowPlayingSsml", "NowPlaying", "en-US", "Rock & Roll");

        Assert.True(response.Response.ShouldEndSession ?? false);
        var speech = Assert.IsType<SsmlOutputSpeech>(response.Response.OutputSpeech);
        // Same JF-407 contract as AskLocalized: string args are escaped exactly once
        // ("Rock & Roll" becomes "Rock &amp; Roll", never raw and never double-escaped).
        Assert.Contains("Rock &amp; Roll", speech.Ssml);
        Assert.DoesNotContain("Rock & Roll", speech.Ssml);
        Assert.StartsWith("<speak>", speech.Ssml);
        XDocument.Parse(speech.Ssml); // throws if the <speak> SSML is not well-formed XML
    }

    [Fact]
    public void TellLocalized_MissingSsmlKey_FallsBackToPlainTextWithRawArgs()
    {
        var response = SpeechBuilder.TellLocalized(
            "NonExistentSsmlKey12345", "NowPlaying", "en-US", "Tom & Jerry");

        Assert.True(response.Response.ShouldEndSession ?? false);
        var speech = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Contains("Tom & Jerry", speech.Text);
        Assert.DoesNotContain("&amp;", speech.Text);
    }

    [Fact]
    public void BuildOutputSpeech_ByteIdenticalToPreMigrationHandRolledShape()
    {
        // JF-315 batch 3 migration proof: the five sites that used to call
        // GetSsml(key, locale, EscapeXml(argN)...) with a hand-rolled <speak> wrap
        // and a raw-args plain fallback (PlayBook resume announce x2, FollowMe,
        // Recommend movie announce, the HandleFuzzyMiss qualifier) produce
        // byte-identical speech through BuildOutputSpeech, on BOTH branches.
        var migratedSsmlPath = SpeechBuilder.BuildOutputSpeech(
            "ResumingBookSsml", "ResumingBook", "en-US", "Tom & Jerry", "Chapter 1");
        string? ssml = SpeechBuilder.GetSsml(
            "ResumingBookSsml", "en-US", SpeechBuilder.EscapeXml("Tom & Jerry"), SpeechBuilder.EscapeXml("Chapter 1"));
        var handRolledSsmlPath = new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" };
        Assert.Equal(handRolledSsmlPath.Ssml, Assert.IsType<SsmlOutputSpeech>(migratedSsmlPath).Ssml);

        var migratedPlainPath = SpeechBuilder.BuildOutputSpeech(
            "NonExistentSsmlKey12345", "ResumingBook", "en-US", "Tom & Jerry", "Chapter 1");
        var handRolledPlainPath = new PlainTextOutputSpeech
        {
            Text = ResponseStrings.Get("ResumingBook", "en-US", "Tom & Jerry", "Chapter 1")
        };
        Assert.Equal(handRolledPlainPath.Text, Assert.IsType<PlainTextOutputSpeech>(migratedPlainPath).Text);
    }
}
