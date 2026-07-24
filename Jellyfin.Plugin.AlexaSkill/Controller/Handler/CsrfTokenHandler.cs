using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Controller.Handler;

/// <summary>
/// Class to handle CSRF tokens.
/// </summary>
public class CsrfTokenHandler
{
    private readonly ConcurrentDictionary<string, CsrfToken> csrfTokens = new();

    /// <summary>
    /// Remove all expired CSRF tokens.
    /// </summary>
    public void RemoveExpiredCsrfTokens()
    {
        List<string> expiredKeys = new List<string>();
        foreach (KeyValuePair<string, CsrfToken> csrfToken in csrfTokens)
        {
            if (DateTime.Compare(DateTime.UtcNow, csrfToken.Value.Expiration) >= 0)
            {
                expiredKeys.Add(csrfToken.Key);
            }
        }

        foreach (string key in expiredKeys)
        {
            csrfTokens.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Validate a CSRF token. The token is single-use: it is consumed (removed) on validation,
    /// whether valid or expired, so it cannot be replayed.
    /// </summary>
    /// <param name="token">The token to validate.</param>
    /// <returns>True if the token was valid (present and not expired), false otherwise.</returns>
    public bool ValidateCsrfToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        if (!csrfTokens.TryRemove(token, out CsrfToken? csrfToken))
        {
            return false;
        }

        // Single-use: TryRemove atomically consumes the token, so a concurrent validation
        // of the same token (e.g. a double-clicked submit) cannot also succeed.
        return DateTime.Compare(DateTime.UtcNow, csrfToken.Expiration) < 0;
    }

    /// <summary>
    /// Get a new CSRF token.
    /// </summary>
    /// <returns>The new CSRF token.</returns>
    public CsrfToken GetNewCsrfToken()
    {
        RemoveExpiredCsrfTokens();

        CsrfToken token;
        do
        {
            token = new CsrfToken();
        }
        while (!csrfTokens.TryAdd(token.Token, token));

        return token;
    }
}