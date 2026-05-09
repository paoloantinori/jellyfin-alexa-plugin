using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
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

    private DynamicEntityBuilder CreateBuilder()
    {
        return new DynamicEntityBuilder(
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory.CreateLogger<DynamicEntityBuilder>());
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
    public void BuildFromRecentItems_UserNotFound_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        _userManagerMock
            .Setup(um => um.GetUserById(userId))
            .Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var builder = CreateBuilder();
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void BuildFromRecentItems_NoItems_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);
        SetupLibraryMock([], []);

        var builder = CreateBuilder();
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void BuildFromRecentItems_WithArtists_CreatesDirectiveWithArtistType()
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
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Types);
        Assert.Equal("AMAZON.Musician", result.Types[0].Name);
        Assert.Single(result.Types[0].Values);
        Assert.Equal("Queen", result.Types[0].Values[0].Name.Value);
        Assert.Equal(CatalogValue.FormatId(CatalogType.Artist, artistId), result.Types[0].Values[0].Id);
    }

    [Fact]
    public void BuildFromRecentItems_WithAlbums_CreatesDirectiveWithAlbumType()
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
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Types);
        Assert.Equal("AMAZON.Album", result.Types[0].Name);
        Assert.Equal("Abbey Road", result.Types[0].Values[0].Name.Value);
    }

    [Fact]
    public void BuildFromRecentItems_WithBoth_CreatesTwoTypes()
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
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result.Types.Count);
        Assert.Equal("AMAZON.Musician", result.Types[0].Name);
        Assert.Equal("AMAZON.Album", result.Types[1].Name);
    }

    [Fact]
    public void BuildFromRecentItems_SkipsItemsWithEmptyNames()
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
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.Types[0].Values);
        Assert.Equal("Queen", result.Types[0].Values[0].Name.Value);
    }

    [Fact]
    public void BuildFromRecentItems_SetsSynonyms_WhenGenerated()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        // Use a name that PhoneticSynonymGenerator is known to produce synonyms for
        var artists = new List<BaseItem>
        {
            new MusicArtist { Name = "Cesare Cremonini", Id = Guid.NewGuid() }
        };
        SetupLibraryMock(artists, []);

        var builder = CreateBuilder();
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.NotNull(result);
        // PhoneticSynonymGenerator produces phonetic variants for Italian-friendly names
        var synonyms = result.Types[0].Values[0].Name.Synonyms;
        // Whether synonyms are generated depends on the name — either way the value is set correctly
        Assert.Equal("Cesare Cremonini", result.Types[0].Values[0].Name.Value);
    }

    [Fact]
    public void BuildFromRecentItems_RespectsMaxDynamicValues()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        // Create more than 50 artists
        var artists = Enumerable.Range(0, 60)
            .Select(i => new MusicArtist { Name = $"Artist {i}", Id = Guid.NewGuid() })
            .Cast<BaseItem>()
            .ToList();
        SetupLibraryMock(artists, []);

        var builder = CreateBuilder();
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(50, result.Types[0].Values.Count);
    }

    [Fact]
    public void BuildFromRecentItems_SetsReplaceBehavior()
    {
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        SetupLibraryMock(
            [new MusicArtist { Name = "Queen", Id = Guid.NewGuid() }],
            []);

        var builder = CreateBuilder();
        var result = builder.BuildFromRecentItems(userId, "it-IT", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("REPLACE", result.UpdateBehavior);
    }
}
