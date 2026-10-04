using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using User = Jellyfin.Plugin.AlexaSkill.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Characterization tests for the JF-315 cluster-E search/fuzzy members
/// (SafeGetItemsResult, GetSearchResponseMode, FuzzyMatchPhonetic,
/// GetArtistSongsAsync, SearchItemsFuzzyAsync), written green against the
/// pre-extraction BaseHandler code BEFORE the move to SearchService (JF-315 batch 6)
/// and retargeted to the collaborator after the move. The thin spots pinned here: the
/// SafeGetItemsResult NRE fallback (previously covered by NO test), the
/// GetSearchResponseMode per-user/global resolution, the FuzzyMatchPhonetic null-index
/// degradation, and the exact InternalItemsQuery shapes
/// GetArtistSongsAsync/SearchItemsFuzzyAsync build (the JF-358
/// IncludeItemTypes=Audio invariant and the JF-382 shared shape). FuzzyMatch
/// threshold resolution is NOT pinned here: FuzzyMatchConfigurationTests already
/// covers it through the same member.
/// </summary>
[Collection("Plugin")]
public class SearchServiceTests : PluginTestBase
{
    private readonly ILoggerFactory _loggerFactory;

    public SearchServiceTests()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
    }

    // ---------------------------------------------------------------------
    // SafeGetItemsResult
    // ---------------------------------------------------------------------

    [Fact]
    public void SafeGetItemsResult_Success_PassesThroughGetItemsResult()
    {
        var libraryManager = new Mock<ILibraryManager>();
        var expected = new QueryResult<BaseItem>(0, 1, new List<BaseItem> { TestHelpers.CreateSong("Song") });
        libraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns(expected);
        var search = CreateSearchService(new PluginConfiguration());

        QueryResult<BaseItem> result = search.SafeGetItemsResult(libraryManager.Object, new InternalItemsQuery());

        Assert.Same(expected, result);
        libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void SafeGetItemsResult_NreFallsBackToGetItemList_WrappingStartIndex()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        var items = new List<BaseItem> { TestHelpers.CreateSong("A"), TestHelpers.CreateSong("B") };
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);
        var query = new InternalItemsQuery { StartIndex = 7 };
        var search = CreateSearchService(new PluginConfiguration());

        QueryResult<BaseItem> result = search.SafeGetItemsResult(libraryManager.Object, query);

        Assert.Equal(7, result.StartIndex);
        Assert.Equal(2, result.TotalRecordCount);
        Assert.Same(items, result.Items);
    }

    // JF-673: the opt-in honest fallback total. The DEFAULT above keeps the page-size
    // total (other callers read TotalRecordCount as a real count: the album
    // count-only query stores it directly, where a sentinel would poison the
    // pick-most-tracks choice); pagination-loop callers (the audiobook continuation
    // head) opt in and get the end-unknown regime value instead of a "complete"-
    // looking page size that stops the loop one page in.
    [Fact]
    public void SafeGetItemsResult_NreFallback_UnknownTotalOptIn_ReportsEndUnknown()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Throws(new NullReferenceException());
        var items = new List<BaseItem> { TestHelpers.CreateSong("A"), TestHelpers.CreateSong("B") };
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(items);
        var query = new InternalItemsQuery { StartIndex = 7 };
        var search = CreateSearchService(new PluginConfiguration());

        QueryResult<BaseItem> result = search.SafeGetItemsResult(libraryManager.Object, query, unknownTotalOnFallback: true);

        Assert.Equal(7, result.StartIndex);
        Assert.Equal(SearchService.UnknownTotal, result.TotalRecordCount);
        Assert.Equal(2, result.Items.Count);
    }

    // ---------------------------------------------------------------------
    // GetSearchResponseMode
    // ---------------------------------------------------------------------

    [Fact]
    public void GetSearchResponseMode_PerUserOverrideWins()
    {
        var config = new PluginConfiguration { DefaultSearchResponseMode = SearchResponseMode.Thorough };
        var search = CreateSearchService(config);
        var user = new User { SearchResponseMode = SearchResponseMode.Fast };

        Assert.Equal(SearchResponseMode.Fast, search.GetSearchResponseMode(user));
    }

    [Fact]
    public void GetSearchResponseMode_NullUserFallsBackToGlobalDefault()
    {
        var config = new PluginConfiguration { DefaultSearchResponseMode = SearchResponseMode.Fast };
        var search = CreateSearchService(config);

        Assert.Equal(SearchResponseMode.Fast, search.GetSearchResponseMode(null));
    }

    // ---------------------------------------------------------------------
    // FuzzyMatchPhonetic
    // ---------------------------------------------------------------------

    [Fact]
    public void FuzzyMatchPhonetic_NullIndex_DegradesToPlainFuzzyMatch()
    {
        var search = CreateSearchService(new PluginConfiguration());
        var candidates = new[] { new TestCandidate("The Beatles", Guid.NewGuid()) };

        TestCandidate? result = search.FuzzyMatchPhonetic(
            "Beatles", candidates, c => c.Name, c => c.Id, artistIndex: null, user: null, threshold: -1);

        Assert.NotNull(result);
        Assert.Equal("The Beatles", result!.Name);
    }

    [Fact]
    public void FuzzyMatchPhonetic_ExplicitThresholdHonored()
    {
        var search = CreateSearchService(new PluginConfiguration());
        var candidates = new[] { new TestCandidate("The Beatles", Guid.NewGuid()) };

        TestCandidate? result = search.FuzzyMatchPhonetic(
            "Beatles", candidates, c => c.Name, c => c.Id, artistIndex: null, user: null, threshold: 100);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------------
    // GetArtistSongsAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetArtistSongsAsync_BuildsSharedArtistSongsQueryShape()
    {
        var libraryManager = new Mock<ILibraryManager>();
        InternalItemsQuery? captured = null;
        var items = new List<BaseItem> { TestHelpers.CreateSong("Song") };
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(items);
        var search = CreateSearchService(new PluginConfiguration());
        var artistIds = new[] { Guid.NewGuid() };

        var results = await search.GetArtistSongsAsync(
            jellyfinUser: null,
            user: new User(),
            libraryManager: libraryManager.Object,
            artistIds: artistIds,
            retryLabel: "TestArtistSongs",
            cancellationToken: CancellationToken.None,
            nameContains: "love",
            limit: 500);

        Assert.Same(items, results);
        Assert.NotNull(captured);
        Assert.Equal(artistIds, captured!.ArtistIds);
        // JF-358 invariant: artist-scoped audio queries filter by IncludeItemTypes=Audio,
        // NEVER MediaTypes=Audio (the member leaves the library's default empty array).
        Assert.Equal(new[] { BaseItemKind.Audio }, captured.IncludeItemTypes);
        Assert.Empty(captured.MediaTypes);
        Assert.True(captured.Recursive);
        Assert.Equal("love", captured.NameContains);
        Assert.Equal(500, captured.Limit);
    }

    [Fact]
    public async Task GetArtistSongsAsync_WithoutOptionalFilters_LeavesQueryBare()
    {
        var libraryManager = new Mock<ILibraryManager>();
        InternalItemsQuery? captured = null;
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem>());
        var search = CreateSearchService(new PluginConfiguration());

        await search.GetArtistSongsAsync(
            jellyfinUser: null,
            user: new User(),
            libraryManager: libraryManager.Object,
            artistIds: new[] { Guid.NewGuid() },
            retryLabel: "TestArtistSongs",
            cancellationToken: CancellationToken.None,
            nameContains: null,
            limit: null);

        Assert.NotNull(captured);
        Assert.Null(captured!.NameContains);
        Assert.Null(captured.Limit);
    }

    // ---------------------------------------------------------------------
    // SearchItemsFuzzyAsync
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SearchItemsFuzzyAsync_ShortQuery_ReturnsNullWithoutLibraryCall()
    {
        var libraryManager = new Mock<ILibraryManager>();
        var search = CreateSearchService(new PluginConfiguration());

        var match = await search.SearchItemsFuzzyAsync(
            query: "ab",
            jellyfinUser: null,
            user: new User(),
            libraryManager: libraryManager.Object,
            itemTypes: new[] { BaseItemKind.MusicAlbum },
            cancellationToken: CancellationToken.None);

        Assert.Null(match);
        libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task SearchItemsFuzzyAsync_BuildsBoundedFallbackQueryShape()
    {
        var libraryManager = new Mock<ILibraryManager>();
        InternalItemsQuery? captured = null;
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem>()); // zero candidates: no match, shape still capturable
        var search = CreateSearchService(new PluginConfiguration());
        var artistIds = new[] { Guid.NewGuid() };

        var match = await search.SearchItemsFuzzyAsync(
            query: "dark side",
            jellyfinUser: null,
            user: new User(),
            libraryManager: libraryManager.Object,
            itemTypes: new[] { BaseItemKind.MusicAlbum },
            cancellationToken: CancellationToken.None,
            operationLabel: "TestFuzzyFallback",
            artistIds: artistIds,
            mediaTypes: new[] { MediaType.Audio });

        Assert.Null(match);
        Assert.NotNull(captured);
        Assert.Equal(new[] { BaseItemKind.MusicAlbum }, captured!.IncludeItemTypes);
        Assert.Equal(artistIds, captured.ArtistIds);
        // JF-667 (JF-358): MediaTypes does not constrain an ArtistIds query, so the
        // helper suppresses it when artist scoping is present (the two filters must
        // never ride the same query).
        TestHelpers.AssertNoMediaTypesFilter(captured!, "ArtistIds-scoped fallback query");
        Assert.Equal(500, captured.Limit);
        Assert.True(captured.Recursive);
    }

    [Fact]
    public async Task SearchItemsFuzzyAsync_MediaTypesApply_WhenNoArtistIds()
    {
        // JF-667 companion pin: without artist scoping, MediaTypes still filters
        // (the PlayRadioIntentHandler station-lookup shape).
        var libraryManager = new Mock<ILibraryManager>();
        InternalItemsQuery? captured = null;
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<BaseItem>());
        var search = CreateSearchService(new PluginConfiguration());

        var match = await search.SearchItemsFuzzyAsync(
            query: "jazz",
            jellyfinUser: null,
            user: new User(),
            libraryManager: libraryManager.Object,
            itemTypes: new[] { BaseItemKind.Audio },
            cancellationToken: CancellationToken.None,
            operationLabel: "TestFuzzyFallback",
            mediaTypes: new[] { MediaType.Audio });

        Assert.Null(match);
        Assert.NotNull(captured);
        Assert.Equal(new[] { MediaType.Audio }, captured!.MediaTypes);
        Assert.True(captured.ArtistIds == null || captured.ArtistIds.Length == 0);
    }

    // ---------------------------------------------------------------------
    // the composition seam
    // ---------------------------------------------------------------------

    [Fact]
    public void Search_Property_Wired_By_BaseHandler_Ctor()
    {
        var handler = new SearchWiringProbe(new Mock<ISessionManager>().Object, new PluginConfiguration(), _loggerFactory);

        Assert.NotNull(handler.Search);
        // One instance per handler, not per call.
        Assert.Same(handler.Search, handler.Search);
    }

    // ---------------------------------------------------------------------
    // ResolveKanaGenreTagAsync (the JF-643 kana genre-resolution tier, lifted
    // from PlayByGenreIntentHandler by JF-645 item 2)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ResolveKanaGenreTagAsync_KanaSlot_ResolvesLatinTag_JF645()
    {
        // 'ジャズ' romanizes to 'jazu'; the phonetic matcher bridges it to 'Jazz'
        // (both share the Double Metaphone code, the load-bearing JF-643 property).
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => GenreFlow.IsVocabularyQuery(q) ? (IReadOnlyList<BaseItem>)GenreFlow.VocabularyRows() : Array.Empty<BaseItem>());
        var search = CreateSearchService(new PluginConfiguration());

        string? resolved = await search.ResolveKanaGenreTagAsync(
            canonicalGenre: null, rawGenreSlot: "ジャズ",
            jellyfinUser: null, user: null, libraryManager: libraryManager.Object,
            vocabularyCache: null, cancellationToken: CancellationToken.None);

        Assert.Equal("Jazz", resolved);
    }

    [Fact]
    public async Task ResolveKanaGenreTagAsync_LatinSlot_ReturnsNullWithoutVocabularyFetch_JF645()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new InvalidOperationException("no query expected"));
        var search = CreateSearchService(new PluginConfiguration());

        string? resolved = await search.ResolveKanaGenreTagAsync(
            canonicalGenre: null, rawGenreSlot: "jazzz",
            jellyfinUser: null, user: null, libraryManager: libraryManager.Object,
            vocabularyCache: null, cancellationToken: CancellationToken.None);

        Assert.Null(resolved);
        libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task ResolveKanaGenreTagAsync_ErCanonicalSlot_ReturnsNullWithoutVocabularyFetch_JF645()
    {
        // The tier is the ER_NO_MATCH long-tail path: a resolved canonical is
        // exact by construction and must not trigger the vocabulary scan even
        // when the raw slot is katakana (the JF-642 gate).
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Throws(new InvalidOperationException("no query expected"));
        var search = CreateSearchService(new PluginConfiguration());

        string? resolved = await search.ResolveKanaGenreTagAsync(
            canonicalGenre: "Jazz", rawGenreSlot: "ジャズ",
            jellyfinUser: null, user: null, libraryManager: libraryManager.Object,
            vocabularyCache: null, cancellationToken: CancellationToken.None);

        Assert.Null(resolved);
        libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task ResolveKanaGenreTagAsync_UnrelatedKanaSlot_ReturnsNull_JF645()
    {
        // 'クラシック' (kurashikku, classical) shares no code with the vocabulary:
        // the honest no-match outcome the tier's callers fall through on.
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) => GenreFlow.IsVocabularyQuery(q) ? (IReadOnlyList<BaseItem>)GenreFlow.VocabularyRows() : Array.Empty<BaseItem>());
        var search = CreateSearchService(new PluginConfiguration());

        string? resolved = await search.ResolveKanaGenreTagAsync(
            canonicalGenre: null, rawGenreSlot: "クラシック",
            jellyfinUser: null, user: null, libraryManager: libraryManager.Object,
            vocabularyCache: null, cancellationToken: CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveKanaGenreTagAsync_EmptyVocabulary_ReturnsNull_JF645()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        var search = CreateSearchService(new PluginConfiguration());

        string? resolved = await search.ResolveKanaGenreTagAsync(
            canonicalGenre: null, rawGenreSlot: "ジャズ",
            jellyfinUser: null, user: null, libraryManager: libraryManager.Object,
            vocabularyCache: null, cancellationToken: CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveKanaGenreTagAsync_VocabularyCacheHit_SkipsSecondFetch_JF645()
    {
        // JF-645 item 3: with a cache, the second kana request for the same scope
        // resolves from the cached vocabulary with NO second library query.
        int vocabularyFetches = 0;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                if (GenreFlow.IsVocabularyQuery(q))
                {
                    vocabularyFetches++;
                    return (IReadOnlyList<BaseItem>)GenreFlow.VocabularyRows();
                }

                return Array.Empty<BaseItem>();
            });
        var search = CreateSearchService(new PluginConfiguration());
        var cache = new GenreVocabularyCache();

        string? first = await search.ResolveKanaGenreTagAsync(null, "ジャズ", null, null, libraryManager.Object, cache, CancellationToken.None);
        string? second = await search.ResolveKanaGenreTagAsync(null, "ロック", null, null, libraryManager.Object, cache, CancellationToken.None);

        Assert.Equal("Jazz", first);
        Assert.Equal("Rock", second);
        Assert.Equal(1, vocabularyFetches);
    }

    [Fact]
    public async Task ResolveKanaGenreTagAsync_EmptyVocabularyNeverCached_RefetchesEachCall_JF645()
    {
        // The tier's empty outcome is its no-match null and is re-derived per
        // request: an empty vocabulary is never cached (a later library scan can
        // add tags within the TTL window).
        int vocabularyFetches = 0;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                vocabularyFetches++;
                return Array.Empty<BaseItem>();
            });
        var search = CreateSearchService(new PluginConfiguration());
        var cache = new GenreVocabularyCache();

        await search.ResolveKanaGenreTagAsync(null, "ジャズ", null, null, libraryManager.Object, cache, CancellationToken.None);
        await search.ResolveKanaGenreTagAsync(null, "ジャズ", null, null, libraryManager.Object, cache, CancellationToken.None);

        Assert.Equal(2, vocabularyFetches);
        Assert.Equal(0, cache.Count);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private SearchService CreateSearchService(PluginConfiguration config)
        => new(config, _loggerFactory.CreateLogger<SearchServiceTests>(), requestTimeoutMs: 6000);

    /// <summary>
    /// Minimal probe pinning the BaseHandler ctor's Search composition (not the
    /// collaborator's own behavior, which the facts above test directly).
    /// </summary>
    private class SearchWiringProbe : BaseHandler
    {
        public SearchWiringProbe(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(
            Request request, Context context, Entities.User user,
            SessionInfo session, CancellationToken cancellationToken)
            => Task.FromResult(ResponseBuilder.Empty());
    }
}
