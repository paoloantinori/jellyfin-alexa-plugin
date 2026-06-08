using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class SongNgramIndexServiceTests : PluginTestBase
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly ILogger<SongNgramIndexService> _logger;

    public SongNgramIndexServiceTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _logger = LoggerFactory.Create(b => { }).CreateLogger<SongNgramIndexService>();
    }

    private SongNgramIndexService CreateService(List<BaseItem>? songs = null)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(songs ?? new List<BaseItem>());

        // Default: GetItemById returns null (no parent resolution)
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
    public async Task EmptyLibrary_IsReadyWithZeroCount()
    {
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsReady);
        Assert.Equal(0, service.SongCount);
        Assert.Equal(0, service.NgramCount);
    }

    [Fact]
    public async Task SingleSong_GeneratesCorrectBigrams()
    {
        var song = MakeSong("Hotel California");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsReady);
        Assert.Equal(1, service.SongCount);
        // "Hotel California" → tokens: ["hotel", "california"] → bigram: "hotel california"
        Assert.Equal(1, service.NgramCount);
    }

    [Fact]
    public async Task Search_MatchingBigram_ReturnsSong()
    {
        var song = MakeSong("Hotel California");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        // KeywordMatcher.Tokenize("hotel california", "en-US") → ["hotel", "california"]
        var results = service.Search(new[] { "hotel", "california" }, "en-US");

        Assert.Single(results);
        Assert.Equal(song.Id, results[0].Item.Id);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public async Task Search_NonMatchingBigram_ReturnsEmpty()
    {
        var song = MakeSong("Hotel California");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        var results = service.Search(new[] { "stairway", "heaven" }, "en-US");

        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_StopWordsExcluded()
    {
        // Italian stop words: "il", "lo", "la", etc.
        // "Il Tempo Gigante" → tokens: ["tempo", "gigante"] (stop word "il" removed)
        var song = MakeSong("Il Tempo Gigante");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        // Index was built with "en-US" locale for tokenization, but "Il" is not an English stop word
        // So tokens in the index are: ["il", "tempo", "gigante"]
        // Searching with it-IT locale: ["tempo", "gigante"] (stop words removed)
        var results = service.Search(new[] { "tempo", "gigante" }, "it-IT");

        Assert.Single(results);
    }

    [Fact]
    public async Task Search_SingleKeyword_UsesFallback()
    {
        var song1 = MakeSong("Yesterday");
        var song2 = MakeSong("Hello");
        var service = CreateService(new List<BaseItem> { song1, song2 });
        await service.StartAsync(CancellationToken.None);

        // Single keyword → falls back to single-token index scan
        var results = service.Search(new[] { "yesterday" }, "en-US");

        Assert.Single(results);
        Assert.Equal(song1.Id, results[0].Item.Id);
    }

    [Fact]
    public async Task Performance_2000Songs_Under10ms()
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

        // Add a specific target song
        var targetSong = MakeSong("Soul Coughing Super");
        songs.Add(targetSong);

        var service = CreateService(songs);
        await service.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        var results = service.Search(new[] { "soul", "coughing" }, "en-US");
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 10, $"Search took {sw.ElapsedMilliseconds}ms, expected < 10ms");
        Assert.Single(results);
        Assert.Equal(targetSong.Id, results[0].Item.Id);
    }

    [Fact]
    public async Task LibraryFilter_ByTopParentId()
    {
        var folderId1 = Guid.NewGuid();
        var folderId2 = Guid.NewGuid();
        var song1 = MakeSong("Hotel California");
        var song2 = MakeSong("Hotel Room");

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

        // Filter by folderId1 → only song1
        var results = service.Search(new[] { "hotel", "california" }, "en-US", new[] { folderId1 });
        Assert.Single(results);
        Assert.Equal(song1.Id, results[0].Item.Id);

        // Filter by folderId2 → only song2
        results = service.Search(new[] { "hotel", "room" }, "en-US", new[] { folderId2 });
        Assert.Single(results);
        Assert.Equal(song2.Id, results[0].Item.Id);

        // No filter → both songs
        results = service.Search(new[] { "hotel" }, "en-US");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Refresh_PicksUpNewSongs()
    {
        var song1 = MakeSong("Song One");
        var initialSongs = new List<BaseItem> { song1 };

        var song2 = MakeSong("Song Two");
        var updatedSongs = new List<BaseItem> { song1, song2 };

        int callCount = 0;
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => ++callCount == 1 ? initialSongs : updatedSongs);

        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new SongNgramIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, service.SongCount);

        // Force refresh with updated data
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(2, service.SongCount);
        var results = service.Search(new[] { "song", "two" }, "en-US");
        Assert.Single(results);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromEvents()
    {
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);
        service.Dispose();

        // Verify no crash when raising events after disposal
        var eventArgs = new ItemChangeEventArgs
        {
            Item = new Audio { Name = "Test", Id = Guid.NewGuid() }
        };

        // Should not throw
        _libraryManagerMock.Raise(l => l.ItemAdded += null, _libraryManagerMock.Object, eventArgs);
    }

    [Fact]
    public async Task BeforeLoad_IsNotReady()
    {
        var service = CreateService();
        // Don't call StartAsync

        Assert.False(service.IsReady);
        Assert.Equal(0, service.SongCount);
    }

    [Fact]
    public async Task Search_MultipleBigramHits_DeduplicatesBySongId()
    {
        // "Love Me Do" and "Love Me Tender" both have the bigram "love me"
        var song1 = MakeSong("Love Me Do");
        var song2 = MakeSong("Love Me Tender");
        var song3 = MakeSong("All You Need Is Love");

        var service = CreateService(new List<BaseItem> { song1, song2, song3 });
        await service.StartAsync(CancellationToken.None);

        // Search with "love me" bigram
        var results = service.Search(new[] { "love", "me" }, "en-US");

        // Both "Love Me Do" and "Love Me Tender" should match (bigram "love me")
        // "All You Need Is Love" has "love" but not bigram "love me"
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Score > 0));
    }

    [Fact]
    public async Task Search_ThreeWordTitle_MultipleBigrams()
    {
        var song = MakeSong("A Day In The Life");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        // "A Day In The Life" → tokens: ["a", "day", "in", "the", "life"] (no English stop words removed in "en-US" for "a", "in", "the")
        // Actually, English stop words include: "the", "a", "an", "of", "in", "on", "at", "to", "and", "or", "is", "it"
        // So tokens: ["day", "life"]
        // bigram: "day life"

        var results = service.Search(new[] { "day", "life" }, "en-US");
        Assert.Single(results);
        Assert.Equal(song.Id, results[0].Item.Id);
    }
}
