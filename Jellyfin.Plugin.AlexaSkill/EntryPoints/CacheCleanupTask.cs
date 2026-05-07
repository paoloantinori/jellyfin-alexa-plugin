using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Scheduled task that periodically removes expired entries from the search result cache.
/// </summary>
public class CacheCleanupTask : IScheduledTask
{
    private readonly ILogger<CacheCleanupTask> _logger;
    private readonly SearchResultCache _searchCache;

    public CacheCleanupTask(ILogger<CacheCleanupTask> logger, SearchResultCache searchCache)
    {
        _logger = logger;
        _searchCache = searchCache;
    }

    /// <inheritdoc />
    public string Name => "Clean Up Alexa Skill Cache";

    /// <inheritdoc />
    public string Key => "AlexaSkillCacheCleanup";

    /// <inheritdoc />
    public string Description => "Removes expired entries from the search result cache.";

    /// <inheritdoc />
    public string Category => "Alexa Skill";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        int removed = _searchCache.RemoveExpired();

        if (removed > 0)
        {
            _logger.LogInformation("Cache cleanup removed {Count} expired entries ({Remaining} remaining)", removed, _searchCache.Count);
        }

        progress.Report(1.0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(1).Ticks
        };
    }
}
