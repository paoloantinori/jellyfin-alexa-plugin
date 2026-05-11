using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AlexaSkill.ProactiveEvents;

/// <summary>
/// Client for sending proactive events to the Alexa Proactive Events API.
/// Uses the LWA <c>client_credentials</c> grant with <c>alexa::proactive_events</c> scope
/// to obtain a token independent of the per-user SMAPI device tokens.
/// </summary>
internal class ProactiveEventClient : IDisposable
{
    private readonly ILogger<ProactiveEventClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedAccessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public ProactiveEventClient(ILogger<ProactiveEventClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Send a proactive event to the Alexa Proactive Events API.
    /// </summary>
    /// <param name="userId">The Alexa user ID to target (from consent token).</param>
    /// <param name="eventPayload">The JSON-LD event payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the event was accepted.</returns>
    public async Task<bool> SendEventAsync(string userId, JObject eventPayload, CancellationToken cancellationToken = default)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || string.IsNullOrWhiteSpace(config.LwaClientId) || string.IsNullOrWhiteSpace(config.LwaClientSecret))
        {
            _logger.LogWarning("Cannot send proactive event: LWA credentials not configured");
            return false;
        }

        string accessToken = await GetOrCreateTokenAsync(config.LwaClientId, config.LwaClientSecret, cancellationToken).ConfigureAwait(false);

        var body = new JObject
        {
            ["timestamp"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["referenceId"] = Guid.NewGuid().ToString(),
            ["expiryTime"] = DateTime.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            ["event"] = eventPayload,
            ["relevantAudience"] = new JObject
            {
                ["type"] = "Unicast",
                ["payload"] = new JObject
                {
                    ["user"] = userId
                }
            }
        };

        // Determine stage based on skill status
        string stage = "development";
        string url = $"https://api.amazonalexa.com/v1/proactiveEvents/stages/{stage}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response = await Plugin.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Proactive event sent successfully to user {UserId}", userId);
                return true;
            }

            string errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if ((int)response.StatusCode == 432)
            {
                _logger.LogWarning("Proactive event rate limited for user {UserId}: {Body}", userId, errorContent);
            }
            else if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            {
                _logger.LogWarning("Proactive event auth failure — clearing cached token");
                await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _cachedAccessToken = null;
                    _tokenExpiresAt = DateTimeOffset.MinValue;
                }
                finally
                {
                    _tokenLock.Release();
                }
            }
            else
            {
                _logger.LogWarning("Proactive event failed: HTTP {Status} — {Body}", (int)response.StatusCode, errorContent);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send proactive event to user {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// Get or create a proactive events access token using client_credentials grant.
    /// </summary>
    private async Task<string> GetOrCreateTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedAccessToken != null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            {
                return _cachedAccessToken;
            }

            _logger.LogDebug("Requesting proactive events access token from LWA");

            var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "client_credentials" },
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "scope", "alexa::proactive_events" }
            });

            HttpResponseMessage response = await Plugin.HttpClient.PostAsync(
                "https://api.amazon.com/auth/o2/token",
                formContent,
                cancellationToken).ConfigureAwait(false);

            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Failed to obtain proactive events token: HTTP {(int)response.StatusCode} — {content}");
            }

            var json = JObject.Parse(content);
            _cachedAccessToken = json["access_token"]?.ToString()
                ?? throw new JsonException("Missing access_token in LWA response");

            int expiresIn = json["expires_in"]?.Value<int>() ?? 3600;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60); // 60s buffer

            _logger.LogDebug("Proactive events token obtained, expires in {ExpiresIn}s", expiresIn);
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Build an <c>AMAZON.MediaContent.Available</c> event payload.
    /// </summary>
    /// <param name="contentType">One of EPISODE, ALBUM, MOVIE.</param>
    /// <param name="name">Content title.</param>
    /// <param name="artistName">Artist or series name (optional).</param>
    /// <param name="seasonNumber">Season number for episodes (optional).</param>
    /// <param name="episodeNumber">Episode number for episodes (optional).</param>
    /// <returns>A JSON-LD event payload ready for the Proactive Events API.</returns>
    public static JObject BuildMediaContentAvailableEvent(
        string contentType,
        string name,
        string? artistName = null,
        int? seasonNumber = null,
        int? episodeNumber = null)
    {
        var uri = $"amzn1.alexa-skill.event.{Guid.NewGuid():N}";
        var mediaContent = new JObject
        {
            ["name"] = new JObject { ["value"] = name },
            ["contentType"] = contentType
        };

        if (!string.IsNullOrEmpty(artistName))
        {
            mediaContent["artistName"] = new JObject { ["value"] = artistName };
        }

        if (seasonNumber.HasValue)
        {
            mediaContent["seasonNumber"] = seasonNumber.Value;
        }

        if (episodeNumber.HasValue)
        {
            mediaContent["episodeNumber"] = episodeNumber.Value;
        }

        return new JObject
        {
            ["type"] = "AMAZON.MediaContent.Available",
            ["payload"] = new JObject
            {
                ["availability"] = new JObject
                {
                    ["startTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["provider"] = new JObject
                    {
                        ["name"] = "Jellyfin"
                    }
                },
                ["content"] = new JObject
                {
                    ["uri"] = uri,
                    ["name"] = new JObject { ["value"] = name }
                },
                ["metadata"] = mediaContent
            }
        };
    }

    /// <summary>
    /// Disposes the token lock and other resources.
    /// </summary>
    public void Dispose()
    {
        _tokenLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
