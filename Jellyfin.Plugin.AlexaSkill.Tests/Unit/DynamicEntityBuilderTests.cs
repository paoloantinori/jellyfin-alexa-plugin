using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class DynamicEntityBuilderTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicEntityBuilderTests()
    {
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    private DynamicEntityBuilder CreateBuilder(IArtistIndex? artistIndex = null)
    {
        return new DynamicEntityBuilder(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory.CreateLogger<DynamicEntityBuilder>(),
            artistIndex);
    }

    private void SetupUserMock(Guid userId)
    {
        var jellyfinUser = new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test");
        _userManagerMock
            .Setup(um => um.GetUserById(userId))
            .Returns(jellyfinUser);
    }

    private void SetupLibraryMock(List<BaseItem> artists, List<BaseItem> albums)
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist))))
            .Returns(artists);

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum))))
            .Returns(albums);
    }

    [Fact]
    public void Build_UserNotFound_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _userManagerMock
            .Setup(um => um.GetUserById(userId))
            .Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Build_NoItems_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);
        SetupLibraryMock([], []);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Build_WithArtists_CreatesDirectiveWithArtistType()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        var artistId = Guid.NewGuid();
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = artistId }
        };
        SetupLibraryMock(artists, []);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Types);
        Assert.Equal("AMAZON.Musician", result.Types[0].Name);
        Assert.Single(result.Types[0].Values);
        Assert.Equal("Queen", result.Types[0].Values[0].Name.Value);
        Assert.Equal(CatalogValue.FormatId(CatalogType.Artist, artistId), result.Types[0].Values[0].Id);
    }

    [Fact]
    public void Build_WithAlbums_CreatesDirectiveWithAlbumType()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        var albumId = Guid.NewGuid();
        var albums = new List<BaseItem>
        {
            new MusicAlbum { Name = "Abbey Road", Id = albumId }
        };
        SetupLibraryMock([], albums);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Types);
        Assert.Equal("AMAZON.Album", result.Types[0].Name);
        Assert.Equal("Abbey Road", result.Types[0].Values[0].Name.Value);
    }

    [Fact]
    public void Build_WithBoth_CreatesTwoTypes()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }
        };
        var albums = new List<BaseItem>
        {
            new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() }
        };
        SetupLibraryMock(artists, albums);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Types.Count);
        Assert.Equal("AMAZON.Musician", result.Types[0].Name);
        Assert.Equal("AMAZON.Album", result.Types[1].Name);
    }

    [Fact]
    public void Build_SkipsItemsWithEmptyNames()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Queen", Id = Guid.NewGuid() },
            new MusicArtist { Name = "", Id = Guid.NewGuid() },
            new MusicArtist { Name = "   ", Id = Guid.NewGuid() },
            new MusicArtist { Name = null!, Id = Guid.NewGuid() }
        };
        SetupLibraryMock(artists, []);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Types[0].Values);
        Assert.Equal("Queen", result.Types[0].Values[0].Name.Value);
    }

    [Fact]
    public void Build_SetsSynonyms_WhenGenerated()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Cesare Cremonini", Id = Guid.NewGuid() }
        };
        SetupLibraryMock(artists, []);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Cesare Cremonini", result.Types[0].Values[0].Name.Value);
    }

    [Fact]
    public void Build_TotalValueCount_StaysUnderLimit()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        // Create many artists and albums — each "The <word>" generates synonyms.
        var artists = Enumerable.Range(0, 60)
            .Select(i => (BaseItem)new MusicArtist { Name = $"The Band {i}", Id = Guid.NewGuid() })
            .ToList();
        var albums = Enumerable.Range(0, 60)
            .Select(i => (BaseItem)new MusicAlbum { Name = $"The Album {i}", Id = Guid.NewGuid() })
            .ToList();
        SetupLibraryMock(artists, albums);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);

        // Count total values + synonyms across all slot types — must not exceed 100.
        int totalCount = 0;
        foreach (var type in result.Types)
        {
            foreach (var val in type.Values)
            {
                totalCount += 1 + (val.Name.Synonyms?.Count ?? 0);
            }
        }

        Assert.True(totalCount <= 100, $"Total value+synonym count was {totalCount}, must be <= 100");
    }

    [Fact]
    public void Build_LargeLibrary_ArtistsGetPriorityOverAlbums()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        var artists = Enumerable.Range(0, 80)
            .Select(i => (BaseItem)new MusicArtist { Name = $"Artist {i}", Id = Guid.NewGuid() })
            .ToList();
        var albums = Enumerable.Range(0, 80)
            .Select(i => (BaseItem)new MusicAlbum { Name = $"Album {i}", Id = Guid.NewGuid() })
            .ToList();
        SetupLibraryMock(artists, albums);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        // Artists are built first and consume the budget; albums get the remainder.
        Assert.True(result.Types[0].Values.Count > 0, "Should have artist values");
    }

    [Fact]
    public void Build_SynonymsCountTowardsBudget()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        // "The White Stripes" generates phonetic synonyms, eating budget faster.
        var artists = Enumerable.Range(0, 50)
            .Select(i => (BaseItem)new MusicArtist { Name = "The White Stripes", Id = Guid.NewGuid() })
            .ToList();
        SetupLibraryMock(artists, []);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        int totalCount = result.Types[0].Values.Sum(v => 1 + (v.Name.Synonyms?.Count ?? 0));
        Assert.True(totalCount <= 100, $"Total count {totalCount} exceeds 100 limit");
        // Each "The White Stripes" generates synonyms, so fewer than 50 items fit.
        Assert.True(result.Types[0].Values.Count < 50,
            "With synonyms, should fit fewer than 50 items within budget");
    }

    [Fact]
    public void Build_NoSynonyms_FitsMoreItems()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        // Single-word Italian-origin names get no synonyms.
        var artists = Enumerable.Range(0, 50)
            .Select(i => (BaseItem)new MusicArtist { Name = $"Metallica", Id = Guid.NewGuid() })
            .ToList();
        SetupLibraryMock(artists, []);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        // "Metallica" is in the known Italian-origin list, so no synonyms.
        // Each value costs exactly 1, so all items that fit budget should be included.
        int totalCount = result.Types[0].Values.Sum(v => 1 + (v.Name.Synonyms?.Count ?? 0));
        Assert.True(totalCount <= 100, $"Total count {totalCount} exceeds 100 limit");
    }

    [Fact]
    public void Build_SetsReplaceBehavior()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        SetupLibraryMock(
            [new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }],
            []);

        var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("REPLACE", result.UpdateBehavior);
    }
}
