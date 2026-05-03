using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Lwa;

/// <summary>
/// Client interacting with LWA to authorize the user for SMAPI access.
/// </summary>
public static class LwaClient
{
    private static ILogger Logger => Plugin.Instance?.LoggerFactory.CreateLogger(nameof(LwaClient))
        ?? LoggerFactory.Create(b => { }).CreateLogger(nameof(LwaClient));

    /// <summary>
    /// Create a device authorization request to LWA.
    /// </summary>
    /// <param name="clientId">LWA client id.</param>
    /// <param name="scopes">List scopes of the request.</param>
    /// <returns>Access token.</returns>
    public static async Task<DeviceAuthorizationRequest?> CreateLwaDeviceAuthorizationRequest(string clientId, string clientSecret, Scope[] scopes)
    {
        string url = "https://api.amazon.com/auth/o2/create/codepair";

        string scopeString = string.Empty;
        for (int i = 0; i < scopes.Length; i++)
        {
            if (i > 0)
            {
                scopeString += " ";
            }

            scopeString += ScopeMethods.ScopeToString(scopes[i]);
        }

        var formUrlEncodedContent = new FormUrlEncodedContent(new Dictionary<string, string>()
        {
            { "response_type", "device_code" },
            { "client_id", clientId },
            { "client_secret", clientSecret },
            { "scope", scopeString }
        });

        HttpResponseMessage response = await RetryHelper.ExecuteWithRetryAsync(
            () => Plugin.HttpClient.PostAsync(url, formUrlEncodedContent),
            Logger,
            "LwaDeviceAuth").ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            Dictionary<string, string>? json = JsonConvert.DeserializeObject<Dictionary<string, string>>(content);
            if (json != null
                && json.TryGetValue("user_code", out var userCode)
                && json.TryGetValue("device_code", out var deviceCode)
                && json.TryGetValue("verification_uri", out var verificationUri)
                && json.TryGetValue("expires_in", out var expiresInStr)
                && int.TryParse(expiresInStr, out int expiresIn)
                && json.TryGetValue("interval", out var intervalStr)
                && int.TryParse(intervalStr, out int interval))
            {
                return new DeviceAuthorizationRequest(
                    userCode,
                    deviceCode,
                    verificationUri,
                    new DateTimeOffset(DateTime.UtcNow).AddSeconds(expiresIn).ToUnixTimeSeconds(),
                    interval);
            }
            else
            {
                throw new JsonException("Could not get access token: " + content);
            }
        }
        else
        {
            string errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpRequestException(
                $"Device authorization request failed with status {response.StatusCode}: {errorContent}");
        }
    }

    /// <summary>
    /// Gets the device token for authenticated requests to SMAPI.
    /// </summary>
    /// <param name="deviceAuthorizationRequest">Device authorization request.</param>
    /// <returns>Device token.</returns>
    public static async Task<DeviceToken?> GetDeviceToken(DeviceAuthorizationRequest deviceAuthorizationRequest)
    {
        string url = "https://api.amazon.com/auth/o2/token";
        var formUrlEncodedContent = new FormUrlEncodedContent(new Dictionary<string, string>()
        {
            { "grant_type", "device_code" },
            { "device_code", deviceAuthorizationRequest.DeviceCode },
            { "user_code", deviceAuthorizationRequest.UserCode }
        });

        // poll the api until we reach the timeout or the user granted the authorization
        while (deviceAuthorizationRequest.ExpireTimestamp > new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds())
        {
            HttpResponseMessage response = await Plugin.HttpClient.PostAsync(url, formUrlEncodedContent).ConfigureAwait(false);

            string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Dictionary<string, string>? json = JsonConvert.DeserializeObject<Dictionary<string, string>>(content);

            if (response.IsSuccessStatusCode)
            {
                return ParseTokenResponse(json, content);
            }
            else if (json != null && json.TryGetValue("error", out var error) && error == "authorization_pending")
            {
                Thread.Sleep(deviceAuthorizationRequest.Interval * 1000);
            }
            else
            {
                throw new InvalidOperationException($"LWA device token polling failed: {json?.GetValueOrDefault("error", "unknown error")}");
            }
        }

        throw new TimeoutException("LWA device authorization timed out before user completed login.");
    }

    /// <summary>
    /// Exchanges an authorization code for access and refresh tokens.
    /// </summary>
    /// <param name="code">The authorization code received from Amazon.</param>
    /// <param name="clientId">LWA client id.</param>
    /// <param name="clientSecret">LWA client secret.</param>
    /// <param name="redirectUri">The redirect URI used in the authorization request.</param>
    /// <returns>Device token.</returns>
    public static async Task<DeviceToken?> ExchangeAuthorizationCode(string code, string clientId, string clientSecret, string redirectUri)
    {
        string url = "https://api.amazon.com/auth/o2/token";
        var formUrlEncodedContent = new FormUrlEncodedContent(new Dictionary<string, string>()
        {
            { "grant_type", "authorization_code" },
            { "code", code },
            { "client_id", clientId },
            { "client_secret", clientSecret },
            { "redirect_uri", redirectUri }
        });

        HttpResponseMessage response = await RetryHelper.ExecuteWithRetryAsync(
            () => Plugin.HttpClient.PostAsync(url, formUrlEncodedContent),
            Logger,
            "LwaExchangeCode")
            .ConfigureAwait(false);

        string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Authorization code exchange failed with status {response.StatusCode}: {content}");
        }

        Dictionary<string, string>? json = JsonConvert.DeserializeObject<Dictionary<string, string>>(content);
        return ParseTokenResponse(json, content);
    }

    /// <summary>
    /// Refreshes the access token.
    /// </summary>
    /// <param name="deviceToken">Device token.</param>
    /// <param name="clientId">LWA client id.</param>
    /// <param name="clientSecret">LWA client secret.</param>
    /// <returns>Device token.</returns>
    public static async Task<DeviceToken?> RefreshDeviceToken(DeviceToken deviceToken, string clientId, string clientSecret)
    {
        string url = "https://api.amazon.com/auth/o2/token";
        var formUrlEncodedContent = new FormUrlEncodedContent(new Dictionary<string, string>()
        {
            { "grant_type", "refresh_token" },
            { "refresh_token", deviceToken.RefreshToken },
            { "client_id", clientId },
            { "client_secret", clientSecret }
        });

        HttpResponseMessage response = await RetryHelper.ExecuteWithRetryAsync(
            () => Plugin.HttpClient.PostAsync(url, formUrlEncodedContent),
            Logger,
            "LwaRefreshToken").ConfigureAwait(false);

        string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Token refresh failed with status {response.StatusCode}: {content}");
        }

        Dictionary<string, string>? json = JsonConvert.DeserializeObject<Dictionary<string, string>>(content);
        return ParseTokenResponse(json, content);
    }

    /// <summary>
    /// Parses a token response JSON into a <see cref="DeviceToken"/>.
    /// </summary>
    private static DeviceToken ParseTokenResponse(Dictionary<string, string>? json, string rawContent)
    {
        if (json != null
            && json.TryGetValue("access_token", out var token)
            && json.TryGetValue("refresh_token", out var refreshToken)
            && json.TryGetValue("token_type", out var tokenType)
            && json.TryGetValue("expires_in", out var expiresInStr)
            && int.TryParse(expiresInStr, out int expiresIn))
        {
            return new DeviceToken(token, refreshToken, tokenType,
                new DateTimeOffset(DateTime.UtcNow).AddSeconds(expiresIn).ToUnixTimeSeconds());
        }

        throw new JsonException("Could not parse token response: " + rawContent);
    }
}
