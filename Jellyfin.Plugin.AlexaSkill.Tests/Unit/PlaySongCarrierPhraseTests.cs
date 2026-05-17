using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class PlaySongCarrierPhraseTests
{
    [Theory]
    [InlineData("la canzone sugar free jazz", "sugar free jazz")]
    [InlineData("canzone sugar free jazz", "sugar free jazz")]
    [InlineData("il brano bohemian rhapsody", "bohemian rhapsody")]
    [InlineData("il pezzo sugar free jazz", "sugar free jazz")]
    [InlineData("la traccia imagine", "imagine")]
    [InlineData("una canzone yesterday", "yesterday")]
    [InlineData("un brano hello", "hello")]
    [InlineData("the song bohemian rhapsody", "bohemian rhapsody")]
    [InlineData("the track imagine", "imagine")]
    [InlineData("that song hey jude", "hey jude")]
    [InlineData("a song called redemption", "redemption")]
    [InlineData("the song called redemption", "redemption")]
    [InlineData("das lied gestern", "gestern")]
    [InlineData("la canción bailando", "bailando")]
    [InlineData("la chanson hier", "hier")]
    [InlineData("het liedje gisteren", "gisteren")]
    [InlineData("a música garota", "garota")]
    [InlineData("sugar free jazz", "sugar free jazz")]
    [InlineData("bohemian rhapsody", "bohemian rhapsody")]
    [InlineData("La Canzone Sugar Free Jazz", "Sugar Free Jazz")]
    public void StripSongCarrierPhrase_RemovesCarrierPhrases(string input, string expected)
    {
        string result = PlaySongIntentHandler.StripSongCarrierPhrase(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void StripSongCarrierPhrase_MatchesDespiteLeadingWhitespace()
    {
        string result = PlaySongIntentHandler.StripSongCarrierPhrase("  la canzone test song");
        Assert.Equal("test song", result);
    }
}
