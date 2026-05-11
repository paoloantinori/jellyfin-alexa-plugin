using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Profile;
using Alexa.NET.Request;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Retrieves customer profile information (name, timezone) from the Alexa Profile API.
/// Falls back gracefully when permissions are not granted.
/// </summary>
internal class CustomerProfileService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ILogger _logger;

    public CustomerProfileService(ILogger<CustomerProfileService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the customer's given (first) name. Returns null if unavailable or permission denied.
    /// </summary>
    /// <param name="context">The Alexa request context containing API credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The customer's given name, or null if unavailable.</returns>
    public async Task<string?> GetGivenNameAsync(Context context, CancellationToken cancellationToken)
    {
        string? token = context?.System?.ApiAccessToken;
        string? endpoint = context?.System?.ApiEndpoint;

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(endpoint))
        {
            _logger.LogDebug("Customer profile: missing API token or endpoint");
            return null;
        }

        try
        {
            var client = new CustomerProfileClient(endpoint, token);
            string name = await client.GivenName().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Customer profile: name permission not granted");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Customer profile: failed to retrieve given name");
            return null;
        }
    }

    /// <summary>
    /// Gets the customer's timezone (e.g. "America/New_York"). Returns null if unavailable.
    /// </summary>
    /// <param name="context">The Alexa request context containing API credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The customer's timezone, or null if unavailable.</returns>
    public async Task<string?> GetTimezoneAsync(Context context, CancellationToken cancellationToken)
    {
        string? token = context?.System?.ApiAccessToken;
        string? endpoint = context?.System?.ApiEndpoint;

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/v2/accounts/~current/settings/Profile.timezones");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string[]? timezones = JsonSerializer.Deserialize<string[]>(json);
            return timezones is { Length: > 0 } ? timezones[0] : null;
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogDebug("Customer profile: timezone permission not granted");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Customer profile: failed to retrieve timezone");
            return null;
        }
    }
}
