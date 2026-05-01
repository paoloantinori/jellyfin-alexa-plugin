using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Alexa.Locale;

public class ResponseStringsTests
{
    private static readonly string[] AllExpectedKeys = new[]
    {
        "UserNotFound", "MediaNotFound", "AddedToFavorites", "RemovedFromFavorites",
        "NoMediaPlaying", "PlaybackFailed", "SomethingWrong", "CouldNotUnderstand",
        "DidNotCatchVideoTitle", "DidNotCatchChannelName", "NotFoundVideo", "NotFoundChannel",
        "NotFoundSongByArtist", "NotFoundSongByNameAndArtist", "NotFoundSongByName",
        "NotFoundAlbumByArtist", "NotFoundAlbumByNameAndArtist", "NotFoundAlbumByName",
        "NoSongsInAlbum", "NotFoundPlaylist", "PlaylistEmpty", "NoFavoriteItems",
        "NoNewlyAddedItems", "NotFoundArtist", "NoSongsForArtist", "Welcome",
        "WelcomeReprompt", "NowPlaying", "NowPlayingWithPosition", "UnknownMedia",
        "HoursAndMinutes", "MinutesAndSeconds", "SecondsOnly", "PositionOfTotal",
        "TrackByArtist", "TrackByArtistFromAlbum", "SeasonEpisode", "SeriesTitle",
        "TitleWithYear"
    };

    [Fact]
    public void Get_EnUs_ReturnsEnglishString()
    {
        string result = ResponseStrings.Get("NoMediaPlaying", "en-US");
        Assert.Equal("Nothing is currently playing.", result);
    }

    [Fact]
    public void Get_EnUsDefaultLocale_ReturnsEnglishString()
    {
        string result = ResponseStrings.Get("NoMediaPlaying");
        Assert.Equal("Nothing is currently playing.", result);
    }

    [Fact]
    public void Get_ItIt_ReturnsItalianString()
    {
        string result = ResponseStrings.Get("NoMediaPlaying", "it-IT");
        Assert.Equal("Nessun contenuto in riproduzione.", result);
    }

    [Fact]
    public void Get_UnknownLocale_FallsBackToEnUs()
    {
        string result = ResponseStrings.Get("NoMediaPlaying", "fr-FR");
        Assert.Equal("Nothing is currently playing.", result);
    }

    [Fact]
    public void Get_MissingKey_ReturnsKey()
    {
        string result = ResponseStrings.Get("NonExistentKey", "en-US");
        Assert.Equal("NonExistentKey", result);
    }

    [Fact]
    public void Get_WithArgs_EnUs_FormatsCorrectly()
    {
        string result = ResponseStrings.Get("NotFoundVideo", "en-US", "Inception");
        Assert.Equal("Sorry, I couldn't find any video with the title Inception.", result);
    }

    [Fact]
    public void Get_WithArgs_ItIt_FormatsCorrectly()
    {
        string result = ResponseStrings.Get("NotFoundVideo", "it-IT", "Inception");
        Assert.Equal("Spiacente, non ho trovato nessun video con il titolo Inception.", result);
    }

    [Fact]
    public void Get_WithMultipleArgs_FormatsCorrectly()
    {
        string result = ResponseStrings.Get("NotFoundSongByNameAndArtist", "en-US", "Yesterday", "The Beatles");
        Assert.Equal("Sorry, I couldn't find any songs with the name Yesterday by The Beatles.", result);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("it-IT")]
    public void Get_AllKeysPresent(string locale)
    {
        foreach (string key in AllExpectedKeys)
        {
            string value = ResponseStrings.Get(key, locale);
            Assert.NotEqual(key, value);
            Assert.NotEmpty(value);
        }
    }

    [Fact]
    public void Get_ItIt_AndEnUs_ReturnDifferentStrings()
    {
        string enResult = ResponseStrings.Get("Welcome", "en-US");
        string itResult = ResponseStrings.Get("Welcome", "it-IT");
        Assert.NotEqual(enResult, itResult);
    }

    [Fact]
    public void Reset_ClearsCachedData()
    {
        // Load first to populate cache
        ResponseStrings.Get("Welcome", "en-US");

        // Reset
        ResponseStrings.Reset();

        // Should still work after reset (lazy reload)
        string result = ResponseStrings.Get("Welcome", "en-US");
        Assert.Equal("Welcome to Jellyfin Skill, what can I play?", result);
    }
}
