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

    // How often to CHECK whether a refresh is due. Must be short enough that a refresh
    // attempt lands inside the safety window: TokenCheckInterval + TokenSafetyMargin must
    // stay below the ~1h LWA access-token lifetime (Amazon controls expires_in).
    private const int TokenCheckIntervalMinutes = 20;

    // Refresh when the token has less than this remaining. Buffer so a delayed check
    // still refreshes before expiry.
    private const int TokenSafetyMarginMinutes = 30;

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

            // Refresh only when the access token is missing or near expiry. LWA access
            // tokens live ~1h; the old blind refresh on a 6h interval left the token
            // expired ~83% of the time, breaking SMAPI management ops (catalog sync,
            // invocation-name redeploy). JF-333.
            if (user.SmapiDeviceToken != null && user.SmapiDeviceToken.ExpireTimestamp > 0)
            {
                DateTimeOffset expiry = DateTimeOffset.FromUnixTimeSeconds(user.SmapiDeviceToken.ExpireTimestamp);
                if (expiry - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(TokenSafetyMarginMinutes))
                {
                    continue; // still fresh
                }
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
        // Interval must be shorter than the ~1h access-token lifetime or the token is
        // expired most of the time. ExecuteAsync additionally skips users whose token
        // still has > 30 min remaining, so we check often but only refresh when needed.
        // JF-333 (was 6h).
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(TokenCheckIntervalMinutes).Ticks
        };
    }
}
