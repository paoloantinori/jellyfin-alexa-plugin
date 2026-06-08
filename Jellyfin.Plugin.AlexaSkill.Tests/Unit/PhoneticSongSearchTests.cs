#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for phonetic (Double Metaphone) song title search.
/// Covers KeywordMatcher.ScorePhonetic, SongNgramIndexService.SearchPhonetic,
/// and the integration between them.
/// </summary>
[Collection("Plugin")]
public class PhoneticSongSearchTests : PluginTestBase
{
    // ─── KeywordMatcher.ScorePhonetic: Core Matching ─────────────────────

    [Fact]
    public void ScorePhonetic_MisspelledTitle_MatchesViaPhoneticCode()
    {
        // "rapsodi" → Double Metaphone same as "rhapsody" (both encode to "RFST")
        // "boemian" should phonetically match "bohemian"
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        // Misspelled: "rapsodi" instead of "rhapsody"
        var keywords = new[] { "rapsodi" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.NotEmpty(result);
        Assert.Equal("Bohemian Rhapsody", result[0].Item.Name);
    }

    [Fact]
    public void ScorePhonetic_ExactMatchStillWorks()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = new[] { "bohemian", "rhapsody" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.Single(result);
        Assert.Equal("Bohemian Rhapsody", result[0].Item.Name);
    }

    [Fact]
    public void ScorePhonetic_ApplyPhoneticPenalty()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        // Exact match score: (0.7*1 + 0.3*1)*100 + 5 = 105 (from Score)
        // Phonetic penalty: 0.75 → (0.7*1 + 0.3*1)*100*0.75 + 5 = 75 + 5 = 80
        var keywords = new[] { "bohemian", "rhapsody" };
        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.Single(result);
        // Score should be strictly less than the exact match score (105)
        Assert.True(result[0].Score < 105.0,
            $"Phonetic score {result[0].Score} should be less than exact match score 105");
        Assert.True(result[0].Score > 0, "Phonetic score should be positive");
    }

