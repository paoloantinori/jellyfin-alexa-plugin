using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Music;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class GenreSimilarityMapTests
{
    [Fact]
    public void GetSimilarGenres_Rock_ReturnsRelatedGenres()
    {
        string[] similar = GenreSimilarityMap.GetSimilarGenres("rock");

        Assert.NotEmpty(similar);
        Assert.Contains("alternative", similar);
        Assert.Contains("indie rock", similar);
        Assert.Contains("classic rock", similar);
    }

    [Fact]
    public void GetSimilarGenres_Jazz_ReturnsRelatedGenres()
    {
        string[] similar = GenreSimilarityMap.GetSimilarGenres("jazz");

        Assert.NotEmpty(similar);
        Assert.Contains("blues", similar);
        Assert.Contains("swing", similar);
    }

    [Fact]
    public void GetSimilarGenres_Electronic_ReturnsRelatedGenres()
    {
        string[] similar = GenreSimilarityMap.GetSimilarGenres("electronic");

        Assert.NotEmpty(similar);
        Assert.Contains("techno", similar);
        Assert.Contains("house", similar);
        Assert.Contains("ambient", similar);
    }

    [Fact]
    public void GetSimilarGenres_HipHop_ReturnsRelatedGenres()
    {
        string[] similar = GenreSimilarityMap.GetSimilarGenres("hip hop");

        Assert.NotEmpty(similar);
        Assert.Contains("rap", similar);
        Assert.Contains("r&b", similar);
    }

    [Fact]
    public void GetSimilarGenres_UnknownGenre_ReturnsEmpty()
    {
        string[] similar = GenreSimilarityMap.GetSimilarGenres("ziggurat");

        Assert.Empty(similar);
    }

    [Fact]
    public void GetSimilarGenres_Null_ReturnsEmpty()
    {
        string[] similar = GenreSimilarityMap.GetSimilarGenres(null!);

        Assert.Empty(similar);
    }

    [Fact]
    public void GetSimilarGenres_EmptyString_ReturnsEmpty()
    {
        string[] similar = GenreSimilarityMap.GetSimilarGenres(string.Empty);

        Assert.Empty(similar);
    }

    [Fact]
    public void GetSimilarGenres_CaseInsensitive_ReturnsResults()
    {
        string[] upper = GenreSimilarityMap.GetSimilarGenres("ROCK");
        string[] lower = GenreSimilarityMap.GetSimilarGenres("rock");
        string[] mixed = GenreSimilarityMap.GetSimilarGenres("Rock");

        Assert.Equal(lower, upper);
        Assert.Equal(lower, mixed);
    }

    [Fact]
    public void HasSimilarGenres_KnownGenre_ReturnsTrue()
    {
        Assert.True(GenreSimilarityMap.HasSimilarGenres("rock"));
        Assert.True(GenreSimilarityMap.HasSimilarGenres("jazz"));
        Assert.True(GenreSimilarityMap.HasSimilarGenres("metal"));
    }

    [Fact]
    public void HasSimilarGenres_UnknownGenre_ReturnsFalse()
    {
        Assert.False(GenreSimilarityMap.HasSimilarGenres("foobar"));
    }

    [Fact]
    public void HasSimilarGenres_Null_ReturnsFalse()
    {
        Assert.False(GenreSimilarityMap.HasSimilarGenres(null!));
    }

    [Fact]
    public void ExpansionThreshold_IsReasonableValue()
    {
        Assert.True(GenreSimilarityMap.ExpansionThreshold > 0);
        Assert.True(GenreSimilarityMap.ExpansionThreshold < 50);
    }

    [Fact]
    public void MaxExpandedResults_IsGreaterThanThreshold()
    {
        Assert.True(GenreSimilarityMap.MaxExpandedResults > GenreSimilarityMap.ExpansionThreshold);
    }

    [Fact]
    public void GetSimilarGenres_AllMappedGenres_ReturnNonEmpty()
    {
        // Verify every entry in the map has at least one similar genre
        string[] allGenres =
        [
            "rock", "jazz", "electronic", "pop", "metal", "classical",
            "hip hop", "country", "blues", "r&b", "reggae", "punk",
            "folk", "latin", "soul", "funk", "disco", "indie",
            "alternative", "dance"
        ];

        foreach (string genre in allGenres)
        {
            string[] similar = GenreSimilarityMap.GetSimilarGenres(genre);
            Assert.NotEmpty(similar);
        }
    }

    [Fact]
    public void GetSimilarGenres_GenreNotInOwnSimilarList()
    {
        // A genre should not list itself as similar
        string[] allGenres =
        [
            "rock", "jazz", "electronic", "pop", "metal", "classical",
            "hip hop", "country", "blues", "r&b", "reggae", "punk",
            "folk", "latin", "soul", "funk", "disco", "indie",
            "alternative", "dance"
        ];

        foreach (string genre in allGenres)
        {
            string[] similar = GenreSimilarityMap.GetSimilarGenres(genre);
            Assert.DoesNotContain(similar, s => string.Equals(s, genre, StringComparison.OrdinalIgnoreCase));
        }
    }
}
