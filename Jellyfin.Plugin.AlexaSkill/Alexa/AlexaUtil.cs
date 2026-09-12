using System;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Util methods.
/// </summary>
public static class AlexaUtil
{
    /// <summary>
    /// Runs an async function and retries with a new access token if the first call fails. Updates the new token in the database.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="user">The user to run the function for.</param>
    /// <param name="func">The async function to run.</param>
    /// <returns>The result of the function.</returns>
    public static async Task<T> CallAsync<T>(User user, Func<Task<T>> func)
    {
        try
        {
            return await func().ConfigureAwait(false);
        }
        catch (Refit.ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return await RefreshAndRetry(user, func, ex).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex.InnerException is Refit.ApiException apiEx && apiEx.StatusCode == HttpStatusCode.Unauthorized)
        {
            return await RefreshAndRetry(user, func, apiEx).ConfigureAwait(false);
        }
    }

    private static async Task<T> RefreshAndRetry<T>(User user, Func<Task<T>> func, Refit.ApiException originalEx)
    {
        if (user.SmapiDeviceToken == null)
        {
            throw originalEx;
        }

        // JF-545: the one shared refresh mechanism (SmapiTokenRefresher); this wrapper
        // keeps the request-path throw-on-failure contract: a failed refresh must fail
        // the request, not swallow into a stale retry. A real logger, not Null: the
        // refresher's warning carries LWA's status and body (the invalid_grant evidence
        // a re-link investigation needs).
        if (!await SmapiTokenRefresher.RefreshAsync(user, LwaClient.RefreshLogger).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("Failed to refresh token");
        }

        return await func().ConfigureAwait(false);
    }
}