using System;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for configuration change propagation.
/// When the plugin configuration changes, dependent services must reset their internal state
/// so that stale data (cached connectivity results, search cache entries, circuit breaker state)
/// is cleared. These tests verify the individual service reset methods that the configuration
/// change handler invokes.
/// </summary>
public class ConfigurationPropagationTests
{
    private readonly ILogger _logger;

    public ConfigurationPropagationTests()
    {
        _logger = LoggerFactory.Create(b => { }).CreateLogger<ConfigurationPropagationTests>();
    }

    // -------------------------------------------------------------------------
    // JellyfinConnectivityChecker.InvalidateCache()
    // -------------------------------------------------------------------------

    [Fact]
    public void InvalidateCache_CanBeCalled_WithoutError()
    {
        var checker = new JellyfinConnectivityChecker(
            LoggerFactory.Create(b => { }).CreateLogger<JellyfinConnectivityChecker>());

        // Should not throw even though no check has been performed
        var exception = Record.Exception(() => checker.InvalidateCache());
        Assert.Null(exception);
    }

    [Fact]
    public void InvalidateCache_CalledMultipleTimes_DoesNotThrow()
    {
        var checker = new JellyfinConnectivityChecker(
            LoggerFactory.Create(b => { }).CreateLogger<JellyfinConnectivityChecker>());

        var exception = Record.Exception(() =>
        {
            checker.InvalidateCache();
            checker.InvalidateCache();
            checker.InvalidateCache();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void InvalidateCache_AfterInvalidate_CanBeCalledAgain()
    {
        var checker = new JellyfinConnectivityChecker(
            LoggerFactory.Create(b => { }).CreateLogger<JellyfinConnectivityChecker>());

        // First invalidation
        checker.InvalidateCache();

        // Second invalidation should also succeed
        var exception = Record.Exception(() => checker.InvalidateCache());
        Assert.Null(exception);
    }

    // -------------------------------------------------------------------------
    // SearchResultCache.Clear() and Count
    // -------------------------------------------------------------------------

    [Fact]
    public void SearchResultCache_Clear_OnEmptyCache_DoesNotThrow()
    {
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());

        var exception = Record.Exception(() => cache.Clear());
        Assert.Null(exception);
    }

    [Fact]
    public void SearchResultCache_Count_OnEmptyCache_ReturnsZero()
    {
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SearchResultCache_Clear_OnEmptyCache_CountRemainsZero()
    {
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());

        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SearchResultCache_Clear_AfterPut_CountReturnsZero()
    {
        var cache = new TestableSearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());

        // Use the testable subclass to add entries without requiring BaseItem instances
        cache.AddEntryDirectly(Guid.NewGuid(), "query1");
        cache.AddEntryDirectly(Guid.NewGuid(), "query2");
        Assert.Equal(2, cache.Count);

        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SearchResultCache_Clear_CalledMultipleTimes_CountStaysZero()
    {
        var cache = new TestableSearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());

        cache.AddEntryDirectly(Guid.NewGuid(), "query1");
        Assert.Equal(1, cache.Count);

        cache.Clear();
        cache.Clear();
        cache.Clear();

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SearchResultCache_Clear_ThenAdd_CountReflectsNewState()
    {
        var cache = new TestableSearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());

        cache.AddEntryDirectly(Guid.NewGuid(), "old_query");
        cache.Clear();
        Assert.Equal(0, cache.Count);

        cache.AddEntryDirectly(Guid.NewGuid(), "new_query");
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void SearchResultCache_TryGet_AfterClear_ReturnsFalse()
    {
        var cache = new TestableSearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());

        var userId = Guid.NewGuid();
        cache.AddEntryDirectly(userId, "test_query");
        Assert.True(cache.TryGet(userId, "test_query", out _));

        cache.Clear();

        Assert.False(cache.TryGet(userId, "test_query", out _));
    }

    // -------------------------------------------------------------------------
    // SearchResultCache.Noop (NoopSearchResultCache)
    // -------------------------------------------------------------------------

    [Fact]
    public void NoopCache_Clear_DoesNotThrow()
    {
        var noop = SearchResultCache.Noop;

        var exception = Record.Exception(() => noop.Clear());
        Assert.Null(exception);
    }

    [Fact]
    public void NoopCache_Count_ReturnsZero()
    {
        var noop = SearchResultCache.Noop;

        Assert.Equal(0, noop.Count);
    }

    [Fact]
    public void NoopCache_Clear_MultipleTimes_DoesNotThrow()
    {
        var noop = SearchResultCache.Noop;

        var exception = Record.Exception(() =>
        {
            noop.Clear();
            noop.Clear();
            noop.Clear();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void NoopCache_TryGet_ReturnsFalse()
    {
        var noop = SearchResultCache.Noop;
        var result = noop.TryGet(Guid.NewGuid(), "anything", out var items);

        Assert.False(result);
        Assert.Null(items);
    }

    // -------------------------------------------------------------------------
    // CircuitBreaker.Reset() (covered by existing CircuitBreakerTests, included
    // here for completeness of the configuration propagation test surface)
    // -------------------------------------------------------------------------

    [Fact]
    public void CircuitBreaker_Reset_ClearsOpenCircuit()
    {
        var cb = new CircuitBreaker(failureThreshold: 1);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);
        Assert.Equal(CircuitStatus.Open, cb.GetStatus(url));

        cb.Reset();

        Assert.Equal(CircuitStatus.Closed, cb.GetStatus(url));
    }

    [Fact]
    public void CircuitBreaker_Reset_OnFreshBreaker_DoesNotThrow()
    {
        var cb = new CircuitBreaker();

        var exception = Record.Exception(() => cb.Reset());
        Assert.Null(exception);
    }

    [Fact]
    public void CircuitBreaker_Reset_MultipleTimes_DoesNotThrow()
    {
        var cb = new CircuitBreaker(failureThreshold: 1);
        string url = "http://test:8096";

        cb.RecordFailure(url, _logger);

        var exception = Record.Exception(() =>
        {
            cb.Reset();
            cb.Reset();
            cb.Reset();
        });

        Assert.Null(exception);
    }

    // -------------------------------------------------------------------------
    // Combined propagation scenario: all services reset together
    // -------------------------------------------------------------------------

    [Fact]
    public void AllServices_ResetTogether_ConfigurationChangeScenario()
    {
        // Simulate what happens when configuration changes:
        // all three services should be resettable without error
        var checker = new JellyfinConnectivityChecker(
            LoggerFactory.Create(b => { }).CreateLogger<JellyfinConnectivityChecker>());
        var cache = new TestableSearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>());
        var cb = new CircuitBreaker(failureThreshold: 1);

        // Populate state
        cache.AddEntryDirectly(Guid.NewGuid(), "query1");
        cache.AddEntryDirectly(Guid.NewGuid(), "query2");
        cb.RecordFailure("http://test:8096", _logger);

        Assert.Equal(2, cache.Count);
        Assert.Equal(CircuitStatus.Open, cb.GetStatus("http://test:8096"));

        // Reset all as configuration change handler would
        var exception = Record.Exception(() =>
        {
            checker.InvalidateCache();
            cache.Clear();
            cb.Reset();
        });

        Assert.Null(exception);
        Assert.Equal(0, cache.Count);
        Assert.Equal(CircuitStatus.Closed, cb.GetStatus("http://test:8096"));
    }

    // -------------------------------------------------------------------------
    // Test helper: subclass that exposes AddEntryDirectly for testing without
    // requiring real BaseItem instances (which are hard to construct in tests).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Testable subclass that allows adding cache entries directly via reflection-free
    /// internal dictionary manipulation, bypassing the Put method's BaseItem requirement.
    /// </summary>
    private sealed class TestableSearchResultCache : SearchResultCache
    {
        public TestableSearchResultCache(ILogger<SearchResultCache> logger)
            : base(logger)
        {
        }

        /// <summary>
        /// Adds a placeholder entry to the internal cache dictionary for testing Clear/Count.
        /// Uses an empty list as the results payload, which is sufficient for testing
        /// cache management behavior (Clear, Count, TryGet after Clear).
        /// </summary>
        public void AddEntryDirectly(Guid userId, string queryKey)
        {
            // Call Put with an empty list to verify cache structure.
            // Put skips empty results, so we must use a non-empty list.
            // Use a single-element list with a minimal BaseItem-derived mock.
            // However, BaseItem cannot be easily instantiated. Instead, we
            // directly verify via TryGet/Count by using the base Put indirectly.
            //
            // Since Put rejects empty lists, we use the protected constructor's
            // ConcurrentDictionary directly. The simplest approach: invoke TryGet
            // is overridden in Noop but not here, so Count works against the real dictionary.
            //
            // For the real cache, we need a non-empty IReadOnlyList<BaseItem>.
            // We create a dummy BaseItem subclass that is only used for cache entries.
            var dummyItem = new DummyBaseItem();
            base.Put(userId, queryKey, new[] { dummyItem });
        }
    }

    /// <summary>
    /// Minimal BaseItem subclass for test purposes only.
    /// BaseItem is abstract and requires at minimum a name to be useful,
    /// but for cache testing we just need a non-null instance in the list.
    /// </summary>
    private sealed class DummyBaseItem : MediaBrowser.Controller.Entities.BaseItem
    {
        public override string GetClientTypeName() => "Dummy";
    }
}
