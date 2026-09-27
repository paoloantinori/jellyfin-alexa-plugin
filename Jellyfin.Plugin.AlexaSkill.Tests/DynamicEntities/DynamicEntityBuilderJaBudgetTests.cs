#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.DynamicEntities;

/// <summary>
/// JF-646 review finding: the ja kana variants multiply every artist's cost
/// against the SHARED dynamic budget (90 total, 85 after the last-played
/// reserve, spent across artists then albums then series at 1+synonyms per
/// value), so a kana-enriched artist list starves the album surface. The ja
/// dynamic arm is capped at the kana-first pair; the full kana coverage lives
/// in the budget-free catalog upload.
/// </summary>
public class DynamicEntityBuilderJaBudgetTests
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicEntityBuilderJaBudgetTests()
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
        var jellyfinUser = TestHelpers.CreateJellyfinUser();
        _userManagerMock
            .Setup(um => um.GetUserById(userId))
            .Returns(jellyfinUser);
    }

    [Fact]
    public void Build_JaLocale_ManyKanaArtists_StillCarriesAlbumValues()
    {
        // Arrange: 20 article+multiword artists (every one kana-enriched, cost
        // 1+4..6 without the cap, which would exhaust the 85 base budget before
        // the album query runs) and 5 albums.
        var userId = Guid.NewGuid();
        SetupUserMock(userId);

        List<BaseItem> artists = Enumerable.Range(1, 20)
            .Select(i => (BaseItem)new MusicArtist { Name = $"The Artist Number {i}", Id = Guid.NewGuid() })
            .ToList();
        List<BaseItem> albums = Enumerable.Range(1, 5)
            .Select(i => (BaseItem)new MusicAlbum { Name = $"Album Number {i}", Id = Guid.NewGuid() })
            .ToList();

        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist) ? artists
                : q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum) ? albums
                : Array.Empty<BaseItem>());

        var builder = CreateBuilder();

        // Act
        var directive = builder.Build(userId, "ja-JP", null, CancellationToken.None);

        // Assert: the surface exists, every artist value carries at most the
        // kana-first pair, and the albums still made it into the payload.
        Assert.NotNull(directive);

        var allValues = directive!.Types.SelectMany(t => t.Values).ToList();
        var artistValues = allValues.Where(v => artists.Any(a => a.Name == v.Name.Value)).ToList();
        Assert.True(artistValues.Count >= 15, $"expected a broad artist surface, got {artistValues.Count}");
        Assert.All(
            artistValues,
            v => Assert.True(
                (v.Name.Synonyms?.Count ?? 0) <= 2,
                $"artist '{v.Name.Value}' carries {v.Name.Synonyms?.Count ?? 0} dynamic synonyms"));

        var valueNames = allValues.Select(v => v.Name.Value).ToHashSet();
        Assert.Contains("Album Number 1", valueNames);
        Assert.Contains("Album Number 5", valueNames);
    }
}
