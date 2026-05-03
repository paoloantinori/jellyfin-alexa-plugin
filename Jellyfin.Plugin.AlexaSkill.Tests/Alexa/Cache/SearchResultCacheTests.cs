using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Alexa.Cache;

public class SearchResultCacheTests
{
    private readonly SearchResultCache _cache;

    public SearchResultCacheTests()
    {
        var logger = new Mock<ILogger<SearchResultCache>>();
        _cache = new SearchResultCache(logger.Object, maxEntriesPerUser: 3, expirationMinutes: 1);
    }

    private static List<BaseItem> MakeItems(params string[] names)
    {
        var items = new List<BaseItem>();
        foreach (string name in names)
        {
            var item = new Audio
            {
                Name = name,
                Id = Guid.NewGuid()
            };
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public void TryGet_EmptyCache_ReturnsFalse()
    {
        Assert.False(_cache.TryGet(Guid.NewGuid(), "test", out IReadOnlyList<BaseItem>? results));
        Assert.Null(results);
    }

    [Fact]
    public void Put_ThenTryGet_ReturnsResults()
    {
        Guid userId = Guid.NewGuid();
        var items = MakeItems("Song A");

        _cache.Put(userId, "key1", items);

        Assert.True(_cache.TryGet(userId, "key1", out IReadOnlyList<BaseItem>? results));
        Assert.Single(results!);
        Assert.Equal("Song A", results![0].Name);
    }

    [Fact]
    public void TryGet_DifferentUser_ReturnsFalse()
    {
        Guid user1 = Guid.NewGuid();
        Guid user2 = Guid.NewGuid();
        _cache.Put(user1, "key1", MakeItems("Song A"));

        Assert.False(_cache.TryGet(user2, "key1", out _));
    }

    [Fact]
    public void Put_OverwritesExisting()
    {
        Guid userId = Guid.NewGuid();
        _cache.Put(userId, "key1", MakeItems("Old"));
        _cache.Put(userId, "key1", MakeItems("New"));

        Assert.True(_cache.TryGet(userId, "key1", out IReadOnlyList<BaseItem>? results));
        Assert.Equal("New", results![0].Name);
    }

    [Fact]
    public void Put_EmptyResults_DoesNotCache()
    {
        Guid userId = Guid.NewGuid();
        _cache.Put(userId, "key1", new List<BaseItem>());

        Assert.False(_cache.TryGet(userId, "key1", out _));
    }

    [Fact]
    public void EvictOldEntries_EvictsWhenLimitExceeded()
    {
        Guid userId = Guid.NewGuid();
        _cache.Put(userId, "key1", MakeItems("A"));
        _cache.Put(userId, "key2", MakeItems("B"));
        _cache.Put(userId, "key3", MakeItems("C"));
        _cache.Put(userId, "key4", MakeItems("D"));

        // key1 should be evicted as the oldest entry
        Assert.False(_cache.TryGet(userId, "key1", out _));
        Assert.True(_cache.TryGet(userId, "key4", out _));
    }

    [Fact]
    public void NoopCache_PutDoesNothing()
    {
        SearchResultCache noop = SearchResultCache.Noop;
        noop.Put(Guid.NewGuid(), "key", MakeItems("A"));

        Assert.False(noop.TryGet(Guid.NewGuid(), "key", out _));
    }

    [Fact]
    public void NoopCache_TryGetAlwaysReturnsFalse()
    {
        SearchResultCache noop = SearchResultCache.Noop;
        Assert.False(noop.TryGet(Guid.NewGuid(), "any", out IReadOnlyList<BaseItem>? results));
        Assert.Null(results);
    }

    [Fact]
    public void TryGet_DifferentKey_ReturnsFalse()
    {
        Guid userId = Guid.NewGuid();
        _cache.Put(userId, "key1", MakeItems("A"));

        Assert.False(_cache.TryGet(userId, "key2", out _));
    }
}
