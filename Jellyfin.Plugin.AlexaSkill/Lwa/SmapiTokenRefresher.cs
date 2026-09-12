using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Lwa;

/// <summary>
/// The one LWA device-token refresh MECHANISM (JF-544/JF-545): the only place that
/// calls LwaClient.RefreshDeviceToken and persists the rotation. Callers layer their
/// own policy on top: TokenRefreshTask's periodic sweep (skip while fresh), the
/// catalog sync's pre-sync gate and per-leg 401 retry, the restart-recovery
/// de-authorization policy in SkillStartup, and AlexaUtil.CallAsync's request-path
/// throw-on-failure. A long-running operation (a sync spans 30-45 min against ~1h
/// access tokens) must refresh up front and on 401 instead of trusting the sweep's
/// safety margin.
/// </summary>
internal static class SmapiTokenRefresher
{
    /// <summary>
    /// Refreshes the user's device token in place and persists the configuration.
    /// Returns false (with the reason logged at Debug/Warning) when LWA credentials
    /// or the refresh token are missing or the refresh call fails or throws; the
    /// caller decides whether that is fatal. Never throwing is part of the contract:
    /// long-running callers use this INSIDE their recovery path, where a thrown LWA
    /// failure would abort the very operation the refresh was meant to save (JF-544).
    /// </summary>
    public static async Task<bool> RefreshAsync(Entities.User user, ILogger logger)
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (config == null
            || string.IsNullOrWhiteSpace(config.LwaClientId)
            || string.IsNullOrWhiteSpace(config.LwaClientSecret))
        {
            // Warning, not Debug: restart recovery DE-AUTHS the user on this path
            // (SkillStartup nulls the refresh token), so the operator must see why
            // at the default log level (JF-545 review).
            logger.LogWarning("LWA credentials not configured; cannot refresh token for user {UserId}", user.Id);
            return false;
        }

        if (string.IsNullOrEmpty(user.SmapiRefreshToken))
        {
            logger.LogDebug("No refresh token for user {UserId}; cannot refresh", user.Id);
            return false;
        }

        DeviceToken tokenResult;
        try
        {
            tokenResult = await LwaClient.RefreshDeviceToken(
                new DeviceToken(user.SmapiRefreshToken, user.SmapiRefreshToken, "Bearer", 0),
                config.LwaClientId,
                config.LwaClientSecret).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token refresh request failed for user {UserId}", user.Id);
            return false;
        }

        // ParseTokenResponse never returns null (it throws on an unparseable body),
        // so the old null check was dead; the catch below owns every failure shape.
        user.SmapiDeviceToken = tokenResult;
        user.SmapiRefreshToken = tokenResult.RefreshToken;

        // Best-effort persist (the repo's own precedent: Plugin.cs wraps this same
        // call): the token IS fresh in memory, so a failed save neither aborts the
        // caller nor fails the refresh; it is loud because the on-disk refresh token
        // can lag the rotation (re-link risk if the process dies before a later save).
        try
        {
            plugin!.SaveConfiguration();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token rotated for user {UserId} but persisting the config failed; continuing with the in-memory token", user.Id);
        }

        logger.LogDebug("Refreshed SMAPI token for user {UserId}", user.Id);
        return true;
    }

    /// <summary>
    /// JF-547 gate idiom, single definition: long-running SMAPI operations refresh
    /// up front unless comfortably more than their whole duration remains (unknown
    /// expiry also refreshes). A failed refresh is non-fatal and logged; callers
    /// keep their own per-attempt defenses (401 retry, sweep rotation).
    /// </summary>
    /// <param name="budgetMinutes">The operation's expected duration; the token must
    /// outlive it plus the sweep margin.</param>
    /// <param name="opName">For the log line, e.g. "catalog sync" or "custom-model deploy".</param>
    public static async Task EnsureLifetimeBudgetAsync(
        Entities.User user, int budgetMinutes, string opName, ILogger logger)
    {
        TimeSpan remaining = RemainingLifetime(user);
        if (remaining >= TimeSpan.FromMinutes(budgetMinutes))
        {
            return;
        }

        logger.LogInformation(
            "Refreshing SMAPI token before {Op}: {Minutes:F0} min remaining < {Budget} min budget (JF-544)",
            opName, remaining.TotalMinutes, budgetMinutes);
        if (!await RefreshAsync(user, logger).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Pre-{Op} token refresh failed for user {UserId}; proceeding with the current token",
                opName, user.Id);
        }
    }

    /// <summary>Remaining access-token lifetime; an unknown expiry reads as zero
    /// so callers err toward refreshing.</summary>
    public static TimeSpan RemainingLifetime(Entities.User user)
    {
        if (user.SmapiDeviceToken == null || user.SmapiDeviceToken.ExpireTimestamp <= 0)
        {
            return TimeSpan.Zero;
        }

        return DateTimeOffset.FromUnixTimeSeconds(user.SmapiDeviceToken.ExpireTimestamp) - DateTimeOffset.UtcNow;
    }
}
