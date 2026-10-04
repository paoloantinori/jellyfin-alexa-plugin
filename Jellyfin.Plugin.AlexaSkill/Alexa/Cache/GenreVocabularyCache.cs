using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Cache;

/// <summary>
/// JF-645 item 3: TTL cache for the JF-643 kana genre-resolution tier's library
/// vocabulary. The tier deduplicates the library's genre tags and computes one
/// Double Metaphone code set per tag name on every scan (~500 encodes per kana
/// genre request against the <see cref="Util.SearchService"/> vocabulary bound);
/// genre vocabulary is near-static, so the built vocabulary is cached per scope
/// the way <see cref="SearchResultCache"/> caches query results (30-minute TTL,
/// staleness bounded by the TTL, no invalidation events). DI SINGLETON, injected
/// into the genre handlers as an optional ctor parameter (the DeviceQueueManager
/// pattern; tests construct handlers without it, which disables caching and
/// keeps every test's mock vocabulary isolated - a process-wide static cache
/// would leak rows between tests sharing the fixture user).
/// GROWTH BOUND: entries are keyed per (Jellyfin user, resolved library scope),
/// each holding at most ~500 item references plus a codes dictionary (tens of
/// KB); expired entries are removed on read, and the user x scope key space at
/// household scale is single digits, so no periodic sweep or size cap is carried
/// (the SearchResultCache sweep solves its unbounded query-key space, not this one).
/// REVISIT TRIGGER (the JF-741 rule): this is the plugin's fourth small
/// lazy-TTL ConcurrentDictionary cache (SearchResultCache, SessionReferenceCache,
/// NextTrackPrecomputeCache, this); a FIFTH consumer is the trigger to hoist a
/// shared generic TTL-cache core rather than copy this shape again.
/// </summary>
public sealed class GenreVocabularyCache
{
    /// <summary>The SearchResultCache TTL: 30 minutes.</summary>
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(Guid UserId, string Scope), CachedEntry> _byScope = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreVocabularyCache"/> class
    /// with the default TTL and wall clock (the constructor the DI container resolves).
    /// </summary>
    public GenreVocabularyCache() : this(DefaultTtl)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GenreVocabularyCache"/> class
    /// with an explicit TTL and clock (the NextTrackPrecomputeCache TimeProvider
    /// seam: tests substitute <c>TestHelpers.FakeTimeProvider</c> and advance past
    /// the TTL deterministically instead of sleeping).
    /// </summary>
    /// <param name="ttl">How long a cached vocabulary stays valid.</param>
    /// <param name="time">The clock; defaults to <see cref="TimeProvider.System"/> in production.</param>
    internal GenreVocabularyCache(TimeSpan ttl, TimeProvider? time = null)
    {
        _ttl = ttl;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the number of cached scopes (diagnostics and test assertions).
    /// </summary>
    public int Count => _byScope.Count;

    /// <summary>
    /// Tries to retrieve the cached vocabulary for one query scope. Expired
    /// entries are removed and reported as misses (the SearchResultCache shape).
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user the vocabulary query was scoped to.</param>
    /// <param name="libraryScope">The plugin user's resolved library scope (top-parent ids), or null when unrestricted.</param>
    /// <param name="vocabulary">The cached vocabulary when found and fresh.</param>
    /// <returns>True when a fresh cached vocabulary was found.</returns>
    public bool TryGet(Guid jellyfinUserId, Guid[]? libraryScope, out GenreVocabulary? vocabulary)
    {
        vocabulary = null;
        (Guid UserId, string Scope) key = BuildKey(jellyfinUserId, libraryScope);
        if (!_byScope.TryGetValue(key, out CachedEntry? cached))
        {
            return false;
        }

        if (_time.GetUtcNow() - cached.CachedAt > _ttl)
        {
            _byScope.TryRemove(key, out _);
            return false;
        }

        vocabulary = cached.Vocabulary;
        return true;
    }

    /// <summary>
    /// Stores a vocabulary for one query scope, overwriting any previous entry.
    /// Callers only cache NON-EMPTY vocabularies (the tier's empty outcome is its
    /// no-match null, re-derived per request like the pre-cache behavior).
    /// </summary>
    /// <param name="jellyfinUserId">The Jellyfin user the vocabulary query was scoped to.</param>
    /// <param name="libraryScope">The plugin user's resolved library scope (top-parent ids), or null when unrestricted.</param>
    /// <param name="vocabulary">The built vocabulary to cache.</param>
    public void Put(Guid jellyfinUserId, Guid[]? libraryScope, GenreVocabulary vocabulary)
    {
        _byScope[BuildKey(jellyfinUserId, libraryScope)] = new CachedEntry(vocabulary, _time.GetUtcNow());
    }

    /// <summary>Removes every cached vocabulary (diagnostics/tests).</summary>
    public void Clear() => _byScope.Clear();

    private static (Guid UserId, string Scope) BuildKey(Guid jellyfinUserId, Guid[]? libraryScope)
        => (jellyfinUserId, libraryScope is { Length: > 0 } ? string.Join(",", libraryScope.OrderBy(id => id).Select(id => id.ToString("N"))) : "*");

    private sealed record CachedEntry(GenreVocabulary Vocabulary, DateTimeOffset CachedAt);
}
