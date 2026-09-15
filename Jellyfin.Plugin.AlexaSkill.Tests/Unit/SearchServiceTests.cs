using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
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
/// (SafeGetItemsResult, CachedSearchAsync, GetSearchResponseMode, FuzzyMatchPhonetic,
/// GetArtistSongsAsync, SearchItemsFuzzyAsync), written green against the
/// pre-extraction BaseHandler code BEFORE the move to SearchService (JF-315 batch 6)
/// and retargeted to the collaborator after the move. The thin spots pinned here: the
/// SafeGetItemsResult NRE fallback (previously covered by NO test), the dead
/// CachedSearchAsync cache-fallback branches (zero coverage anywhere), the
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

    // ---------------------------------------------------------------------
    // CachedSearchAsync (dead in production today: no callers. Pinned here so
    // the extraction moves a characterized body, not an untested one.)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CachedSearchAsync_Success_CachesResults_AndCountsMiss()
    {
        var search = CreateSearchService(new PluginConfiguration());
        var (cache, counters, oldCache, oldCounters) = InstallTestCache();
        try
        {
            var items = new List<BaseItem> { TestHelpers.CreateSong("Song") }.AsReadOnly();
            Guid userId = Guid.NewGuid();

            var (results, fromCache) = await search.CachedSearchAsync(
                userId, "query-key", () => items, "TestSearch", CancellationToken.None);

            Assert.False(fromCache);
            Assert.Same(items, results);
            Assert.Equal(1, counters.CacheMisses);
            Assert.Equal(0, counters.CacheHits);
            Assert.True(cache.TryGet(userId, "query-key", out IReadOnlyList<BaseItem>? cached));
            Assert.Same(items, cached);
        }
        finally
        {
            RestoreTestCache(oldCache, oldCounters);
        }
    }

    [Fact]
    public async Task CachedSearchAsync_NonTransientFailure_ServesCachedResults_AndCountsHit()
    {
        var search = CreateSearchService(new PluginConfiguration());
        var (cache, counters, oldCache, oldCounters) = InstallTestCache();
        try
        {
            var cachedItems = new List<BaseItem> { TestHelpers.CreateSong("Cached") }.AsReadOnly();
            Guid userId = Guid.NewGuid();
            cache.Put(userId, "query-key", cachedItems);

            var (results, fromCache) = await search.CachedSearchAsync(
                userId,
                "query-key",
                () => throw new InvalidOperationException("boom"),
                "TestSearch",
                CancellationToken.None);

            Assert.True(fromCache);
            Assert.Same(cachedItems, results);
            Assert.Equal(1, counters.CacheHits);
            Assert.Equal(0, counters.CacheMisses);
        }
        finally
        {
            RestoreTestCache(oldCache, oldCounters);
        }
    }

    [Fact]
    public async Task CachedSearchAsync_Cancelled_Rethrows_EvenWithCache()
    {
        var search = CreateSearchService(new PluginConfiguration());
        var (cache, _, oldCache, oldCounters) = InstallTestCache();
        try
        {
            var cachedItems = new List<BaseItem> { TestHelpers.CreateSong("Cached") }.AsReadOnly();
            Guid userId = Guid.NewGuid();
            cache.Put(userId, "query-key", cachedItems);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // The OperationCanceledException catch is ordered BEFORE the cache-serve
            // catch, so cancellation must propagate even when a cached copy exists.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                search.CachedSearchAsync(
                    userId,
                    "query-key",
                    () => throw new OperationCanceledException(cts.Token),
                    "TestSearch",
                    cts.Token));
        }
        finally
        {
            RestoreTestCache(oldCache, oldCounters);
        }
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
        Assert.Equal(new[] { MediaType.Audio }, captured.MediaTypes);
        Assert.Equal(500, captured.Limit);
        Assert.True(captured.Recursive);
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
    // helpers
    // ---------------------------------------------------------------------

    private SearchService CreateSearchService(PluginConfiguration config)
        => new(config, _loggerFactory.CreateLogger<SearchServiceTests>(), requestTimeoutMs: 6000);

    private (SearchResultCache Cache, RequestCounters Counters, SearchResultCache OldCache, RequestCounters OldCounters) InstallTestCache()
    {
        TestHelpers.EnsurePluginInstance(new PluginConfiguration(), _loggerFactory, _ => { }, "searchservice-tests");
        SearchResultCache oldCache = Plugin.Instance!.SearchCache;
        RequestCounters oldCounters = Plugin.Instance.RequestCounters;
        var cache = new SearchResultCache(_loggerFactory.CreateLogger<SearchResultCache>(), maxEntriesPerUser: 10, expirationMinutes: 30);
        var counters = new RequestCounters();
        Plugin.Instance.SearchCache = cache;
        Plugin.Instance.RequestCounters = counters;
        return (cache, counters, oldCache, oldCounters);
    }

    private static void RestoreTestCache(SearchResultCache oldCache, RequestCounters oldCounters)
    {
        if (Plugin.Instance != null)
        {
            Plugin.Instance.SearchCache = oldCache;
            Plugin.Instance.RequestCounters = oldCounters;
        }
    }

    private record TestCandidate(string Name, Guid Id);

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
