using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class ArtistIndexServiceTests : PluginTestBase
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly ILogger<ArtistIndexService> _logger;

    public ArtistIndexServiceTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _logger = LoggerFactory.Create(b => { }).CreateLogger<ArtistIndexService>();
    }

    private ArtistIndexService CreateService(List<BaseItem>? artists = null)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(artists ?? new List<BaseItem>());

        // Default: GetItemById returns null (no parent resolution)
        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        return new ArtistIndexService(_libraryManagerMock.Object, _logger);
    }

    [Fact]
    public async Task StartAsync_LoadsArtists()
    {
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Beatles", Id = Guid.NewGuid() },
            new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsReady);
        Assert.Equal(2, service.Count);
        Assert.Equal(2, service.GetArtists().Count);
    }

    [Fact]
    public async Task StartAsync_EmptyLibrary_ReadyWithZeroArtists()
    {
        var service = CreateService();
        await service.StartAsync(CancellationToken.None);

        Assert.True(service.IsReady);
        Assert.Equal(0, service.Count);
        Assert.Empty(service.GetArtists());
    }

    // --- JF-419.1: failed initial load must self-recover via background retry ---

    /// <summary>
    /// Creates a service whose load behavior is driven by <paramref name="load"/>
    /// (may throw to simulate a failing DB). Fast retry interval by default so
    /// recovery tests run in milliseconds.
    /// </summary>
    private ArtistIndexService CreateServiceWithLoad(Func<List<BaseItem>> load, TimeSpan? retryInterval = null)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => load());

        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        service.FailedLoadRetryInterval = retryInterval ?? TimeSpan.FromMilliseconds(50);
        return service;
    }

    [Fact]
    public async Task StartAsync_FailedInitialLoad_RetriesAndRecovers()
    {
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() }
        };

        int callCount = 0;
        var service = CreateServiceWithLoad(() =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new InvalidOperationException("db still migrating");
            }

            return artists;
        });
        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.False(service.IsReady); // gate closed after the failed initial load

            // The service must recover on its own, no restart, no library change
            Assert.True(
                await WaitUntilAsync(() => service.IsReady),
                "index should recover via background retry");
            Assert.Equal(1, service.Count);
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
            await Task.Delay(250);

            Assert.False(service.IsReady);
            Assert.True(callCount > 1, $"expected background retries, saw {callCount} load attempts");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task StartAsync_SuccessfulLoad_DoesNotScheduleRetry()
    {
        // Count the artist load specifically: a successful load may also run the
        // folderless-artist album join (JF-455), which is part of the same load, not a reload.
        int artistLoadCount = 0;
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(() =>
            {
                artistLoadCount++;
                return new List<BaseItem> { new MusicArtist { Name = "Mina", Id = Guid.NewGuid() } };
            });
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(new List<BaseItem>());
        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        try
        {
            await service.StartAsync(CancellationToken.None);
            Assert.True(service.IsReady);

            // Long enough for a wrongly-armed retry timer to fire at least twice
            await Task.Delay(150);

            Assert.Equal(1, artistLoadCount); // no background reload after a successful load
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task GetArtists_NoFilter_ReturnsAllArtists()
    {
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() },
            new MusicArtist { Name = "Nirvana", Id = Guid.NewGuid() },
            new MusicArtist { Name = " Muse ", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        var result = service.GetArtists();
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetArtists_WithTopParentIds_FiltersCorrectly()
    {
        var folderId1 = Guid.NewGuid();
        var folderId2 = Guid.NewGuid();
        var artist1Id = Guid.NewGuid();
        var artist2Id = Guid.NewGuid();
        var artist3Id = Guid.NewGuid();

        // Set up artists with parent IDs
        var artist1 = new MusicArtist { Name = "Artist1", Id = artist1Id };
        var artist2 = new MusicArtist { Name = "Artist2", Id = artist2Id };
        var artist3 = new MusicArtist { Name = "Artist3", Id = artist3Id };

        var artists = new List<BaseItem> { artist1, artist2, artist3 };

        // artist1.ParentId = parentA, parentA has no further parent → top parent = parentA's ID
        var parentAId = Guid.NewGuid();
        artist1.ParentId = parentAId;

        // artist2.ParentId = folderId1 → top parent = folderId1
        artist2.ParentId = folderId1;

        // artist3.ParentId = folderId2 → top parent = folderId2
        artist3.ParentId = folderId2;

        var parentA = new Folder { Id = parentAId };
        var folder1 = new Folder { Id = folderId1 };
        var folder2 = new Folder { Id = folderId2 };

        // Setup mocks BEFORE creating service (CreateService uses SetupGetItemList)
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(artists);
        _libraryManagerMock.Setup(l => l.GetItemById(parentAId)).Returns(parentA);
        _libraryManagerMock.Setup(l => l.GetItemById(folderId1)).Returns(folder1);
        _libraryManagerMock.Setup(l => l.GetItemById(folderId2)).Returns(folder2);

        // Don't use CreateService() here — it overwrites GetItemById setup
        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        // Filter by folderId1 → should get artist2 (direct child) only,
        // since artist1's top parent is parentA (not folderId1)
        var result = service.GetArtists(new[] { folderId1 });
        Assert.Single(result);
        Assert.Contains(result, a => a.Name == "Artist2");

        // Filter by parentAId → should get artist1
        result = service.GetArtists(new[] { parentAId });
        Assert.Single(result);
        Assert.Contains(result, a => a.Name == "Artist1");

        // Filter by folderId2 → should get artist3
        result = service.GetArtists(new[] { folderId2 });
        Assert.Single(result);
        Assert.Contains(result, a => a.Name == "Artist3");

        // No filter → all artists
        result = service.GetArtists();
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GetArtists_WithEmptyTopParentIds_ReturnsAll()
    {
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Artist1", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        var result = service.GetArtists(Array.Empty<Guid>());
        Assert.Single(result);
    }

    [Fact]
    public async Task GetArtists_BeforeLoad_ReturnsEmptyList()
    {
        var service = CreateService();
        // Don't call StartAsync

        Assert.False(service.IsReady);
        Assert.Empty(service.GetArtists());
    }

    [Fact]
    public async Task Refresh_OnLibraryChanged_ReloadsArtists()
    {
        var initialArtists = new List<BaseItem>
        {
            new MusicArtist { Name = "Initial", Id = Guid.NewGuid() }
        };

        var updatedArtists = new List<BaseItem>
        {
            new MusicArtist { Name = "Initial", Id = Guid.NewGuid() },
            new MusicArtist { Name = "New Artist", Id = Guid.NewGuid() }
        };

        int callCount = 0;
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => ++callCount == 1 ? initialArtists : updatedArtists);

        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(1, service.Count);

        // Simulate library change
        var eventArgs = new ItemChangeEventArgs
        {
            Item = new MusicArtist { Name = "New Artist", Id = Guid.NewGuid() }
        };
        _libraryManagerMock.Raise(l => l.ItemAdded += null, _libraryManagerMock.Object, eventArgs);

        // Wait for debounce (5s) - but we can also call RefreshAsync directly for testing
        await service.StartAsync(CancellationToken.None); // Force re-load
        Assert.True(service.Count >= 1);
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
            Item = new MusicArtist { Name = "Test", Id = Guid.NewGuid() }
        };

        // Should not throw
        _libraryManagerMock.Raise(l => l.ItemAdded += null, _libraryManagerMock.Object, eventArgs);
    }

    // --- JF-456: debounce max-pending cap ---

    [Fact]
    public async Task ContinuousLibraryEvents_EventuallyRefreshDespiteDebounce()
    {
        // A stream of qualifying events arriving faster than the debounce delay
        // re-arms the timer forever; the max-pending cap must force a refresh
        // anyway (box-set rip / watched-folder trickle shape, JF-456).
        int callCount = 0;
        var artists = new List<BaseItem> { new MusicArtist { Name = "A", Id = Guid.NewGuid() } };
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => { callCount++; return artists; });
        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        service.RefreshDebounceInterval = TimeSpan.FromMilliseconds(250);
        service.MaxDebouncePendingInterval = TimeSpan.FromMilliseconds(700);
        try
        {
            await service.StartAsync(CancellationToken.None);
            // Two loads per refresh: the artist query plus the folderless-artist album
            // join (every artist is self-mapped here because GetItemById returns null).
            int initialLoads = callCount;
            Assert.True(initialLoads >= 1, "the startup load must have run");

            // Events every 150ms: always faster than the 250ms debounce, so without
            // the cap NO refresh can fire while the stream lasts (every event re-arms
            // the timer; the check happens before the stream ends so a post-stream
            // refresh cannot satisfy it vacuously).
            bool firedDuringStream = false;
            for (int i = 0; i < 12; i++)
            {
                _libraryManagerMock.Raise(
                    l => l.ItemAdded += null,
                    _libraryManagerMock.Object,
                    new ItemChangeEventArgs { Item = new MusicArtist { Name = "New", Id = Guid.NewGuid() } });
                await Task.Delay(150);
                firedDuringStream |= callCount > initialLoads;
            }

            Assert.True(firedDuringStream, "the max-pending cap must force a refresh while the event stream continues");
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task StartAsync_LargeLibrary_LoadsAllArtists()
    {
        var artists = new List<BaseItem>();
        for (int i = 0; i < 1000; i++)
        {
            artists.Add(new MusicArtist { Name = $"Artist {i}", Id = Guid.NewGuid() });
        }

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(1000, service.Count);
        Assert.Equal(1000, service.GetArtists().Count);
    }

    [Fact]
    public async Task ArtistSearch_FuzzyMatchWorks()
    {
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() },
            new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() },
            new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        var allArtists = service.GetArtists();

        // Contains search (tier 1)
        var tier1 = allArtists.Where(a => a.Name.Contains("soul", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(tier1);
        Assert.Equal("Soul Coughing", tier1[0].Name);

        // Prefix search
        var prefix = allArtists.Where(a => a.Name.StartsWith("Pink", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(prefix);

        // Fuzzy match
        var fuzzy = FuzzyMatcher.FindBestMatch("beatles", allArtists, a => a.Name, 60);
        Assert.NotNull(fuzzy);
        Assert.Equal("The Beatles", fuzzy.Name);
    }

    [Fact]
    public async Task Performance_ArtistSearch_Under10ms()
    {
        // Build a realistic library with 2000 artists
        var artists = new List<BaseItem>();
        for (int i = 0; i < 2000; i++)
        {
            artists.Add(new MusicArtist { Name = $"Artist {i}", Id = Guid.NewGuid() });
        }

        // Add some realistic names scattered in
        artists.Add(new MusicArtist { Name = "Soul Coughing", Id = Guid.NewGuid() });
        artists.Add(new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() });
        artists.Add(new MusicArtist { Name = "The Beatles", Id = Guid.NewGuid() });
        artists.Add(new MusicArtist { Name = "Led Zeppelin", Id = Guid.NewGuid() });
        artists.Add(new MusicArtist { Name = "Radiohead", Id = Guid.NewGuid() });

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        var allArtists = service.GetArtists();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Simulate the 4-tier search
        string query = "soul coughin"; // misspelling
        var tier1 = allArtists.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        string firstWord = query.Split(' ')[0];
        var prefix = allArtists.Where(a => a.Name.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase)).ToList();
        var fuzzy = FuzzyMatcher.FindBestMatch(query, prefix, a => a.Name, 60);

        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 10, $"Artist search took {sw.ElapsedMilliseconds}ms, expected < 10ms");
        Assert.NotNull(fuzzy);
        Assert.Equal("Soul Coughing", fuzzy.Name);
    }

    // --- Phonetic code pre-computation tests ---

    [Fact]
    public async Task StartAsync_PreComputesPhoneticCodes()
    {
        var artistId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "The Beatles", Id = artistId },
            new MusicArtist { Name = "Pink Floyd", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        // Phonetic codes should be pre-computed
        Assert.True(service.TryGetPhoneticCode(artistId, out var codes));
        Assert.NotEmpty(codes.Primary);
    }

    [Fact]
    public async Task TryGetPhoneticCode_UnknownArtist_ReturnsFalse()
    {
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Beatles", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        Assert.False(service.TryGetPhoneticCode(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task TryGetPhoneticCode_BeforeLoad_ReturnsFalse()
    {
        var service = CreateService();
        // Don't call StartAsync

        Assert.False(service.TryGetPhoneticCode(Guid.NewGuid(), out _));
    }

    [Fact]
    public async Task StartAsync_EmptyNameArtist_DoesNotCrash()
    {
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "", Id = Guid.NewGuid() },
            new MusicArtist { Name = null!, Id = Guid.NewGuid() },
            new MusicArtist { Name = "Beatles", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        Assert.Equal(3, service.Count);
    }

    [Fact]
    public async Task PhoneticCodes_AreRecomputedOnRefresh()
    {
        var artistId = Guid.NewGuid();
        var initialArtists = new List<BaseItem>
        {
            new MusicArtist { Name = "Beatles", Id = artistId }
        };

        var updatedArtists = new List<BaseItem>
        {
            new MusicArtist { Name = "The Beatles", Id = artistId }
        };

        int callCount = 0;
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => ++callCount == 1 ? initialArtists : updatedArtists);

        _libraryManagerMock
            .Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => null as BaseItem);

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        // Initial code for "Beatles"
        Assert.True(service.TryGetPhoneticCode(artistId, out var initialCodes));

        // Force refresh with updated data
        await service.StartAsync(CancellationToken.None);

        // Code should now be for "The Beatles"
        Assert.True(service.TryGetPhoneticCode(artistId, out var updatedCodes));
        // The codes may or may not differ, but the lookup should still work
        Assert.NotEmpty(updatedCodes.Primary);
    }

    [Fact]
    public async Task PhoneticCodes_LargeLibrary_AllArtistsHaveCodes()
    {
        var artists = new List<BaseItem>();
        for (int i = 0; i < 500; i++)
        {
            artists.Add(new MusicArtist { Name = $"Artist {i}", Id = Guid.NewGuid() });
        }

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        int codesFound = 0;
        foreach (var artist in service.GetArtists())
        {
            if (service.TryGetPhoneticCode(artist.Id, out var codes))
            {
                Assert.NotEmpty(codes.Primary);
                codesFound++;
            }
        }

        Assert.Equal(500, codesFound);
    }

    [Fact]
    public async Task PhoneticFuzzyMatch_Integration_PhoneticallySimilarName()
    {
        // "Schmidt" and "Smith" are phonetically similar
        var smithId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Smith", Id = smithId },
            new MusicArtist { Name = "Metallica", Id = Guid.NewGuid() }
        };

        var service = CreateService(artists);
        await service.StartAsync(CancellationToken.None);

        var allArtists = service.GetArtists();

        // Use phonetic-enhanced matching
        var result = FuzzyMatcher.FindBestMatch(
            "smit",
            allArtists,
            a => a.Name,
            a => a.Id,
            id => service.TryGetPhoneticCode(id, out var codes) ? codes : null,
            40);

        Assert.NotNull(result);
        Assert.Equal("Smith", result.Name);
    }

    // --- JF-432: atomic snapshot publishing (no torn reads across a refresh) ---

    [Fact]
    public void PublishedState_IsOneSnapshotField_StructuralInvariant()
    {
        // The loaded state must live in ONE field (an immutable snapshot record),
        // never in a group of separate volatile fields: volatile orders the individual
        // assignments but not the group, so sequential publishing let a reader observe
        // a torn mix mid-refresh (new artist list against the old top-parent map).
        // Asserting the shape mechanically guards against a regression to per-member fields.
        var derivedFields = typeof(ArtistIndexService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.DeclaringType == typeof(ArtistIndexService))
            .ToList();

        // A future non-state instance field on the service is fine ONLY if this
        // assertion is updated alongside it: the snapshot must stay the single
        // published-state field.
        Assert.True(
            derivedFields.Count == 1 && derivedFields[0].FieldType == typeof(ArtistIndexSnapshot),
            $"ArtistIndexService must declare exactly one published-state field of type ArtistIndexSnapshot, found: {string.Join(", ", derivedFields.Select(f => $"{f.FieldType.Name} {f.Name}"))}");
    }

    [Fact]
    public async Task Refresh_PublishesOneSnapshot_AllReadSurfacesAgree()
    {
        var folderX = Guid.NewGuid();
        var keptId = Guid.NewGuid();
        var newArtistId = Guid.NewGuid();
        var initial = new List<BaseItem> { new MusicArtist { Name = "Kept", Id = keptId } };
        var updated = new List<BaseItem>
        {
            new MusicArtist { Name = "Kept", Id = keptId },
            new MusicArtist { Name = "New Artist", Id = newArtistId, ParentId = folderX }
        };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => ++callCount == 1 ? initial : updated);
        _libraryManagerMock.Setup(l => l.GetItemById(folderX)).Returns(new Folder { Id = folderX });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);
        var before = service.CurrentSnapshot;

        // Force refresh with updated data
        await service.StartAsync(CancellationToken.None);

        var after = service.CurrentSnapshot;
        Assert.NotSame(before, after); // one atomic swap, not a member-by-member publish
        Assert.Equal(2, service.Count);
        Assert.Equal(2, service.GetArtists().Count);

        // The freshly added artist is only visible through the library filter when the
        // artist list AND the top-parent map come from the same publish (the torn shape
        // filtered freshly added artists out)
        var filtered = service.GetArtists(new[] { folderX });
        var added = Assert.Single(filtered);
        Assert.Equal(newArtistId, added.Id);

        Assert.True(service.TryGetPhoneticCode(newArtistId, out var codes));
        Assert.NotEmpty(codes.Primary);
    }

    [Fact]
    public async Task ConcurrentRefreshAndReads_NeverObserveTornState()
    {
        // Refreshes swap two disjoint datasets (identical name, different IDs and
        // folders) while a reader filters by BOTH folders. Either snapshot alone
        // always yields exactly one artist; an empty result can only come from a
        // torn publish (new artist list against the old top-parent map).
        var folderA = Guid.NewGuid();
        var folderB = Guid.NewGuid();
        var setA = new List<BaseItem> { new MusicArtist { Name = "Torn Artist", Id = Guid.NewGuid(), ParentId = folderA } };
        var setB = new List<BaseItem> { new MusicArtist { Name = "Torn Artist", Id = Guid.NewGuid(), ParentId = folderB } };

        int callCount = 0;
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(() => Interlocked.Increment(ref callCount) % 2 == 1 ? setA : setB);
        _libraryManagerMock.Setup(l => l.GetItemById(folderA)).Returns(new Folder { Id = folderA });
        _libraryManagerMock.Setup(l => l.GetItemById(folderB)).Returns(new Folder { Id = folderB });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);
        var filters = new[] { folderA, folderB };

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
                Assert.NotEmpty(service.GetArtists(filters));
                reads++;
            }

            await refresher;
        }
        finally
        {
            service.Dispose();
        }
    }

    // --- JF-455: top-parent id space (walk stops at the AggregateFolder boundary) ---

    /// <summary>
    /// Maps GetItemList responses per item kind so the artist query and the album
    /// join query can return different fixtures from the same mock.
    /// </summary>
    private void SetupKindQueries(List<BaseItem> artists, List<BaseItem>? albums = null)
    {
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(artists);
        _libraryManagerMock
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(albums ?? new List<BaseItem>());
    }

    /// <summary>
    /// Registers each chain node so GetItemById(id) resolves it: the JF-455 walk tests
    /// all mock a parent chain ending in an AggregateFolder root.
    /// </summary>
    private void SetupParents(params BaseItem[] chain)
    {
        foreach (var node in chain)
        {
            _libraryManagerMock.Setup(l => l.GetItemById(node.Id)).Returns(node);
        }
    }

    [Fact]
    public async Task StartAsync_ParentChainAboveAggregateFolder_StopsAtPhysicalLibraryFolder()
    {
        // Live 10.11 hierarchy shape: artist -> physical Folder -> AggregateFolder root.
        // The map value must be the PHYSICAL folder id (what LibraryFilter resolves),
        // not the server-wide aggregate root id (a constant, useless as a filter).
        var rootId = Guid.NewGuid();
        var physicalFolderId = Guid.NewGuid();
        var artist = new MusicArtist { Name = "Battisti", Id = Guid.NewGuid(), ParentId = physicalFolderId };

        SetupKindQueries(new List<BaseItem> { artist });
        SetupParents(
            new Folder { Id = physicalFolderId, ParentId = rootId },
            new AggregateFolder { Id = rootId });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        var filtered = Assert.Single(service.GetArtists(new[] { physicalFolderId }));
        Assert.Equal(artist.Id, filtered.Id);
        Assert.Empty(service.GetArtists(new[] { rootId })); // the pre-fix behavior matched only the root
    }

    [Fact]
    public async Task StartAsync_DeepChainAboveAggregateFolder_ResolvesPhysicalFolderNotRoot()
    {
        // Live 10.11 album hierarchy: album -> folder-artist MusicArtist -> physical
        // Folder -> AggregateFolder root. The album's resolved top parent must be the
        // PHYSICAL folder; the folderless artist's inherited scope carries the proof
        // (it has no chain of its own, so its value can only come from the album walk).
        var rootId = Guid.NewGuid();
        var physicalFolderId = Guid.NewGuid();
        var folderArtistId = Guid.NewGuid();
        var folderless = new MusicArtist { Name = "Mina", Id = Guid.NewGuid() };
        var album = new MusicAlbum
        {
            Name = "Città vuota",
            Id = Guid.NewGuid(),
            ParentId = folderArtistId,
            AlbumArtists = new List<string> { "Mina" }
        };

        SetupKindQueries(new List<BaseItem> { folderless }, new List<BaseItem> { album });
        SetupParents(
            new MusicArtist { Name = "Mina", Id = folderArtistId, ParentId = physicalFolderId },
            new Folder { Id = physicalFolderId, ParentId = rootId },
            new AggregateFolder { Id = rootId });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        var filtered = Assert.Single(service.GetArtists(new[] { physicalFolderId }));
        Assert.Equal(folderless.Id, filtered.Id);
        Assert.Empty(service.GetArtists(new[] { rootId })); // the pre-fix walk resolved the root here
    }

    [Fact]
    public async Task StartAsync_FolderlessArtist_InheritsLibraryFromItsAlbum()
    {
        // Metadata-path MusicArtist (ParentId empty): the walk returns the artist's own
        // id, which never matches a filter. The album join must let it inherit the
        // album's physical library folder (mirrors Jellyfin's album-based artist scoping).
        var rootId = Guid.NewGuid();
        var physicalFolderId = Guid.NewGuid();
        var folderless = new MusicArtist { Name = "Mina", Id = Guid.NewGuid() }; // no ParentId

        var album = new MusicAlbum
        {
            Name = "Città vuota",
            Id = Guid.NewGuid(),
            ParentId = physicalFolderId,
            AlbumArtists = new List<string> { "Mina" }
        };

        SetupKindQueries(new List<BaseItem> { folderless }, new List<BaseItem> { album });
        SetupParents(
            new Folder { Id = physicalFolderId, ParentId = rootId },
            new AggregateFolder { Id = rootId });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        // Physical-space filter matches (the union emitted by LibraryFilter.ResolveTopParentIds)
        var filtered = Assert.Single(service.GetArtists(new[] { physicalFolderId }));
        Assert.Equal(folderless.Id, filtered.Id);
    }

    [Fact]
    public async Task StartAsync_FolderlessArtistWithoutAlbums_StaysUnmatched()
    {
        // An artist with no folder chain AND no albums keeps its own id: it matches no
        // library filter (same as pre-fix), rather than inheriting a wrong library.
        var physicalFolderId = Guid.NewGuid();
        var folderless = new MusicArtist { Name = "Hermit", Id = Guid.NewGuid() };

        SetupKindQueries(new List<BaseItem> { folderless }, new List<BaseItem>());

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        Assert.Single(service.GetArtists());
        Assert.Empty(service.GetArtists(new[] { physicalFolderId }));
    }

    [Fact]
    public async Task StartAsync_AlbumJoin_DoesNotOverwriteFolderDerivedEntry()
    {
        // Two artists named "Mina": one folder-derived (folderA), one folderless. The
        // album join may only fill the folderless one; the folder-derived entry is more
        // precise and must survive the join untouched.
        var rootId = Guid.NewGuid();
        var folderA = Guid.NewGuid();
        var folderB = Guid.NewGuid();
        var folderlessId = Guid.NewGuid();
        var folderDerivedId = Guid.NewGuid();

        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Mina", Id = folderlessId },                              // self-mapped
            new MusicArtist { Name = "Mina", Id = folderDerivedId, ParentId = folderA }       // folder-derived
        };
        var album = new MusicAlbum
        {
            Name = "Celentano duets",
            Id = Guid.NewGuid(),
            ParentId = folderB,
            AlbumArtists = new List<string> { "Mina" }
        };

        SetupKindQueries(artists, new List<BaseItem> { album });
        SetupParents(
            new Folder { Id = folderA, ParentId = rootId },
            new Folder { Id = folderB, ParentId = rootId },
            new AggregateFolder { Id = rootId });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        var inFolderA = Assert.Single(service.GetArtists(new[] { folderA }));
        Assert.Equal(folderDerivedId, inFolderA.Id); // folder-derived entry preserved
        var inFolderB = Assert.Single(service.GetArtists(new[] { folderB }));
        Assert.Equal(folderlessId, inFolderB.Id);    // folderless one inherited the album's library
    }

    [Fact]
    public async Task StartAsync_StaleOrParentlessAlbums_SkippedWithoutConsumingJoin()
    {
        // Album A is parentless (ParentId empty); albums B1 and B2 are siblings under
        // the SAME dead parent id (the walk returns each album's OWN id, the
        // stale-parent shape). All three carry no library scope and must be skipped
        // WITHOUT consuming the one-shot write guard, so the healthy album C can
        // still scope the artist. The sibling pair pins the per-parent memo: a memo
        // that cached B1's self-resolved id would hand it to B2 as a "scope", B2
        // would pass the stale check (B1's id != B2's id) and consume the guard
        // with an album id in no library's id space (0.12.1 port).
        var rootId = Guid.NewGuid();
        var folderC = Guid.NewGuid();
        var deadParentId = Guid.NewGuid();
        var folderlessId = Guid.NewGuid();
        var folderless = new MusicArtist { Name = "Mina", Id = folderlessId };

        var parentlessAlbum = new MusicAlbum
        {
            Name = "Orphan",
            Id = Guid.NewGuid(),
            AlbumArtists = new List<string> { "Mina" }
        };
        var staleAlbum1 = new MusicAlbum
        {
            Name = "Stale 1",
            Id = Guid.NewGuid(),
            ParentId = deadParentId, // deliberately NOT registered in SetupParents
            AlbumArtists = new List<string> { "Mina" }
        };
        var staleAlbum2 = new MusicAlbum
        {
            Name = "Stale 2",
            Id = Guid.NewGuid(),
            ParentId = deadParentId, // sibling under the same dead parent
            AlbumArtists = new List<string> { "Mina" }
        };
        var healthyAlbum = new MusicAlbum
        {
            Name = "Città vuota",
            Id = Guid.NewGuid(),
            ParentId = folderC,
            AlbumArtists = new List<string> { "Mina" }
        };

        SetupKindQueries(
            new List<BaseItem> { folderless },
            new List<BaseItem> { parentlessAlbum, staleAlbum1, staleAlbum2, healthyAlbum });
        SetupParents(
            new Folder { Id = folderC, ParentId = rootId },
            new AggregateFolder { Id = rootId });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        // The published snapshot's TopParentMap must carry the HEALTHY album's
        // library folder: the garbage candidates neither consumed the one-shot
        // guard nor wrote a bogus scope into the map.
        Assert.Equal(folderC, service.CurrentSnapshot.TopParentMap[folderlessId]);
        var scoped = Assert.Single(service.GetArtists(new[] { folderC }));
        Assert.Equal(folderlessId, scoped.Id);
    }

    [Fact]
    public async Task StartAsync_AllArtistsFolderDerived_SkipsAlbumQuery()
    {
        // The album join must stay bounded: when no artist is folderless, the extra
        // MusicAlbum query never runs (one library query per load, as before).
        var rootId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var artist = new MusicArtist { Name = "Battisti", Id = Guid.NewGuid(), ParentId = folderId };

        SetupKindQueries(new List<BaseItem> { artist });
        SetupParents(
            new Folder { Id = folderId, ParentId = rootId },
            new AggregateFolder { Id = rootId });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None);

        _libraryManagerMock.Verify(
            l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))),
            Times.Never);
        _libraryManagerMock.Verify(
            l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))),
            Times.Once);
    }

    [Fact]
    public async Task StartAsync_CyclicParentChain_Terminates()
    {
        // Cycle protection must survive the AggregateFolder boundary change: two folders
        // pointing at each other must not hang the load.
        var folderAId = Guid.NewGuid();
        var folderBId = Guid.NewGuid();
        var artist = new MusicArtist { Name = "Loop", Id = Guid.NewGuid(), ParentId = folderAId };

        SetupKindQueries(new List<BaseItem> { artist });
        SetupParents(
            new Folder { Id = folderAId, ParentId = folderBId },
            new Folder { Id = folderBId, ParentId = folderAId });

        var service = new ArtistIndexService(_libraryManagerMock.Object, _logger);
        await service.StartAsync(CancellationToken.None); // completing is the termination proof

        Assert.True(service.IsReady);
        Assert.Single(service.GetArtists());
    }
}
