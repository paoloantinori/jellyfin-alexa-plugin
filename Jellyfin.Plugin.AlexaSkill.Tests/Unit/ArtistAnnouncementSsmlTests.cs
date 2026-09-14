#nullable enable
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// The <w role='amazon:musicArtist'> SSML trick (stolen 2026-09-14 from an Amazon
/// Music first-party TTS capture in the developer console): artist names spoken in
/// announcements carry the music-artist role for entity-aware pronunciation. The
/// role is NOT in the public custom-skill SSML reference; acceptance was validated
/// in the console TTS pipeline. These tests pin the string contract: every {0}
/// occurrence in the localized template is wrapped, the artist name is XML-escaped
/// inside the role tag, and ApplyAnnouncement routes speak-prefixed announcements
/// to SsmlOutputSpeech while keeping plain-text announcements untouched.
/// </summary>
public class ArtistAnnouncementSsmlTests
{
    [Fact]
    public void WrapsEveryArtistOccurrence_WithMusicArtistRole()
    {
        string ssml = BaseHandler.BuildArtistAnnouncementSsml("FoundArtistInstead", "it-IT", "Koop");

        Assert.Equal(
            "<speak>Ho trovato l'artista <w role='amazon:musicArtist'>Koop</w>. Ecco la musica di <w role='amazon:musicArtist'>Koop</w>.</speak>",
            ssml);
    }

    [Fact]
    public void EscapesSsmlReservedChars_InTheArtistName()
    {
        string ssml = BaseHandler.BuildArtistAnnouncementSsml("FoundArtistInstead", "it-IT", "A&B <X>");

        Assert.Contains("<w role='amazon:musicArtist'>A&amp;B &lt;X&gt;</w>", ssml);
        Assert.DoesNotContain("A&B", ssml);
    }

    [Fact]
    public void ApplyAnnouncement_RoutesSpeakPrefixedString_ToSsmlOutputSpeech()
    {
        var response = new SkillResponse { Response = new ResponseBody() };

        BaseHandler.ApplyAnnouncement(response, "<speak>ciao</speak>");

        var ssml = Assert.IsType<SsmlOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal("<speak>ciao</speak>", ssml.Ssml);
    }

    [Fact]
    public void WrapArtistInSentence_EscapesSentence_AndWrapsTheName()
    {
        string ssml = BaseHandler.WrapArtistInSentence("Questo è di A&B.", "A&B");

        Assert.Equal("Questo è di <w role='amazon:musicArtist'>A&amp;B</w>.", ssml);
    }

    [Fact]
    public void WrapArtistInSentence_NameAbsent_IsAHelpfulNoOp()
    {
        string ssml = BaseHandler.WrapArtistInSentence("Nessun nome qui.", "Koop");

        Assert.Equal("Nessun nome qui.", ssml);
    }

    [Fact]
    public void ApplyAnnouncement_KeepsPlainTextRouting()
    {
        var response = new SkillResponse { Response = new ResponseBody() };

        BaseHandler.ApplyAnnouncement(response, "plain announcement");

        var plain = Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        Assert.Equal("plain announcement", plain.Text);
    }
}
