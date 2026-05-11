using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Cache;

/// <summary>
/// Thread-safe in-memory cache for library search results.
/// Stores recent successful query results per user for fallback when the API is unavailable.
/// </summary>
public class SearchResultCache
{
    private readonly ConcurrentDictionary<string, CachedResult> _cache = new();
    private readonly int _maxEntriesPerUser;
    private readonly TimeSpan _expiration;
    private readonly ILogger? _logger;

    /// <summary>
    /// Gets a no-op cache instance that never stores or returns results.
    /// Used when the real cache is not available from DI.
    /// </summary>
    public static SearchResultCache Noop { get; } = new NoopSearchResultCache();

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchResultCache"/> class.
    /// </summary>
    protected SearchResultCache()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchResultCache"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxEntriesPerUser">Maximum cached queries per user.</param>
    /// <param name="expirationMinutes">How long cached results remain valid.</param>
    public SearchResultCache(ILogger<SearchResultCache> logger, int maxEntriesPerUser = 50, int expirationMinutes = 30)
        : this()
    {
        _logger = logger;
        _maxEntriesPerUser = maxEntriesPerUser;
        _expiration = TimeSpan.FromMinutes(expirationMinutes);
    }

    /// <summary>
    /// Gets the current number of entries in the cache (for diagnostics).
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Store search results for a user query.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="queryKey">A normalized key representing the search query.</param>
    /// <param name="results">The search results to cache.</param>
    public virtual void Put(Guid userId, string queryKey, IReadOnlyList<BaseItem> results)
    {
        if (results.Count == 0)
        {
            return;
        }

        string cacheKey = BuildKey(userId, queryKey);
        var entry = new CachedResult(results, DateTimeOffset.UtcNow);

        _cache[cacheKey] = entry;

        EvictOldEntries(userId);
    }

    /// <summary>
    /// Try to retrieve cached search results for a user query.
    /// </summary>
    /// <param name="userId">The Jellyfin user ID.</param>
    /// <param name="queryKey">A normalized key representing the search query.</param>
    /// <param name="results">The cached results if found and not expired.</param>
    /// <returns>True if valid cached results were found.</returns>
    public virtual bool TryGet(Guid userId, string queryKey, out IReadOnlyList<BaseItem>? results)
    {
        results = null;
        string cacheKey = BuildKey(userId, queryKey);

        if (!_cache.TryGetValue(cacheKey, out CachedResult? cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.Timestamp > _expiration)
        {
            _cache.TryRemove(cacheKey, out _);
            return false;
        }

        results = cached.Results;
        _logger?.LogDebug("Cache hit for user {UserId} query '{Query}'", userId, queryKey);
        return true;
    }

    /// <summary>
    /// Remove all entries from the cache.
    /// </summary>
    public virtual void Clear()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Remove all expired entries from the cache.
    /// </summary>
    /// <returns>The number of entries removed.</returns>
    public virtual int RemoveExpired()
    {
        var expiredKeys = new List<string>();
        foreach (KeyValuePair<string, CachedResult> kvp in _cache)
        {
            if (DateTimeOffset.UtcNow - kvp.Value.Timestamp > _expiration)
            {
                expiredKeys.Add(kvp.Key);
            }
        }

        int removed = 0;
        foreach (string key in expiredKeys)
        {
            if (_cache.TryRemove(key, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger?.LogDebug("Removed {Count} expired cache entries", removed);
        }

        return removed;
    }

    /// <summary>
    /// Build a normalized cache key from user ID and query parameters.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="queryKey">Normalized query key (search term + filters).</param>
    /// <returns>Cache key string.</returns>
    private static string BuildKey(Guid userId, string queryKey)
    {
        return $"{userId:N}:{queryKey}";
    }

    /// <summary>
    /// Remove oldest entries for a user when the per-user limit is exceeded.
    /// </summary>
    private void EvictOldEntries(Guid userId)
    {
        string prefix = $"{userId:N}:";
        int count = 0;
        string? oldestKey = null;
        DateTimeOffset oldestTime = DateTimeOffset.MaxValue;

        foreach (KeyValuePair<string, CachedResult> kvp in _cache)
        {
            if (kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
                if (kvp.Value.Timestamp < oldestTime)
                {
                    oldestTime = kvp.Value.Timestamp;
                    oldestKey = kvp.Key;
                }
            }
        }

        if (count > _maxEntriesPerUser && oldestKey != null)
        {
            _cache.TryRemove(oldestKey, out _);
        }
    }

    /// <summary>
    /// Cached result entry with timestamp for expiration.
    /// </summary>
    private sealed class CachedResult
    {
        public CachedResult(IReadOnlyList<BaseItem> results, DateTimeOffset timestamp)
        {
            Results = results;
            Timestamp = timestamp;
        }

        public IReadOnlyList<BaseItem> Results { get; }

        public DateTimeOffset Timestamp { get; }
    }

    /// <summary>
    /// No-op cache that never stores or returns results. Used when DI is unavailable.
    /// </summary>
    private sealed class NoopSearchResultCache : SearchResultCache
    {
        public override void Put(Guid userId, string queryKey, IReadOnlyList<BaseItem> results)
        {
        }

        public override bool TryGet(Guid userId, string queryKey, out IReadOnlyList<BaseItem>? results)
        {
            results = null;
            return false;
        }

        public override void Clear()
        {
        }

        public override int RemoveExpired() => 0;
    }
}
