using System.Collections.Generic;
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
        "NoSongsInAlbum", "NotFoundPlaylist", "PlaylistEmpty", "DidNotCatchPlaylistName", "NoFavoriteItems",
        "NoNewlyAddedItems", "NotFoundArtist", "NoSongsForArtist", "Welcome",
        "WelcomeReprompt", "NowPlaying", "NowPlayingWithPosition", "UnknownMedia",
        "HoursAndMinutes", "MinutesAndSeconds", "SecondsOnly", "PositionOfTotal",
        "TrackByArtist", "TrackByArtistFromAlbum", "SeasonEpisode", "SeriesTitle",
        "TitleWithYear", "SearchingMedia", "DisambiguatePrompt", "DisambiguateNext",
        "NoMoreMatches", "DisambiguateReprompt", "UnexpectedYes"
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
        string result = ResponseStrings.Get("NoMediaPlaying", "zh-CN");
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
    [InlineData("ar-SA")]
    [InlineData("de-DE")]
    [InlineData("en-AU")]
    [InlineData("en-CA")]
    [InlineData("en-GB")]
    [InlineData("en-IN")]
    [InlineData("en-US")]
    [InlineData("es-ES")]
    [InlineData("es-MX")]
    [InlineData("es-US")]
    [InlineData("fr-CA")]
    [InlineData("fr-FR")]
    [InlineData("hi-IN")]
    [InlineData("it-IT")]
    [InlineData("ja-JP")]
    [InlineData("nl-NL")]
    [InlineData("pt-BR")]
    public void Get_AllKeysPresent(string locale)
    {
        foreach (string key in AllExpectedKeys)
        {
            string value = ResponseStrings.Get(key, locale);
            Assert.NotEqual(key, value);
            Assert.NotEmpty(value);
        }
    }

    [Theory]
    [InlineData("en-AU")]
    [InlineData("en-CA")]
    [InlineData("en-GB")]
    [InlineData("en-IN")]
    public void Get_EnglishVariants_MatchEnUs(string locale)
    {
        foreach (string key in AllExpectedKeys)
        {
            Assert.Equal(
                ResponseStrings.Get(key, "en-US"),
                ResponseStrings.Get(key, locale));
        }
    }

    [Fact]
    public void Get_NonEnglishLocales_ReturnDifferentStringsFromEnUs()
    {
        string[] nonEnglishLocales = new[] { "ar-SA", "de-DE", "es-ES", "fr-FR", "hi-IN", "it-IT", "ja-JP", "nl-NL", "pt-BR" };
        foreach (string locale in nonEnglishLocales)
        {
            Assert.NotEqual(
                ResponseStrings.Get("Welcome", "en-US"),
                ResponseStrings.Get("Welcome", locale));
        }
    }

    [Fact]
    public void Reset_ClearsCachedData()
    {
        ResponseStrings.Get("Welcome", "en-US");
        ResponseStrings.Reset();
        string result = ResponseStrings.Get("Welcome", "en-US");
        Assert.Equal("Welcome to Jellyfin Skill, what can I play?", result);
    }

    [Fact]
    public void Get_FallbackToLanguageRoot_ReturnsLanguageRootString()
    {
        // Scenario: "es-MX" missing a key, but "es" (language root) has it
        ResponseStrings.RegisterLocale("es", new Dictionary<string, string> { { "TestKeyRoot", "Valor raiz" } });

        string result = ResponseStrings.Get("TestKeyRoot", "es-MX");
        Assert.Equal("Valor raiz", result);

        // Clean up test locale
        ResponseStrings.Reset();
    }

    [Fact]
    public void Get_FallbackSkipsLanguageRoot_WhenExactMatchExists()
    {
        // Scenario: both "es-MX" and "es" have the key, exact locale wins
        ResponseStrings.RegisterLocale("es", new Dictionary<string, string> { { "TestExactVsRoot", "Root value" } });
        ResponseStrings.RegisterLocale("es-MX", new Dictionary<string, string>
        {
            { "TestExactVsRoot", "Exact value" },
        });

        string result = ResponseStrings.Get("TestExactVsRoot", "es-MX");
        Assert.Equal("Exact value", result);

        ResponseStrings.Reset();
    }

    [Fact]
    public void Get_FallbackChain_ExactToRootToEnUs()
    {
        // Scenario: "pt-BR" doesn't exist at all, no "pt" root, falls to en-US
        ResponseStrings.RegisterLocale("pt-BR", new Dictionary<string, string>());

        string result = ResponseStrings.Get("NoMediaPlaying", "pt-BR");
        Assert.Equal("Nothing is currently playing.", result); // en-US fallback

        ResponseStrings.Reset();
    }

    [Fact]
    public void Get_FallbackChain_SkipsRootWhenRootMissing_FallsToEnUs()
    {
        // Scenario: regional locale exists but is missing key, no language root, en-US has it
        ResponseStrings.RegisterLocale("pt-BR", new Dictionary<string, string>());

        string result = ResponseStrings.Get("NoMediaPlaying", "pt-BR");
        Assert.Equal("Nothing is currently playing.", result);

        ResponseStrings.Reset();
    }

    [Fact]
    public void Get_KeyNotFoundAnywhere_ReturnsKey()
    {
        ResponseStrings.RegisterLocale("xx-YY", new Dictionary<string, string>());

        string result = ResponseStrings.Get("CompletelyMissingKey", "xx-YY");
        Assert.Equal("CompletelyMissingKey", result);

        ResponseStrings.Reset();
    }

    [Fact]
    public void Get_LanguageRootFallback_DoesNotApplyToNonHyphenatedLocale()
    {
        // Scenario: locale "en" (no hyphen) should skip language root step and go to en-US
        ResponseStrings.RegisterLocale("en", new Dictionary<string, string>());

        string result = ResponseStrings.Get("NoMediaPlaying", "en");
        // "en" has no keys, language root is "" (no hyphen), so falls to en-US
        Assert.Equal("Nothing is currently playing.", result);

        ResponseStrings.Reset();
    }
}
