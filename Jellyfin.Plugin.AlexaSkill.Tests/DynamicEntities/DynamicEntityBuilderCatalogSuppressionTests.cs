#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.DynamicEntities;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.DynamicEntities;

/// <summary>
/// JF-826: the session <c>Dialog.UpdateDynamicEntities</c> push must not REPLACE
/// the vocabulary of a slot type the user's catalog sync already wires into the
/// saved model. The directive carries <c>updateBehavior: REPLACE</c>, so any type
/// it names trades the model's catalog supplier (full library + seed + phonetic
/// synonyms, the JF-493 SeriesName and JF-823 AudiobookTitle shapes) for the
/// shared-90-value-budget truncation, and catalog values outside that budget stop
/// resolving for the REST OF THE SESSION (the JF-684 selection-gating risk
/// in-session). The builder suppresses the push per TYPE NAME once the user's
/// stored catalog id for that type exists and the locale can host the wiring;
/// these pins hold that mechanism on every arm that can write those names,
/// including the last-played distribution (the sneakier arm: one recently played
/// book replaced the whole 383-title catalog on every NEW session).
/// </summary>
[Collection("Plugin")]
public class DynamicEntityBuilderCatalogSuppressionTests : PluginTestBase
{
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly ILoggerFactory _loggerFactory;

    public DynamicEntityBuilderCatalogSuppressionTests()
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

    private void SetupUser(Guid userId, Action<User>? configureCatalogIds = null)
    {
        _userManagerMock
            .Setup(um => um.GetUserById(userId))
            .Returns(TestHelpers.CreateJellyfinUser(id: userId));

        var pluginUser = new User { Id = userId };
        configureCatalogIds?.Invoke(pluginUser);

        var config = new PluginConfiguration();
        config.Users.Add(pluginUser);
        TestHelpers.EnsurePluginInstance(
            config,
            _loggerFactory,
            c => c.Users.Add(pluginUser),
            "jf826-suppression");
    }

