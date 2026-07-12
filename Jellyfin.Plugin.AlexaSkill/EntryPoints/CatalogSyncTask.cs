using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Scheduled task that syncs Jellyfin library items to SMAPI catalogs
/// for improved Alexa recognition of artist and album names.
/// </summary>
public class CatalogSyncTask : IScheduledTask
{
    private readonly ILogger<CatalogSyncTask> _logger;
    private readonly LibrarySyncService _syncService;
    private readonly IUserManager _userManager;

    // StartupTrigger fires on every plugin load; skip users synced within this window
    // so frequent restarts don't trigger redundant SMAPI catalog work.
    private const int RecentSyncThresholdHours = 12;

    public CatalogSyncTask(
        ILogger<CatalogSyncTask> logger,
        LibrarySyncService syncService,
        IUserManager userManager)
    {
        _logger = logger;
        _syncService = syncService;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public string Name => "Sync Alexa Skill Catalogs";

    /// <inheritdoc />
    public string Key => "AlexaSkillCatalogSync";

    /// <inheritdoc />
    public string Description => "Syncs Jellyfin library artists and albums to Alexa custom slot type catalogs for improved voice recognition.";

    /// <inheritdoc />
    public string Category => "Alexa Skill";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        PluginConfiguration config = Plugin.Instance!.Configuration;

        if (config.Users.Count == 0)
        {
            _logger.LogInformation("No users configured, skipping catalog sync");
            progress.Report(1.0);
            return;
        }

        int totalUsers = config.Users.Count;
        int processed = 0;

        foreach (Entities.User user in config.Users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (user.SmapiDeviceToken == null || user.UserSkill?.SkillId == null)
            {
                _logger.LogDebug("Skipping user {UserId}: no SMAPI credentials or skill", user.Id);
                processed++;
                progress.Report((double)processed / totalUsers);
                continue;
            }

            // StartupTrigger fires on every plugin load (each Jellyfin restart). Skip
            // users synced recently to avoid redundant SMAPI work; the weekly
            // IntervalTrigger plus this gate keep catalogs fresh without syncing on
            // every restart. JF-333.
            TimeSpan sinceSync = user.LastCatalogSync.HasValue
                ? DateTime.UtcNow - user.LastCatalogSync.Value
                : TimeSpan.MaxValue;
            if (sinceSync < TimeSpan.FromHours(RecentSyncThresholdHours))
            {
                _logger.LogDebug(
                    "Skipping user {UserId}: catalog synced {Hours:F1}h ago (< 12h)",
                    user.Id,
                    sinceSync.TotalHours);
                processed++;
                progress.Report((double)processed / totalUsers);
                continue;
            }

            try
            {
                Jellyfin.Database.Implementations.Entities.User? jellyfinUser = _userManager.GetUserById(user.Id);
                if (jellyfinUser == null)
                {
                    _logger.LogWarning("Jellyfin user {UserId} not found, skipping", user.Id);
                    processed++;
                    progress.Report((double)processed / totalUsers);
                    continue;
                }

                SyncResult result = await _syncService.SyncUserLibraryAsync(
                    user, jellyfinUser, cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    user.LastCatalogSync = result.SyncTime;
                    _logger.LogInformation(
                        "Catalog sync succeeded for user {UserId}: {Artists} artists, {Albums} albums",
                        user.Id,
                        result.ArtistCount,
                        result.AlbumCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catalog sync failed for user {UserId}", user.Id);
            }

            processed++;
            progress.Report((double)processed / totalUsers);
        }

        Plugin.Instance.SaveConfiguration();
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // StartupTrigger: Jellyfin re-registers a plugin's IScheduledTasks on each
        // plugin load (DLL update), which resets the weekly IntervalTrigger's first-run
        // baseline — so on servers that update the plugin more often than weekly, the
        // interval never elapses and the catalog (AlbumName/JellyfinArtist) never gets
        // populated (this is why it never auto-ran before JF-333). The startup trigger
        // fires on every restart; ExecuteAsync skips users synced < 12h ago.
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(7).Ticks
        };
    }
}
