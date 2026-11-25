using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Alexa.NET;
using Alexa.NET.Request;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Microsoft.Extensions.Logging;
using Moq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

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

    /// <summary>
    /// JF-419.3: service over a controllable load function, with a fast retry
    /// interval so the recovery tests run in milliseconds.
    /// </summary>
    private SongNgramIndexService CreateServiceWithLoad(Func<List<BaseItem>> load, TimeSpan? retryInterval = null)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => load());

        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new SongNgramIndexService(_libraryManagerMock.Object, _logger);
        service.FailedLoadRetryInterval = retryInterval ?? TimeSpan.FromMilliseconds(50);
        return service;
    }

    // --- JF-419.3: failed-load self-recovery (mirrors ArtistIndexServiceTests) ---

    [Fact]
    public async Task StartAsync_FailedInitialLoad_RetriesAndRecovers()
    {
        var songs = new List<BaseItem> { MakeSong("Bohemian Rhapsody") };

        int callCount = 0;
        var service = CreateServiceWithLoad(() =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new InvalidOperationException("db still migrating");
            }

            return songs;
        });
        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.False(service.IsReady); // gate closed after the failed initial load

            // The service must recover on its own, no restart, no library change
            Assert.True(
                await WaitUntilAsync(() => service.IsReady),
                "index should recover via background retry");

            Assert.Equal(1, service.SongCount);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task StartAsync_PersistentFailure_KeepsRetrying()
    {
        int callCount = 0;
        var service = CreateServiceWithLoad(() =>
        {
            callCount++;
            throw new InvalidOperationException("db down");
        });
        try
        {
            await service.StartAsync(CancellationToken.None);

            Assert.False(service.IsReady);
            Assert.True(
                await WaitUntilAsync(() => callCount > 1),
                $"expected background retries, saw {callCount} load attempts");
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// Review round 1 finding 2: after MaxLoadAttempts consecutive failures the index
    /// DISABLES itself instead of retrying forever, so the warming gates treat it as
    /// absent and callers degrade to their database paths (no endless warming Tell).
    /// </summary>
    [Fact]
    public async Task StartAsync_PersistentFailure_DisablesAfterMaxAttempts()
    {
        int callCount = 0;
        var service = CreateServiceWithLoad(() =>
        {
            callCount++;
            throw new InvalidOperationException("full-catalog scan keeps failing");
        });
        try
        {
            await service.StartAsync(CancellationToken.None);

            Assert.True(
                await WaitUntilAsync(() => service.IsDisabled),
                "index should give up after the max consecutive failures");
            Assert.False(service.IsReady);

            // A disabled index degrades to empty results (DB fallback), never throws
            Assert.Empty(service.Search(new[] { "hello" }, "en-US"));
            Assert.Empty(service.SearchPhonetic(new[] { "hello" }, "en-US"));

            int attemptsAtGiveUp = callCount;
            await Task.Delay(150);
            Assert.Equal(attemptsAtGiveUp, callCount); // no retries after the give-up
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task StartAsync_SuccessfulLoad_DoesNotScheduleRetry()
    {
        int callCount = 0;
        var service = CreateServiceWithLoad(() =>
        {
            callCount++;
            return new List<BaseItem> { MakeSong("Circles") };
        });
        try
        {
            await service.StartAsync(CancellationToken.None);
            await Task.Delay(150);

            Assert.True(service.IsReady);
            Assert.Equal(1, callCount); // no background reload after a successful load
        }
        finally
        {
            service.Dispose();
        }
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

    // --- JF-755: symmetric index-side kana normalization (romaji tokens) ---

    [Fact]
    public async Task Search_RomajiKeywords_FindKanaTitledSong_JF755()
    {
        // The song-side JF-643 narrowing closed: a kana-titled song is indexed
        // under its romanized tokens alongside the kana ones, and the query tokens
        // (always romanized by the callers) reach it through the O(1) lookup with
        // full keyword coverage in the scorer (TitleTokens, the one shared union).
        var song = MakeSong("ヨルニカケル");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        var results = service.Search(new[] { "yorunikakeru" }, "ja-JP");

        var match = Assert.Single(results);
        Assert.Equal(song.Id, match.Item.Id);
        Assert.True(match.Score > 0);
    }

    [Fact]
    public async Task Search_KanaTitle_UnrelatedRomajiKeywords_ReturnsEmpty_JF755()
    {
        // The romaji keys only widen lookup toward the romanized reading of the
        // SAME title; an unrelated romaji query still misses (no false positives).
        var song = MakeSong("ヨルニカケル");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        Assert.Empty(service.Search(new[] { "bohemian" }, "ja-JP"));
    }

    [Fact]
    public async Task StartAsync_LatinLibrary_IndexShapeUnchanged_JF755()
    {
        // Control: kana-free titles keep the identical single token stream, so the
        // index shape (and the NgramCount surface the load log prints) is unchanged
        // for Latin libraries.
        var song = MakeSong("Hotel California");
        var service = CreateService(new List<BaseItem> { song });
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1, service.SongCount);
        Assert.Equal(1, service.NgramCount);
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

        // JF-641: the guard is noise-robust (warmup + one retry) while keeping the
        // order-of-magnitude regression teeth (a lost index shape breaches every attempt).
        PerfGuard.UnderMs(
            () => Assert.Single(service.Search(new[] { "soul", "coughing" }, "en-US")),
            budgetMs: 10,
            label: "SongNgram search (2000 songs)");

        var results = service.Search(new[] { "soul", "coughing" }, "en-US");
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

    // --- JF-432: atomic snapshot publishing (no torn reads across a refresh) ---

    [Fact]
    public void PublishedState_IsOneSnapshotField_StructuralInvariant()
    {
        // The loaded state must live in ONE field (an immutable snapshot record),
        // never in a group of separate volatile fields: volatile orders the individual
        // assignments but not the group, so sequential publishing let a reader observe
        // a torn mix mid-refresh (old bigram candidate IDs against the new entries list,
        // which returned empty results for an existing song). Asserting the shape
        // mechanically guards against a regression to per-member fields. Shared helper
        // since JF-448 (review F7): the field now lives on the generic snapshot base
        // and the walk covers the whole ownership chain.
        IndexSnapshotAssertions.AssertSingleSnapshotField<SongNgramIndexService, SongNgramIndexSnapshot>();
    }

    [Fact]
    public async Task Refresh_PublishesOneSnapshot_AllReadSurfacesAgree()
    {
        var song1 = MakeSong("Song One");
        var song2 = MakeSong("Song Two");
        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => ++callCount == 1
                ? new List<BaseItem> { song1 }
                : new List<BaseItem> { song1, song2 });
        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new SongNgramIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);
        var before = service.CurrentSnapshot;

        // Force refresh with updated data
        await service.StartAsync(CancellationToken.None);

        var after = service.CurrentSnapshot;
        Assert.NotSame(before, after); // one atomic swap, not a member-by-member publish
        Assert.Equal(2, service.SongCount);
        Assert.Equal(2, service.NgramCount); // "song one" and "song two"

        var results = service.Search(new[] { "song", "two" }, "en-US");
        var match = Assert.Single(results);
        Assert.Equal(song2.Id, match.Item.Id);

        // The returned item must exist in the CURRENT snapshot's entries: a torn read
        // served old-index hits against the new entries list (or the reverse)
        Assert.Contains(after.AllEntries, s => s.Id == match.Item.Id);
    }

    [Fact]
    public async Task ConcurrentRefreshAndReads_NeverObserveTornState()
    {
        // Refreshes swap two disjoint datasets (identical title, different song IDs
        // and folders) while a reader searches with BOTH folders as the library
        // filter, alternating the bigram path and the phonetic path so every snapshot
        // member is exercised mid-refresh. Either snapshot alone always returns
        // exactly one song; an empty result requires a torn publish (old bigram IDs
        // against the new entries, or the old top-parent map filtering the new song out).
        var folderA = Guid.NewGuid();
        var folderB = Guid.NewGuid();
        var songA = MakeSong("Torn Song");
        songA.ParentId = folderA;
        var songB = MakeSong("Torn Song");
        songB.ParentId = folderB;

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => Interlocked.Increment(ref callCount) % 2 == 1
                ? new List<BaseItem> { songA }
                : new List<BaseItem> { songB });
        _libraryManagerMock.Setup(l => l.GetItemById(folderA)).Returns(new Folder { Id = folderA });
        _libraryManagerMock.Setup(l => l.GetItemById(folderB)).Returns(new Folder { Id = folderB });

        var service = new SongNgramIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);
        var filters = new[] { folderA, folderB };
        var tokens = new[] { "torn", "song" };

        using var done = new ManualResetEventSlim(false);
        var refresher = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 300; i++)
                {
                    await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                done.Set(); // a faulted refresher must fail the test, not hang the read loop
            }
        });

        try
        {
            // The loop condition (not a timing-dependent assert) guarantees a minimum
            // read count: a fast refresher must not leave the reader with near-zero reads
            int reads = 0;
            while (!done.IsSet || reads < 50)
            {
                if (reads % 2 == 0)
                {
                    Assert.NotEmpty(service.Search(tokens, "en-US", filters));
                }
                else
                {
                    Assert.NotEmpty(service.SearchPhonetic(new[] { "torn" }, "en-US", filters));
                }

                reads++;
            }

            await refresher;
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// JF-948 (issue #46 side note, live report): a metadata EDIT raises
    /// ItemUpdated, not ItemAdded/ItemRemoved. The index must refresh on it,
    /// otherwise a re-tagged track keeps serving its stale entries until the
    /// next process restart (the live report: the JF-439 song fallback
    /// stopped finding the title after an album/artist tag edit while the
    /// reload log showed the same count, restart fixed it).
    /// </summary>
    [Fact]
    public async Task ItemUpdated_OnAudio_SchedulesARefresh()
    {
        int loadCount = 0;
        var service = CreateServiceWithLoad(() => { loadCount++; return new List<BaseItem>(); });
        service.RefreshDebounceInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            await service.StartAsync(CancellationToken.None);
            int initialLoads = loadCount;
            Assert.True(initialLoads >= 1, "the startup load must have run");

            _libraryManagerMock.Raise(
                l => l.ItemUpdated += null,
                _libraryManagerMock.Object,
                new ItemChangeEventArgs { Item = new Audio { Name = "Retagged", Id = Guid.NewGuid() } });

            // The debounce window plus a margin: the refresh must have fired.
            await Task.Delay(600);
            Assert.True(loadCount > initialLoads, "an ItemUpdated event on an Audio item must schedule a refresh (JF-948)");
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>
    /// JF-948 END-TO-END reproduction of the issue #46 reporter's flow, through
    /// the REAL JF-439 fallback: a track is indexed; its metadata is edited
    /// (the library query now returns the item under its new name); ItemUpdated
    /// fires; after the debounced refresh the fallback must find the track
    /// under the NEW name. This is the shape the pre-fix build could not serve:
    /// with no ItemUpdated subscription the index kept the pre-edit entries
    /// until process restart, and the artist-not-found path answered
    /// "keinen Kuenstler gefunden" instead of falling through to the song.
    /// The user's allowed-library filter is deliberately unrestricted (the
    /// reporter's single-library shape), so the only gate is the index itself.
    /// </summary>
    [Fact]
    public async Task MetadataEdit_ItemUpdatedRefreshes_AndTheJF439FallbackFindsTheNewTitle()
    {
        var track = new Audio { Name = "Die Streithoernchen", Id = Guid.NewGuid() };
        var library = new List<BaseItem> { track };
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => new List<BaseItem>(library));
        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = CreateServiceWithLoad(() => library.ToList<BaseItem>());
        service.RefreshDebounceInterval = TimeSpan.FromMilliseconds(100);
        // BuildSingleSongResponse needs the plugin instance for the stream URL.
        TestHelpers.EnsurePluginInstance(
            new PluginConfiguration { ServerAddress = "http://localhost:8096/" },
            LoggerFactory.Create(b => b.AddConsole()),
            c => { },
            "jf-948-repro");
        try
        {
            await service.StartAsync(CancellationToken.None);
            var sessionManagerMock = new Mock<ISessionManager>();
            var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);
            var config = new PluginConfiguration { ServerAddress = "http://localhost:8096/" };
            ILogger fallbackLogger = loggerFactory.CreateLogger("jf-948-fallback");
            var fallback = new CrossMediaFallback(
                config,
                fallbackLogger,
                new PlaybackLaunchBuilder(config, fallbackLogger, (_, _, _) => Task.FromResult(false)),
                requestTimeoutMs: 6000);
            var pluginUser = new Entities.User { Id = Guid.NewGuid(), InvocationName = "test", JellyfinToken = "test-token" };

            // Pre-edit: the fallback finds the track by its title.
            var before = fallback.TrySongFallback(
                "die streithoernchen", pluginUser, session, TestHelpers.CreateTestContext(), "de-DE",
                service, _libraryManagerMock.Object, "jf-948 probe", CancellationToken.None);
            Assert.NotNull(before);

            // THE EDIT: the track's metadata changes (a re-tag); the library
            // now serves the item under the new name. ItemUpdated fires.
            track.Name = "Umgetagte Aufnahme";
            _libraryManagerMock.Raise(
                l => l.ItemUpdated += null,
                _libraryManagerMock.Object,
                new ItemChangeEventArgs { Item = track });

            // The debounce window plus a margin.
            await Task.Delay(600);

            var after = fallback.TrySongFallback(
                "umgetagte aufnahme", pluginUser, session, TestHelpers.CreateTestContext(), "de-DE",
                service, _libraryManagerMock.Object, "jf-948 probe", CancellationToken.None);
            Assert.NotNull(after);

            // And the OLD title's entry is gone (the snapshot swapped, not
            // appended): a title disjoint from the new one must not match
            // anything, so the fallback serves the caller's clean not-found.
            var stale = fallback.TrySongFallback(
                "die streithoernchen", pluginUser, session, TestHelpers.CreateTestContext(), "de-DE",
                service, _libraryManagerMock.Object, "jf-948 probe", CancellationToken.None);
            Assert.Null(stale);
        }
        finally
        {
            service.Dispose();
        }
    }
}
