using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Alexa.Cache;

public class ItemListCacheTests
{
    private readonly SearchResultCache _cache;
    private int _queryCallCount;

    public ItemListCacheTests()
    {
        var logger = new Mock<ILogger<SearchResultCache>>();
        _cache = new SearchResultCache(logger.Object, maxEntriesPerUser: 3, expirationMinutes: 1);
        _queryCallCount = 0;
    }

    private static List<BaseItem> MakeItems(params string[] names)
    {
        var items = new List<BaseItem>();
        foreach (string name in names)
        {
            items.Add(new Audio { Name = name, Id = Guid.NewGuid() });
        }

        return items;
    }

    private Task<IReadOnlyList<BaseItem>> QueryFunc()
    {
        _queryCallCount++;
        return Task.FromResult<IReadOnlyList<BaseItem>>(MakeItems($"Item{_queryCallCount}"));
    }

    [Fact]
    public async Task GetRecentlyAddedCachedAsync_FirstCall_ExecutesQuery()
    {
        Guid userId = Guid.NewGuid();

        IReadOnlyList<BaseItem> results = await _cache.GetRecentlyAddedCachedAsync(userId, QueryFunc);

        Assert.Equal(1, _queryCallCount);
        Assert.Single(results);
        Assert.Equal("Item1", results[0].Name);
    }

    [Fact]
    public async Task GetRecentlyAddedCachedAsync_SecondCall_ReturnsCached()
    {
        Guid userId = Guid.NewGuid();

        await _cache.GetRecentlyAddedCachedAsync(userId, QueryFunc);
        IReadOnlyList<BaseItem> results = await _cache.GetRecentlyAddedCachedAsync(userId, QueryFunc);

        Assert.Equal(1, _queryCallCount);
        Assert.Equal("Item1", results[0].Name);
    }

    [Fact]
    public async Task GetRecentlyAddedCachedAsync_DifferentUsers_CacheSeparately()
    {
        Guid user1 = Guid.NewGuid();
        Guid user2 = Guid.NewGuid();

        await _cache.GetRecentlyAddedCachedAsync(user1, QueryFunc);
        IReadOnlyList<BaseItem> user2Results = await _cache.GetRecentlyAddedCachedAsync(user2, QueryFunc);

        Assert.Equal(2, _queryCallCount);
        Assert.Equal("Item2", user2Results[0].Name);
    }

    [Fact]
    public async Task GetFavoritesCachedAsync_FirstCall_ExecutesQuery()
    {
        Guid userId = Guid.NewGuid();

        IReadOnlyList<BaseItem> results = await _cache.GetFavoritesCachedAsync(userId, QueryFunc);

        Assert.Equal(1, _queryCallCount);
        Assert.Single(results);
        Assert.Equal("Item1", results[0].Name);
    }

    [Fact]
    public async Task GetFavoritesCachedAsync_SecondCall_ReturnsCached()
    {
        Guid userId = Guid.NewGuid();

        await _cache.GetFavoritesCachedAsync(userId, QueryFunc);
        IReadOnlyList<BaseItem> results = await _cache.GetFavoritesCachedAsync(userId, QueryFunc);

        Assert.Equal(1, _queryCallCount);
        Assert.Equal("Item1", results[0].Name);
    }

    [Fact]
    public async Task GetFavoritesCachedAsync_DifferentUsers_CacheSeparately()
    {
        Guid user1 = Guid.NewGuid();
        Guid user2 = Guid.NewGuid();

        await _cache.GetFavoritesCachedAsync(user1, QueryFunc);
        IReadOnlyList<BaseItem> user2Results = await _cache.GetFavoritesCachedAsync(user2, QueryFunc);

        Assert.Equal(2, _queryCallCount);
        Assert.Equal("Item2", user2Results[0].Name);
    }

    [Fact]
    public async Task GetRecentlyAddedCachedAsync_EmptyResults_DoesNotCache()
    {
        Guid userId = Guid.NewGuid();
        int callCount = 0;

        Task<IReadOnlyList<BaseItem>> emptyQuery()
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<BaseItem>>(new List<BaseItem>());
        }

        await _cache.GetRecentlyAddedCachedAsync(userId, emptyQuery);
        await _cache.GetRecentlyAddedCachedAsync(userId, emptyQuery);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetFavoritesCachedAsync_EmptyResults_DoesNotCache()
    {
        Guid userId = Guid.NewGuid();
        int callCount = 0;

        Task<IReadOnlyList<BaseItem>> emptyQuery()
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<BaseItem>>(new List<BaseItem>());
        }

        await _cache.GetFavoritesCachedAsync(userId, emptyQuery);
        await _cache.GetFavoritesCachedAsync(userId, emptyQuery);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetRecentlyAddedCachedAsync_DoesNotAffectFavoritesCache()
    {
        Guid userId = Guid.NewGuid();

        await _cache.GetRecentlyAddedCachedAsync(userId, QueryFunc);
        await _cache.GetFavoritesCachedAsync(userId, QueryFunc);

        Assert.Equal(2, _queryCallCount);
    }

