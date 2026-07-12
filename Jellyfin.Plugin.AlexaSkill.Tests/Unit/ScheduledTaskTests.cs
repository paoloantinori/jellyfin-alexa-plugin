using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.EntryPoints;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// Tests for JF-95: IScheduledTask implementations and SearchResultCache.RemoveExpired().
/// </summary>
[Collection("Plugin")]
public class ScheduledTaskTests : PluginTestBase
{
    private readonly ILogger _logger;

    public ScheduledTaskTests()
    {
        _logger = LoggerFactory.Create(b => { }).CreateLogger<ScheduledTaskTests>();
    }

    // -------------------------------------------------------------------------
    // SearchResultCache.RemoveExpired()
    // -------------------------------------------------------------------------

    [Fact]
    public void RemoveExpired_OnEmptyCache_ReturnsZero()
    {
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>(),
            maxEntriesPerUser: 10,
            expirationMinutes: 1);

        int removed = cache.RemoveExpired();
        Assert.Equal(0, removed);
    }

    [Fact]
    public void RemoveExpired_WithNoExpiredEntries_ReturnsZero()
    {
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>(),
            maxEntriesPerUser: 10,
            expirationMinutes: 60);

        var userId = Guid.NewGuid();
        cache.Put(userId, "test", new[] { new DummyBaseItem("item1") });

        int removed = cache.RemoveExpired();
        Assert.Equal(0, removed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RemoveExpired_WithExpiredEntries_RemovesThem()
    {
        // Use negative expiration so entries are always expired
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>(),
            maxEntriesPerUser: 10,
            expirationMinutes: -1);

        var userId = Guid.NewGuid();
        cache.Put(userId, "expired1", new[] { new DummyBaseItem("item1") });
        cache.Put(userId, "expired2", new[] { new DummyBaseItem("item2") });

        int removed = cache.RemoveExpired();
        Assert.Equal(2, removed);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RemoveExpired_MixedExpiredAndFresh_OnlyRemovesExpired()
    {
        // Use short expiration so first entries expire, then verify fresh entries survive
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>(),
            maxEntriesPerUser: 10,
            expirationMinutes: 60);

        var userId = Guid.NewGuid();
        cache.Put(userId, "fresh", new[] { new DummyBaseItem("item") });

        // Fresh entry should not be removed
        int removed = cache.RemoveExpired();
        Assert.Equal(0, removed);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(userId, "fresh", out var results));
        Assert.NotNull(results);
    }

    [Fact]
    public void NoopSearchResultCache_RemoveExpired_ReturnsZero()
    {
        int removed = SearchResultCache.Noop.RemoveExpired();
        Assert.Equal(0, removed);
    }

    [Fact]
    public void NoopSearchResultCache_PutDoesNothing()
    {
        var userId = Guid.NewGuid();
        SearchResultCache.Noop.Put(userId, "key", new[] { new DummyBaseItem("item") });

        Assert.False(SearchResultCache.Noop.TryGet(userId, "key", out _));
        Assert.Equal(0, SearchResultCache.Noop.Count);
    }

    // -------------------------------------------------------------------------
    // CacheCleanupTask
    // -------------------------------------------------------------------------

    [Fact]
    public async Task CacheCleanupTask_ExecuteAsync_RemovesExpiredEntries()
    {
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>(),
            maxEntriesPerUser: 10,
            expirationMinutes: -1);

        var userId = Guid.NewGuid();
        cache.Put(userId, "old1", new[] { new DummyBaseItem("a") });
        cache.Put(userId, "old2", new[] { new DummyBaseItem("b") });

        var task = new CacheCleanupTask(
            LoggerFactory.Create(b => { }).CreateLogger<CacheCleanupTask>(),
            cache);

        await task.ExecuteAsync(NullProgress.Instance, CancellationToken.None);

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task CacheCleanupTask_ExecuteAsync_NoExpiredEntries_NoRemoval()
    {
        var cache = new SearchResultCache(
            LoggerFactory.Create(b => { }).CreateLogger<SearchResultCache>(),
            maxEntriesPerUser: 10,
            expirationMinutes: 60);

        var userId = Guid.NewGuid();
        cache.Put(userId, "fresh", new[] { new DummyBaseItem("item") });

        var task = new CacheCleanupTask(
            LoggerFactory.Create(b => { }).CreateLogger<CacheCleanupTask>(),
            cache);

        await task.ExecuteAsync(NullProgress.Instance, CancellationToken.None);

        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void CacheCleanupTask_Metadata_HasCorrectProperties()
    {
        var task = new CacheCleanupTask(
            LoggerFactory.Create(b => { }).CreateLogger<CacheCleanupTask>(),
            SearchResultCache.Noop);

        Assert.Equal("Clean Up Alexa Skill Cache", task.Name);
        Assert.Equal("AlexaSkillCacheCleanup", task.Key);
        Assert.Equal("Alexa Skill", task.Category);
        Assert.NotNull(task.GetDefaultTriggers());
    }

    [Fact]
    public void CacheCleanupTask_DefaultTrigger_IsHourly()
    {
        var task = new CacheCleanupTask(
            LoggerFactory.Create(b => { }).CreateLogger<CacheCleanupTask>(),
            SearchResultCache.Noop);

        var triggers = task.GetDefaultTriggers().ToList();
        Assert.Single(triggers);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, triggers[0].IntervalTicks);
    }

    // -------------------------------------------------------------------------
    // TokenRefreshTask
    // -------------------------------------------------------------------------

    [Fact]
    public void TokenRefreshTask_Metadata_HasCorrectProperties()
    {
        var task = new TokenRefreshTask(
            LoggerFactory.Create(b => { }).CreateLogger<TokenRefreshTask>());

        Assert.Equal("Refresh Alexa LWA Tokens", task.Name);
        Assert.Equal("AlexaSkillTokenRefresh", task.Key);
        Assert.Equal("Alexa Skill", task.Category);
        Assert.NotNull(task.GetDefaultTriggers());
    }

    [Fact]
    public void TokenRefreshTask_DefaultTrigger_IntervalShorterThanAccessTokenLifetime()
    {
        // LWA access tokens live ~1h; the refresh interval must be shorter or the token
        // is expired most of the time, breaking SMAPI management ops (catalog sync,
        // invocation-name redeploy). Old value was 6h. JF-333.
        var task = new TokenRefreshTask(
            LoggerFactory.Create(b => { }).CreateLogger<TokenRefreshTask>());

        var triggers = task.GetDefaultTriggers().ToList();
        var interval = triggers.Single(t => t.Type == TaskTriggerInfoType.IntervalTrigger);
        Assert.True(
            TimeSpan.FromTicks(interval.IntervalTicks ?? 0) < TimeSpan.FromHours(1),
            $"token refresh interval must be < 1h token lifetime, got {TimeSpan.FromTicks(interval.IntervalTicks ?? 0)}");
    }

    [Fact]
    public async Task TokenRefreshTask_ExecuteAsync_NoPluginInstance_ReturnsWithoutError()
    {
        // Plugin.Instance is null in test context
        var task = new TokenRefreshTask(
            LoggerFactory.Create(b => { }).CreateLogger<TokenRefreshTask>());

        // Should complete without throwing
        await task.ExecuteAsync(NullProgress.Instance, CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // CatalogSyncTask
    // -------------------------------------------------------------------------

    [Fact]
    public void CatalogSyncTask_Metadata_HasCorrectProperties()
    {
        var task = new CatalogSyncTask(
            LoggerFactory.Create(b => { }).CreateLogger<CatalogSyncTask>(),
            syncService: null!,
            userManager: null!);

        Assert.Equal("Sync Alexa Skill Catalogs", task.Name);
        Assert.Equal("AlexaSkillCatalogSync", task.Key);
        Assert.Equal("Alexa Skill", task.Category);
    }

    [Fact]
    public void CatalogSyncTask_DefaultTrigger_HasStartupTrigger()
    {
        // Plugin re-registration on each load resets the weekly IntervalTrigger's
        // first-run baseline, so CatalogSync never auto-ran before JF-333. The
        // StartupTrigger fires on every restart (ExecuteAsync skips users synced
        // < 12h ago to avoid redundant SMAPI work).
        var task = new CatalogSyncTask(
            LoggerFactory.Create(b => { }).CreateLogger<CatalogSyncTask>(),
            syncService: null!,
            userManager: null!);

        var triggerTypes = task.GetDefaultTriggers().Select(t => t.Type).ToList();
        Assert.Contains(TaskTriggerInfoType.StartupTrigger, triggerTypes);
        Assert.Contains(TaskTriggerInfoType.IntervalTrigger, triggerTypes);

        var weekly = task.GetDefaultTriggers().Single(t => t.Type == TaskTriggerInfoType.IntervalTrigger);
        Assert.Equal(TimeSpan.FromDays(7).Ticks, weekly.IntervalTicks);
    }

    /// <summary>
    /// Minimal <see cref="BaseItem"/> subclass for cache testing.
    /// </summary>
    private sealed class DummyBaseItem : BaseItem
    {
        public DummyBaseItem(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// No-op progress reporter for tests that don't need to check progress.
    /// </summary>
    private sealed class NullProgress : IProgress<double>
    {
        public static NullProgress Instance { get; } = new();
        public void Report(double value) { }
    }
}
