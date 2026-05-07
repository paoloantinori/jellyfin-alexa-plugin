using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Scheduled task that proactively refreshes LWA tokens before they expire,
/// avoiding 401 errors during live Alexa requests.
/// </summary>
public class TokenRefreshTask : IScheduledTask
{
    private readonly ILogger<TokenRefreshTask> _logger;

    public TokenRefreshTask(ILogger<TokenRefreshTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh Alexa LWA Tokens";

    /// <inheritdoc />
    public string Key => "AlexaSkillTokenRefresh";

    /// <inheritdoc />
    public string Description => "Refreshes Login with Amazon OAuth tokens for all configured users before they expire.";

    /// <inheritdoc />
    public string Category => "Alexa Skill";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance == null)
        {
            return;
        }

        var config = Plugin.Instance.Configuration;
        if (string.IsNullOrWhiteSpace(config.LwaClientId) || string.IsNullOrWhiteSpace(config.LwaClientSecret))
        {
            _logger.LogDebug("LWA credentials not configured — skipping token refresh");
            return;
        }

        var users = config.Users;
        if (users.Count == 0)
        {
            return;
        }

        int refreshed = 0;
        int failed = 0;

        for (int i = 0; i < users.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report((double)i / users.Count);

            User user = users[i];

            if (string.IsNullOrEmpty(user.SmapiRefreshToken))
            {
                continue;
            }

            try
            {
                DeviceToken? tokenResult = await LwaClient.RefreshDeviceToken(
                    new DeviceToken(user.SmapiRefreshToken, user.SmapiRefreshToken, "Bearer", 0),
                    config.LwaClientId,
                    config.LwaClientSecret).ConfigureAwait(false);

                if (tokenResult != null)
                {
                    user.SmapiDeviceToken = tokenResult;
                    user.SmapiRefreshToken = tokenResult.RefreshToken;
                    refreshed++;
                    _logger.LogDebug("Refreshed token for user {UserId}", user.Id);
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Failed to refresh token for user {UserId}", user.Id);
            }
        }

        if (refreshed > 0 || failed > 0)
        {
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("Token refresh complete: {Refreshed} refreshed, {Failed} failed", refreshed, failed);
        }

        progress.Report(1.0);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
