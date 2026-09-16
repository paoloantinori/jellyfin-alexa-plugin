using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch 10 characterization suite for the radio-track family
/// (FindRadioTracksAsync/FindRadioTracksByGenreAsync, Alexa/Util/RadioTrackSource.cs).
/// The family was covered only end-to-end through the PlayRadio/RadioMode tests
/// before the move; these facts pin the DIRECT contracts BEFORE the move: the
/// exact genre query shape (the JF-358-style IncludeItemTypes=Audio invariant,
/// Limit 50, Random ordering), the empty-genre no-query branch, the seed-item
/// exclusion, and the result dedup. Written green on the pre-move BaseHandler
/// code via a probe subclass, then retargeted to direct RadioTrackSource
/// construction after the move (zero expectation edits).
/// </summary>
[Collection("Plugin")]
public class RadioTrackSourceTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new();

    private RadioTrackSource CreateSource()
        => new(_fx.LoggerFactory.CreateLogger<RadioTrackSource>(), 6000);

    private void SetupGenreTracks(params BaseItem[] items)
        => _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items.ToList());

    [Fact]
    public async Task ByGenre_EmptyGenres_ReturnsEmptyWithoutQuery()
    {
        SetupGenreTracks(TestHelpers.CreateSong("should not be queried"));

        IReadOnlyList<BaseItem> result = await CreateSource().FindRadioTracksByGenreAsync(
            Array.Empty<string>(), TestHelpers.CreateJellyfinUser(), TestHelpers.CreateTestUser(),
            _fx.LibraryManager.Object, CancellationToken.None);

        Assert.Empty(result);
        _fx.LibraryManager.Verify(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task ByGenre_BuildsTheRadioGenreQueryShape()
    {
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>())
            .Callback<InternalItemsQuery>(q => queries.Add(q));
        var jellyfinUser = TestHelpers.CreateJellyfinUser();

        await CreateSource().FindRadioTracksByGenreAsync(
            new[] { "Jazz" }, jellyfinUser, TestHelpers.CreateTestUser(),
            _fx.LibraryManager.Object, CancellationToken.None);

        InternalItemsQuery query = Assert.Single(queries);
        Assert.Equal(jellyfinUser, query.User);
        Assert.True(query.Recursive);
        Assert.Equal(new[] { "Jazz" }, query.Genres);
        // JF-358 invariant: Audio filtering is IncludeItemTypes=BaseItemKind.Audio, never MediaTypes=Audio.
        Assert.Equal(new[] { BaseItemKind.Audio }, query.IncludeItemTypes);
        Assert.Equal(50, query.Limit);
        Assert.NotNull(query.OrderBy);
        Assert.Equal(ItemSortBy.Random, Assert.Single(query.OrderBy).Item1);
        Assert.NotNull(query.DtoOptions);
    }

    [Fact]
    public async Task ByGenre_ExcludesTheSeedItem()
    {
        var seed = TestHelpers.CreateSong("seed");
        var other = TestHelpers.CreateSong("other");
        SetupGenreTracks(seed, other);

        IReadOnlyList<BaseItem> result = await CreateSource().FindRadioTracksByGenreAsync(
            new[] { "Jazz" }, TestHelpers.CreateJellyfinUser(), TestHelpers.CreateTestUser(),
            _fx.LibraryManager.Object, CancellationToken.None, excludeId: seed.Id);

        BaseItem single = Assert.Single(result);
        Assert.Equal(other.Id, single.Id);
    }

    [Fact]
    public async Task ByGenre_DeduplicatesRepeatedItems()
    {
        var track = TestHelpers.CreateSong("dup");
        SetupGenreTracks(track, track);

        IReadOnlyList<BaseItem> result = await CreateSource().FindRadioTracksByGenreAsync(
            new[] { "Jazz" }, TestHelpers.CreateJellyfinUser(), TestHelpers.CreateTestUser(),
            _fx.LibraryManager.Object, CancellationToken.None);

        BaseItem single = Assert.Single(result);
        Assert.Equal(track.Id, single.Id);
    }

    [Fact]
    public async Task FindRadioTracks_SeedsFromTheItemsGenresAndExcludesTheItemItself()
    {
        var current = TestHelpers.CreateSong("current");
        current.Genres = new[] { "Rock", "Indie" };
        var other = TestHelpers.CreateSong("other");
        var queries = new List<InternalItemsQuery>();
        _fx.LibraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { current, other })
            .Callback<InternalItemsQuery>(q => queries.Add(q));

        IReadOnlyList<BaseItem> result = await CreateSource().FindRadioTracksAsync(
            current, TestHelpers.CreateJellyfinUser(), TestHelpers.CreateTestUser(),
            _fx.LibraryManager.Object, CancellationToken.None);

        // The seeding item itself is excluded even when the server returns it.
        BaseItem single = Assert.Single(result);
        Assert.Equal(other.Id, single.Id);
        InternalItemsQuery query = Assert.Single(queries);
        Assert.Equal(new[] { "Rock", "Indie" }, query.Genres);
    }
}
