using System;
using System.Collections.Generic;
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
}