    [Fact]
    public async Task NoopCache_GetRecentlyAddedCachedAsync_AlwaysExecutesQuery()
    {
        SearchResultCache noop = SearchResultCache.Noop;
        int callCount = 0;

        Task<IReadOnlyList<BaseItem>> countingQuery()
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<BaseItem>>(MakeItems("A"));
        }

        Guid userId = Guid.NewGuid();
        await noop.GetRecentlyAddedCachedAsync(userId, countingQuery);
        await noop.GetRecentlyAddedCachedAsync(userId, countingQuery);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task NoopCache_GetFavoritesCachedAsync_AlwaysExecutesQuery()
    {
        SearchResultCache noop = SearchResultCache.Noop;
        int callCount = 0;

        Task<IReadOnlyList<BaseItem>> countingQuery()
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<BaseItem>>(MakeItems("A"));
        }

        Guid userId = Guid.NewGuid();
        await noop.GetFavoritesCachedAsync(userId, countingQuery);
        await noop.GetFavoritesCachedAsync(userId, countingQuery);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetRecentlyAddedCachedAsync_TtlExpiry_TriggersRequery()
    {
        var logger = new Mock<ILogger<SearchResultCache>>();
        var shortTtlCache = new TestableCache(logger.Object, recentlyAddedTtl: TimeSpan.FromMilliseconds(50));
        Guid userId = Guid.NewGuid();
        int callCount = 0;

        Task<IReadOnlyList<BaseItem>> countingQuery()
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<BaseItem>>(MakeItems($"Item{callCount}"));
        }

        await shortTtlCache.GetRecentlyAddedCachedAsync(userId, countingQuery);
        Assert.Equal(1, callCount);

        await Task.Delay(100);

        await shortTtlCache.GetRecentlyAddedCachedAsync(userId, countingQuery);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task GetFavoritesCachedAsync_TtlExpiry_TriggersRequery()
    {
        var logger = new Mock<ILogger<SearchResultCache>>();
        var shortTtlCache = new TestableCache(logger.Object, favoritesTtl: TimeSpan.FromMilliseconds(50));
        Guid userId = Guid.NewGuid();
        int callCount = 0;

        Task<IReadOnlyList<BaseItem>> countingQuery()
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<BaseItem>>(MakeItems($"Item{callCount}"));
        }

        await shortTtlCache.GetFavoritesCachedAsync(userId, countingQuery);
        Assert.Equal(1, callCount);

        await Task.Delay(100);

        await shortTtlCache.GetFavoritesCachedAsync(userId, countingQuery);
        Assert.Equal(2, callCount);
    }

    private class TestableCache : SearchResultCache
    {
        private readonly TimeSpan _recentlyAddedTtl;
        private readonly TimeSpan _favoritesTtl;

        public TestableCache(ILogger<SearchResultCache> logger, TimeSpan? recentlyAddedTtl = null, TimeSpan? favoritesTtl = null)
            : base(logger, maxEntriesPerUser: 10, expirationMinutes: 30)
        {
            _recentlyAddedTtl = recentlyAddedTtl ?? TimeSpan.FromMinutes(2);
            _favoritesTtl = favoritesTtl ?? TimeSpan.FromMinutes(5);
        }

        public override async Task<IReadOnlyList<BaseItem>> GetRecentlyAddedCachedAsync(
            Guid userId, Func<Task<IReadOnlyList<BaseItem>>> queryFunc)
        {
            string cacheKey = $"{userId:N}:recently_added";

            if (TryGetFromInternal(cacheKey, _recentlyAddedTtl, out IReadOnlyList<BaseItem>? cached))
            {
                return cached!;
            }

            IReadOnlyList<BaseItem> results = await queryFunc().ConfigureAwait(false);

            if (results.Count > 0)
            {
                PutInternal(cacheKey, results);
            }

            return results;
        }

        public override async Task<IReadOnlyList<BaseItem>> GetFavoritesCachedAsync(
            Guid userId, Func<Task<IReadOnlyList<BaseItem>>> queryFunc)
        {
            string cacheKey = $"{userId:N}:favorites";

            if (TryGetFromInternal(cacheKey, _favoritesTtl, out IReadOnlyList<BaseItem>? cached))
            {
                return cached!;
            }

            IReadOnlyList<BaseItem> results = await queryFunc().ConfigureAwait(false);

            if (results.Count > 0)
            {
                PutInternal(cacheKey, results);
            }

            return results;
        }

        private readonly Dictionary<string, (IReadOnlyList<BaseItem> Results, DateTimeOffset Timestamp)> _testCache = new();

        private void PutInternal(string key, IReadOnlyList<BaseItem> results)
        {
            _testCache[key] = (results, DateTimeOffset.UtcNow);
        }

        private bool TryGetFromInternal(string key, TimeSpan ttl, out IReadOnlyList<BaseItem>? results)
        {
            results = null;
            if (!_testCache.TryGetValue(key, out var cached))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - cached.Timestamp > ttl)
            {
                _testCache.Remove(key);
                return false;
            }

            results = cached.Results;
            return true;
        }
    }
}
