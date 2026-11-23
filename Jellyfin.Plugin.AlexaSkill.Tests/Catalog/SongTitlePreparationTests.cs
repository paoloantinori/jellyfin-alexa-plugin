#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Catalog;

/// <summary>
/// JF-946: the pure song-title preparation rules, pinned in isolation from
/// the sync pipeline. The rules are the Fable-reviewed plan decisions
/// (grounded on the household library's shape: 7.4% of titles carry
/// parenthetical or " - " suffixes nobody speaks, 37 tracks are literally
/// named "no title", 1,942 titles are single words).
/// </summary>
public class SongTitlePreparationTests
{
    [Theory]
    [InlineData("Song", "Song")]
    [InlineData("Song (G Corp Mix)", "Song")]
    [InlineData("Song (Remix) (Live)", "Song")]
    [InlineData("Title - Remastered", "Title")]
    [InlineData("Title - Remastered - 2011", "Title")]
    [InlineData("Left (Right) Song", "Left (Right) Song")] // mid-title parens stay
    [InlineData("A - B", "A")] // whole-title " - " shape: the plan's blunt suffix rule wins; the dedup accepts the rare collision
    [InlineData("  Padded  ", "Padded")]
    [InlineData("(Whole)", "")] // everything strips: junk downstream
    public void StripTitle_StripsTrailingSuffixes(string input, string expected)
    {
        Assert.Equal(expected, SongTitlePreparation.StripTitle(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("42")]
    [InlineData("007")]
    [InlineData("no title")]
    [InlineData("No Title")]
    [InlineData("UNTITLED")]
    [InlineData("Unknown")]
    public void IsJunkTitle_ExcludesTheJunkClass(string title)
    {
        Assert.True(SongTitlePreparation.IsJunkTitle(title));
    }

    [Theory]
    [InlineData("Rapsodia")]
    [InlineData("Suona Il Blues")]
    [InlineData("42nd Street")]
    public void IsJunkTitle_KeepsRealTitles(string title)
    {
        Assert.False(SongTitlePreparation.IsJunkTitle(title));
    }

    [Theory]
    [InlineData("Rapsodia", true)]
    [InlineData("Suona il blues", true)]
    [InlineData("ab cd", false)]
    [InlineData("O", false)]
    public void HasQualifyingWord_FourLetterMinimum(string title, bool expected)
    {
        Assert.Equal(expected, SongTitlePreparation.HasQualifyingWord(title));
    }

    [Fact]
    public void SongSynonyms_ValuesOnly_AlwaysEmpty()
    {
        // The size-cap policy (live 2026-11-23): Amazon's 1.5MB effective
        // per-model limit counts the catalog values, and 9,458 titles with
        // per-locale phonetic variants blew it (it-IT 2.0MB). Songs upload
        // VALUES-ONLY in every locale; selection anchoring is exact-match and
        // ASR drift stays on the handler-side fuzzy chain. If variants are
        // ever reintroduced, this pin is the place that fails loudly.
        Assert.Empty(SongTitlePreparation.SongSynonyms("Crash Into Me", "it-IT"));
        Assert.Empty(SongTitlePreparation.SongSynonyms("ab cd", "it-IT"));
    }

    [Fact]
    public void Prepare_OrdersByPlayCountThenRecency_DedupsOnStrippedForm()
    {
        var candidates = new[]
        {
            (Guid.NewGuid(), "Rapsodia (Remaster)", 0, new DateTime(2020, 1, 1)),
            (Guid.NewGuid(), "Rapsodia", 5, new DateTime(2019, 1, 1)),
            (Guid.NewGuid(), "No Title", 99, new DateTime(2024, 1, 1)),
            (Guid.NewGuid(), "Zebra", 0, new DateTime(2024, 6, 1)),
            (Guid.NewGuid(), "Anchor", 12, new DateTime(2015, 1, 1))
        };

        var result = SongTitlePreparation.Prepare(candidates, cap: 50, out int totalDistinct);

        Assert.Equal(3, totalDistinct);
        Assert.Equal("Anchor", result[0].Name); // play count beats everything
        Assert.Equal("Rapsodia", result[1].Name); // dedup kept the played instance
        Assert.Equal("Zebra", result[2].Name); // recency is only the tiebreak
    }

    [Fact]
    public void Prepare_CapsAfterDedup_AndReportsPreCapTotal()
    {
        var candidates = new List<(Guid, string, int, DateTime)>();
        for (int i = 0; i < 10; i++)
        {
            // Ten distinct titles, each appearing twice (suffix variants).
            candidates.Add((Guid.NewGuid(), $"Song {i}", 10 - i, new DateTime(2020, 1, 1)));
            candidates.Add((Guid.NewGuid(), $"Song {i} (Live)", 0, new DateTime(2020, 1, 1)));
        }

        var result = SongTitlePreparation.Prepare(candidates, cap: 3, out int totalDistinct);

        Assert.Equal(10, totalDistinct); // 20 candidates collapse to 10 distinct BEFORE the cap
        Assert.Equal(3, result.Count); // N is the capped count
        Assert.Equal("Song 0", result[0].Name);
        Assert.Equal("Song 1", result[1].Name);
        Assert.Equal("Song 2", result[2].Name);
    }
}
