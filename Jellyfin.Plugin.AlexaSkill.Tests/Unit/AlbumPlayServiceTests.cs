using System;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Characterization tests for the JF-315 cluster-G album/playlist play members
/// (BuildAlbumQuery, TryStripLeadingAlbumCallingWord, TryAlbumFallbackAsync,
/// BuildAlbumPlayResponseAsync, BuildPlaylistPlayResponseAsync), written green
/// against the pre-extraction BaseHandler code BEFORE the move to the
/// AlbumPlayService collaborator (JF-315 batch 8) via a probe subclass, then
/// retargeted to the collaborator after the move. The thin spots pinned here
/// (previously covered only INDIRECTLY through handler tests, or not at all):
/// the compilations-inclusive side of BuildAlbumQuery's artist scoping (the
/// albumArtistsOnly=false branch sets ArtistIds and leaves AlbumArtistIds
/// untouched; the albumArtistsOnly=true side is pinned directly by
/// PlayAlbumIntentHandlerTests) and the whitespace search-term omission (a
/// whitespace-only term must not reach the indexed SearchTerm tier).
/// TryStripLeadingAlbumCallingWord keeps its direct coverage in
/// PlayAlbumIntentHandlerTests (the JF-469 strip table), the JF-345 cascade
/// keeps PlaySongAlbumFallbackTests + the music-gate suites, and the playlist
/// flow keeps PlayPlaylistIntentHandlerTests + DeviceQueueManagerTests
/// (including the visibility filter, the disambiguation arm through the
/// fuzzy-miss delegate seam, and the shuffle arm); those are not re-pinned here.
/// </summary>
[Collection("Plugin")]
public class AlbumPlayServiceTests : PluginTestBase
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });

    // ---------------------------------------------------------------------
    // BuildAlbumQuery (JF-345 the ONE album query shape)
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildAlbumQuery_ArtistIdsWithoutAlbumArtistsOnly_IncludesCompilations()
    {
        var svc = CreateService();
        Guid artistId = Guid.NewGuid();

        InternalItemsQuery q = svc.BuildAlbumQuery(
            new Mock<ILibraryManager>().Object,
            jellyfinUser: null,
            TestHelpers.CreateTestUser(),
            searchTerm: "thriller",
            artistIds: new[] { artistId });

        // The compilations-inclusive side of the artist scoping: ArtistIds matches
        // albums containing a track by the artist; AlbumArtistIds stays untouched
        // (InternalItemsQuery initializes it to an empty array, not null, the same
        // shape as MediaTypes; the by-the-artist side is the albumArtistsOnly=true
        // branch, pinned at the PlayAlbum handler level: the "un disco dei Koop"
        // live finding).
        Assert.NotNull(q.ArtistIds);
        Assert.Contains(artistId, q.ArtistIds);
        Assert.Empty(q.AlbumArtistIds);

        // The ONE shape both the direct album search and the song-to-album cascade
        // run: a recursive MusicAlbum query scoped to the user.
        Assert.True(q.Recursive);
        Assert.Contains(BaseItemKind.MusicAlbum, q.IncludeItemTypes);
        Assert.Equal("thriller", q.SearchTerm);
    }

    [Fact]
    public void BuildAlbumQuery_WhitespaceSearchTerm_NotSet()
    {
        var svc = CreateService();

        InternalItemsQuery q = svc.BuildAlbumQuery(
            new Mock<ILibraryManager>().Object,
            jellyfinUser: null,
            TestHelpers.CreateTestUser(),
            searchTerm: "   ",
            artistIds: null);

        // The IsNullOrWhiteSpace guard: a whitespace-only term must not reach the
        // indexed SearchTerm tier (a null SearchTerm is the broad fuzzy-scan shape).
        Assert.Null(q.SearchTerm);
    }

    // ---------------------------------------------------------------------
    // the composition seam
    // ---------------------------------------------------------------------

    [Fact]
    public void AlbumPlay_Property_Wired_By_BaseHandler_Ctor()
    {
        var handler = new SharedGateProbeHandler(new Mock<ISessionManager>().Object, new PluginConfiguration(), _loggerFactory);

        Assert.NotNull(handler.AlbumPlay);
        // The getter hands out ONE stable instance (not a fresh collaborator per
        // access); the ctor line above is what pins the per-handler wiring.
        Assert.Same(handler.AlbumPlay, handler.AlbumPlay);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private AlbumPlayService CreateService(PluginConfiguration? config = null)
    {
        config ??= new PluginConfiguration();
        ILogger logger = _loggerFactory.CreateLogger<AlbumPlayServiceTests>();
        var launch = new PlaybackLaunchBuilder(config, logger, (_, _, _) => Task.FromResult(false));
        var search = new SearchService(config, logger, requestTimeoutMs: 6000);
        var crossMedia = new CrossMediaFallback(config, logger, launch, requestTimeoutMs: 6000);
        return new AlbumPlayService(
            config, logger, launch, search, crossMedia, requestTimeoutMs: 6000,
            (_, _, _, _, _, _, _, _) => Task.FromResult((BaseHandler.FuzzyMissOutcome.NotFound, (SkillResponse?)null)));
    }
}