    private void SetupLibraryItems(BaseItemKind kind, params BaseItem[] items)
    {
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.IncludeItemTypes.Contains(kind))))
            .Returns(items.ToList());
    }

    private void SetupLastPlayed(params BaseItem[] items)
    {
        // Registered AFTER the arm setups so this (latest) matching setup wins
        // for the multi-type DatePlayed query, whose IncludeItemTypes also
        // contains AudioBook when books are enabled.
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.OrderBy != null && q.OrderBy.Any(o => o.Item1 == ItemSortBy.DatePlayed))))
            .Returns(items.ToList());
    }

    private static bool HasType(DynamicEntitiesDirective? directive, string slotTypeName) =>
        directive?.Types.Any(t => t.Name == slotTypeName) == true;

    /// <summary>
    /// THE red-green pin (JF-826, the filed defect): a book conversation on a
    /// user whose audiobook catalog is live must not carry the AudiobookTitle
    /// dynamic block. Today's red: the includeAudiobooks arm pushes the
    /// budget-truncated book list onto AudiobookTitle, REPLACING the catalog
    /// vocabulary mid-conversation.
    /// </summary>
    [Fact]
    public void Build_CatalogWiredAudiobook_BookFlowOmitsAudiobookTitle()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u => u.AudiobookCatalogId = "amzn1.catalog.audiobook.test");
        SetupLibraryItems(BaseItemKind.MusicArtist);
        SetupLibraryItems(BaseItemKind.MusicAlbum, new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() });
        SetupLibraryItems(BaseItemKind.AudioBook, new AudioBook { Name = "Murderbot 1", Id = Guid.NewGuid() });

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, includeSeries: false, includeAudiobooks: true, cancellationToken: CancellationToken.None);

        // The album arm (AMAZON.Album is not a catalog-wired name anywhere,
        // JF-332) must still push: suppression is per wired type, never a
        // silent empty-everything.
        Assert.NotNull(result);
        Assert.True(HasType(result, "AMAZON.Album"), "Non-wired types must still push");
        Assert.False(HasType(result, "AudiobookTitle"), "A catalog-wired AudiobookTitle must not be replaced by the dynamic push");
    }

    /// <summary>
    /// The new-session variant (the gate-marker's sharper scenario applies to
    /// LaunchRequest responses too): a recently played book distributes onto
    /// AudiobookTitle via the last-played arm even when includeAudiobooks is
    /// false, so EVERY fresh session after listening to a book replaced the
    /// whole catalog vocabulary with that one title from turn 2. Wired: the
    /// filtered-out value leaves nothing to push, and the builder returns null
    /// instead of an empty directive.
    /// </summary>
    [Fact]
    public void Build_CatalogWiredAudiobook_LastPlayedBookDoesNotReplaceCatalog()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u => u.AudiobookCatalogId = "amzn1.catalog.audiobook.test");
        SetupLibraryItems(BaseItemKind.MusicArtist);
        SetupLibraryItems(BaseItemKind.MusicAlbum);
        SetupLastPlayed(new AudioBook { Name = "Murderbot 1", Id = Guid.NewGuid() });

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.Null(result);
    }

    /// <summary>
    /// The pre-catalog mechanism must survive for users whose sync has not run
    /// (no stored catalog id: fresh install before the first sync, or SMAPI
    /// never linked): there the saved model's vocabulary is the static seed,
    /// and the dynamic library list is strictly better than the seed.
    /// </summary>
    [Fact]
    public void Build_NoCatalogSyncYet_StillPushesAudiobookTitle()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId);
        SetupLibraryItems(BaseItemKind.MusicArtist);
        SetupLibraryItems(BaseItemKind.MusicAlbum, new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() });
        SetupLibraryItems(BaseItemKind.AudioBook, new AudioBook { Name = "Murderbot 1", Id = Guid.NewGuid() });

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, includeSeries: false, includeAudiobooks: true, cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(HasType(result, "AudiobookTitle"), "Without a live catalog the dynamic push stays (library list beats the static seed)");
        Assert.True(HasType(result, "AMAZON.Album"));
    }

    /// <summary>
    /// The code-review F1 crowding shape: a wired type's recent items must be
    /// skipped INSIDE the last-played selection loop, not filtered out
    /// afterwards. Five recent books then a song: the books must neither push
    /// nor consume the 5-slot cap, so the pushable song survives; a post-hoc
    /// filter would have spent every slot on books and dropped the whole block.
    /// </summary>
    [Fact]
    public void Build_CatalogWiredAudiobook_LastPlayedWiredItemsDoNotCrowdOutPushable()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u => u.AudiobookCatalogId = "amzn1.catalog.audiobook.test");
        SetupLibraryItems(BaseItemKind.MusicArtist);
        SetupLibraryItems(BaseItemKind.MusicAlbum);
        var recentBooks = Enumerable.Range(0, 5)
            .Select(i => (BaseItem)new AudioBook { Name = $"Book {i}", Id = Guid.NewGuid() })
            .ToList();
        var song = new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() };
        song.Artists = new List<string> { "Queen" };
        recentBooks.Add(song);
        SetupLastPlayed(recentBooks.ToArray());

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(HasType(result, "AudiobookTitle"), "Wired books must not push");
        Assert.True(HasType(result, "JellyfinArtist"), "The pushable song must survive the wired books' skip");
    }

    /// <summary>
    /// The Series precedent, fixed by the same mechanism (the JF-823 arity
    /// lesson: one mechanism, not a per-type special case). SeriesName has been
    /// catalog-wired since JF-493 in every locale that can host wiring, and the
    /// turn-2+ TV-context push replaced it with the budget-truncated series
    /// list all along; the same silent overwrite the JF-823 review caught for
    /// AudiobookTitle.
    /// </summary>
    [Fact]
    public void Build_CatalogWiredSeries_TvFlowOmitsSeriesName()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u => u.SeriesCatalogId = "amzn1.catalog.series.test");
        SetupLibraryItems(BaseItemKind.MusicArtist);
        SetupLibraryItems(BaseItemKind.MusicAlbum, new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() });
        SetupLibraryItems(
            BaseItemKind.Series,
            new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Breaking Bad", Id = Guid.NewGuid() });

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, includeSeries: true, includeAudiobooks: false, cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(HasType(result, "AMAZON.Album"));
        Assert.False(HasType(result, "SeriesName"), "A catalog-wired SeriesName must not be replaced by the dynamic push");
    }

    /// <summary>
    /// The artist arm on the catalog-backed musician locales (JF-415): the
    /// resolved target is JellyfinArtist, the wired name, so a live artist
    /// catalog suppresses the push (the catalog carries the full library with
    /// uncapped phonetic synonyms; the push carried at most 70 artists).
    /// </summary>
    [Fact]
    public void Build_CatalogWiredArtist_OnCatalogBackedLocale_OmitsJellyfinArtist()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u => u.ArtistCatalogId = "amzn1.catalog.artist.test");
        SetupLibraryItems(BaseItemKind.MusicArtist, new MusicArtist { Name = "Queen", Id = Guid.NewGuid() });
        SetupLibraryItems(BaseItemKind.MusicAlbum, new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() });

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(HasType(result, "JellyfinArtist"), "A catalog-wired JellyfinArtist must not be replaced by the dynamic push");
        Assert.True(HasType(result, "AMAZON.Album"));
    }

    /// <summary>
    /// The contrast twin of the artist pin: on the locales that keep the
    /// AMAZON.Musician built-in (the JF-415 map), the resolved target is not a
    /// catalog-wired name, so the push stays even with a live artist catalog -
    /// there the library list is the only in-session artist vocabulary that
    /// names THIS user's library.
    /// </summary>
    [Fact]
    public void Build_CatalogWiredArtist_OnBuiltInMusicianLocale_StillPushes()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u => u.ArtistCatalogId = "amzn1.catalog.artist.test");
        SetupLibraryItems(BaseItemKind.MusicArtist, new MusicArtist { Name = "Queen", Id = Guid.NewGuid() });
        SetupLibraryItems(BaseItemKind.MusicAlbum);

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "de-DE", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(HasType(result, "AMAZON.Musician"), "The built-in musician target is never a wired name; the push stays");
    }

    /// <summary>
    /// The JF-543 locale gate: ar-SA cannot host catalog-wired models, so its
    /// SeriesName/AudiobookTitle stay static-seed types and the dynamic push is
    /// the only library vocabulary. Stored catalog ids exist there too (the
    /// catalogs are type-scoped, created by the other locales' legs), so the
    /// ids ALONE must never suppress: the locale gate does.
    /// </summary>
    [Fact]
    public void Build_CatalogWired_OnWiringUnsupportedLocale_StillPushes()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u =>
        {
            u.SeriesCatalogId = "amzn1.catalog.series.test";
            u.AudiobookCatalogId = "amzn1.catalog.audiobook.test";
        });
        SetupLibraryItems(BaseItemKind.MusicArtist);
        SetupLibraryItems(BaseItemKind.MusicAlbum, new MusicAlbum { Name = "Abbey Road", Id = Guid.NewGuid() });
        SetupLibraryItems(
            BaseItemKind.Series,
            new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Breaking Bad", Id = Guid.NewGuid() });
        SetupLibraryItems(BaseItemKind.AudioBook, new AudioBook { Name = "Murderbot 1", Id = Guid.NewGuid() });

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "ar-SA", null, includeSeries: true, includeAudiobooks: true, cancellationToken: CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(HasType(result, "SeriesName"), "ar-SA cannot host the wiring; the push is its library vocabulary");
        Assert.True(HasType(result, "AudiobookTitle"));
    }

    /// <summary>
    /// When every arm's type is wired and nothing else remains (no albums, no
    /// last-played on non-wired types), the builder must return null rather
    /// than push an empty directive.
    /// </summary>
    [Fact]
    public void Build_AllWiredTypesSuppressed_NothingRemains_ReturnsNull()
    {
        var userId = Guid.NewGuid();
        SetupUser(userId, u =>
        {
            u.ArtistCatalogId = "amzn1.catalog.artist.test";
            u.SeriesCatalogId = "amzn1.catalog.series.test";
            u.AudiobookCatalogId = "amzn1.catalog.audiobook.test";
        });
        SetupLibraryItems(BaseItemKind.MusicArtist, new MusicArtist { Name = "Queen", Id = Guid.NewGuid() });
        SetupLibraryItems(BaseItemKind.MusicAlbum);
        SetupLibraryItems(
            BaseItemKind.Series,
            new global::MediaBrowser.Controller.Entities.TV.Series { Name = "Breaking Bad", Id = Guid.NewGuid() });
        SetupLibraryItems(BaseItemKind.AudioBook, new AudioBook { Name = "Murderbot 1", Id = Guid.NewGuid() });
        SetupLastPlayed(new AudioBook { Name = "Murderbot 1", Id = Guid.NewGuid() });

        using var builder = CreateBuilder();
        var result = builder.Build(userId, "it-IT", null, includeSeries: true, includeAudiobooks: true, cancellationToken: CancellationToken.None);

        Assert.Null(result);
    }
}