    [Fact]
    public void ScorePhonetic_RelaxedKeywordCoverage_Allows50Percent()
    {
        // Two keywords, only one matches phonetically → 50% coverage (at threshold)
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        // "rapsodi" matches "rhapsody" phonetically, "xyzzyfoo" matches nothing
        var keywords = new[] { "rapsodi", "xyzzyfoo" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.NotEmpty(result);
    }

    [Fact]
    public void ScorePhonetic_BelowMinCoverage_Excluded()
    {
        // Three keywords, only one matches → 33% coverage (below 50% threshold)
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        // Only "bohemian" matches phonetically, "xyzzy" and "plugh" don't
        var keywords = new[] { "bohemian", "xyzzy", "plugh" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.Empty(result);
    }

    // ─── KeywordMatcher.ScorePhonetic: Cross-Language Misspellings ────────

    [Fact]
    public void ScorePhonetic_ItalianSpeakerMisspellingEnglishTitle()
    {
        // Italian speaker spells "Bohemian" as "Boemia" and "Rhapsody" as "Rapsodi"
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = new[] { "boemia", "rapsodi" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "it-IT");

        Assert.NotEmpty(result);
    }

    [Fact]
    public void ScorePhonetic_GermanSpeakerMisspellingEnglishTitle()
    {
        // German speaker spells "Satisfaction" as "Satisfaktion"
        var songs = new List<Audio>
        {
            new() { Name = "Satisfaction", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = new[] { "satisfaktion" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "de-DE");

        Assert.NotEmpty(result);
    }

    [Fact]
    public void ScorePhonetic_MultipleSongs_RanksByScoreDescending()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() },
            new() { Name = "Bohemian Like You", Id = Guid.NewGuid() },
            new() { Name = "Totally Unrelated Song", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        // Both "boemia" and "rapsodi" are misspelled forms
        var keywords = new[] { "boemia", "rapsodi" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.NotEmpty(result);
        // Results should always be sorted by score descending
        for (int i = 1; i < result.Count; i++)
        {
            Assert.True(result[i - 1].Score >= result[i].Score,
                $"Result {i - 1} ({result[i - 1].Item.Name}, score={result[i - 1].Score}) " +
                $"should be >= result {i} ({result[i].Item.Name}, score={result[i].Score})");
        }
    }

    // ─── KeywordMatcher.ScorePhonetic: Edge Cases ────────────────────────

    [Fact]
    public void ScorePhonetic_EmptyKeywords_ReturnsEmpty()
    {
        var songs = new List<Audio>
        {
            new() { Name = "Hello", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var result = KeywordMatcher.ScorePhonetic(songs, Array.Empty<string>(), "en-US");

        Assert.Empty(result);
    }

    [Fact]
    public void ScorePhonetic_EmptySongs_ReturnsEmpty()
    {
        var keywords = new[] { "hello" };

        var result = KeywordMatcher.ScorePhonetic(new List<BaseItem>(), keywords, "en-US");

        Assert.Empty(result);
    }

    [Fact]
    public void ScorePhonetic_SongWithNullName_Skipped()
    {
        var songs = new List<Audio>
        {
            new() { Name = null!, Id = Guid.NewGuid() },
            new() { Name = "Hello", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = new[] { "helo" }; // misspelled

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.Single(result);
        Assert.Equal("Hello", result[0].Item.Name);
    }

    [Fact]
    public void ScorePhonetic_StopWordsOnlyKeywords_ReturnsEmpty()
    {
        var songs = new List<Audio>
        {
            new() { Name = "The Sound of Silence", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        // Stop words only → empty tokens after KeywordMatcher.Tokenize
        var keywords = KeywordMatcher.Tokenize("the of", "en-US");

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.Empty(result);
    }

    // ─── SongNgramIndexService.SearchPhonetic ─────────────────────────────

    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly ILogger<SongNgramIndexService> _logger;

    public PhoneticSongSearchTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _logger = LoggerFactory.Create(b => { }).CreateLogger<SongNgramIndexService>();
    }

    private SongNgramIndexService CreateService(List<BaseItem>? songs = null)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(songs ?? new List<BaseItem>());

        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        return new SongNgramIndexService(_libraryManagerMock.Object, _logger);
    }

    private static Audio MakeSong(string title, Guid? id = null)
    {
        return new Audio { Name = title, Id = id ?? Guid.NewGuid() };
    }

    [Fact]
    public async Task SearchPhonetic_ExactMissMisspelledQuery_FindsSong()
    {
        var song = MakeSong("Bohemian Rhapsody");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        // Exact search should miss (misspelled)
        var exactResults = service.Search(new[] { "rapsodi" }, "en-US");
        Assert.Empty(exactResults);

        // Phonetic search should find it
        var phoneticResults = service.SearchPhonetic(new[] { "rapsodi" }, "en-US");
        Assert.NotEmpty(phoneticResults);
        Assert.Equal(song.Id, phoneticResults[0].Item.Id);
    }

    [Fact]
    public async Task SearchPhonetic_ReturnsLowerScoreThanExactSearch()
    {
        var song = MakeSong("Bohemian Rhapsody");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        var exactResults = service.Search(new[] { "bohemian", "rhapsody" }, "en-US");
        var phoneticResults = service.SearchPhonetic(new[] { "bohemian", "rhapsody" }, "en-US");

        Assert.NotEmpty(exactResults);
        Assert.NotEmpty(phoneticResults);
        Assert.True(phoneticResults[0].Score < exactResults[0].Score,
            $"Phonetic score ({phoneticResults[0].Score}) should be less than exact ({exactResults[0].Score})");
    }

    [Fact]
    public async Task SearchPhonetic_CompletelyUnrelatedQuery_ReturnsEmpty()
    {
        var song = MakeSong("Bohemian Rhapsody");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        var results = service.SearchPhonetic(new[] { "xyzzyfoo" }, "en-US");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchPhonetic_NotReady_ReturnsEmpty()
    {
        var service = CreateService();
        // Don't call StartAsync

        var results = service.SearchPhonetic(new[] { "hello" }, "en-US");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchPhonetic_EmptyTokens_ReturnsEmpty()
    {
        var song = MakeSong("Hello");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        var results = service.SearchPhonetic(Array.Empty<string>(), "en-US");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchPhonetic_FiltersByLibrary()
    {
        var folderId1 = Guid.NewGuid();
        var folderId2 = Guid.NewGuid();
        var song1 = MakeSong("Bohemian Rhapsody");
        var song2 = MakeSong("Bohemian Dreams");

        song1.ParentId = folderId1;
        song2.ParentId = folderId2;

        var songs = new List<BaseItem> { song1, song2 };

        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(songs);
        _libraryManagerMock.Setup(l => l.GetItemById(folderId1))
            .Returns(new Folder { Id = folderId1 });
        _libraryManagerMock.Setup(l => l.GetItemById(folderId2))
            .Returns(new Folder { Id = folderId2 });

        var service = new SongNgramIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        // Phonetic search with library filter → only song1
        var results = service.SearchPhonetic(new[] { "rapsodi" }, "en-US", new[] { folderId1 });
        Assert.All(results, r => Assert.Equal(song1.Id, r.Item.Id));
    }

    [Fact]
    public async Task SearchPhonetic_MultipleKeywordsMisspelled()
    {
        var song = MakeSong("Stairway to Heaven");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        // "stairway" → misspelled as "stairwei" (same phonetic code)
        // "heaven" → misspelled as "heven" (same phonetic code)
        var results = service.SearchPhonetic(new[] { "stairwei", "heven" }, "en-US");
        Assert.NotEmpty(results);
        Assert.Equal(song.Id, results[0].Item.Id);
    }

    [Fact]
    public async Task SearchPhonetic_SingleKeywordMisspelled()
    {
        var song = MakeSong("Yesterday");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        // "yesterday" → misspelled as "yesterdai" (should share phonetic code)
        var results = service.SearchPhonetic(new[] { "yesterdai" }, "en-US");
        Assert.NotEmpty(results);
    }

    // ─── Performance ──────────────────────────────────────────────────────

    [Fact]
    public async Task SearchPhonetic_2000Songs_Under20ms()
    {
        var songs = new List<BaseItem>();
        var rng = new Random(42);
        var adjectives = new[] { "Red", "Blue", "Dark", "Bright", "Wild", "Calm", "Fast", "Slow", "High", "Deep" };
        var nouns = new[] { "Night", "Day", "Road", "Sky", "Sea", "Fire", "Rain", "Sun", "Moon", "Star" };

        for (int i = 0; i < 2000; i++)
        {
            string title = $"{adjectives[rng.Next(adjectives.Length)]} {nouns[rng.Next(nouns.Length)]} {i}";
            songs.Add(MakeSong(title));
        }

        // Add target with a name that's easy to misspell
        var targetSong = MakeSong("Bohemian Rhapsody");
        songs.Add(targetSong);

        var service = CreateService(songs);
        await service.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var results = service.SearchPhonetic(new[] { "rapsodi" }, "en-US");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 20,
            $"Phonetic search took {sw.ElapsedMilliseconds}ms, expected < 20ms");
        Assert.NotEmpty(results);
    }

    // ─── Phonetic vs Exact: Correct Behavior ──────────────────────────────

    [Fact]
    public async Task ExactSearchBeatsPhoneticSearch_SameQuery()
    {
        // When exact match works, phonetic should return lower scores
        var song = MakeSong("Hotel California");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        var exactResults = service.Search(new[] { "hotel", "california" }, "en-US");
        var phoneticResults = service.SearchPhonetic(new[] { "hotel", "california" }, "en-US");

        Assert.NotEmpty(exactResults);
        Assert.NotEmpty(phoneticResults);

        // Phonetic score should always be lower due to penalty
        Assert.True(phoneticResults[0].Score < exactResults[0].Score,
            "Phonetic score should be penalized relative to exact score");
    }

    // ─── Double Metaphone Specific Cases ──────────────────────────────────

    [Fact]
    public void ScorePhonetic_SilentLettersMatch()
    {
        // "Knight" vs "Night" — silent 'K' → same phonetic code
        var songs = new List<Audio>
        {
            new() { Name = "Night", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = new[] { "knight" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.NotEmpty(result);
    }

    [Fact]
    public void ScorePhonetic_PhoneticallySimilarConsonants()
    {
        // "Smith" vs "Schmidt" → Double Metaphone: both "SMT" / "XMT"
        // But for song titles, let's test "fotograf" vs "photograph"
        // "photograph" → tokens: "photograph"
        // Double Metaphone: "photograph" → "FTRF"
        // "fotograf" → "FTRF" (same!)
        var songs = new List<Audio>
        {
            new() { Name = "Photograph", Id = Guid.NewGuid() }
        }.Cast<BaseItem>().ToList();

        var keywords = new[] { "fotograf" };

        var result = KeywordMatcher.ScorePhonetic(songs, keywords, "en-US");

        Assert.NotEmpty(result);
    }
}
