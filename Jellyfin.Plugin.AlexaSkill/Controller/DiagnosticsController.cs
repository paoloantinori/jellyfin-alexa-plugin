using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Controller for plugin diagnostics and health information.
/// </summary>
[ApiController]
[Route("alexaskill/api/")]
public class DiagnosticsController : ControllerBase
{
    private readonly RequestCounters _counters;
    private readonly JellyfinConnectivityChecker _connectivityChecker;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsController"/> class.
    /// </summary>
    /// <param name="counters">Instance of the <see cref="RequestCounters"/> service.</param>
    /// <param name="connectivityChecker">Instance of the <see cref="JellyfinConnectivityChecker"/> service.</param>
    public DiagnosticsController(RequestCounters counters, JellyfinConnectivityChecker connectivityChecker)
    {
        _counters = counters;
        _connectivityChecker = connectivityChecker;
    }

    /// <summary>
    /// Get plugin diagnostics information.
    /// </summary>
    /// <returns>A JSON object with plugin state and metrics.</returns>
    [HttpGet("diagnostics")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetDiagnostics()
    {
        var config = Plugin.Instance!.Configuration;
        var validationErrors = config.Validate();

        var users = config.Users.Select(u => new
        {
            u.Id,
            u.Username,
            SkillStatus = u.UserSkill?.UserSkillStatus.ToString() ?? "None",
            SkillId = u.UserSkill?.SkillId,
            SmapiTokenExpiresAt = u.SmapiDeviceToken != null
                ? DateTimeOffset.FromUnixTimeSeconds(u.SmapiDeviceToken.ExpireTimestamp).UtcDateTime
                : (DateTime?)null,
            SmapiTokenExpired = u.SmapiDeviceToken != null
                && u.SmapiDeviceToken.ExpireTimestamp > 0
                && DateTimeOffset.FromUnixTimeSeconds(u.SmapiDeviceToken.ExpireTimestamp) < DateTimeOffset.UtcNow,
            SmapiRefreshTokenPresent = !string.IsNullOrEmpty(u.SmapiRefreshToken)
        }).ToList();

        var locales = Plugin.Instance.InteractionModels
            .Select(m => m.Item1)
            .ToList();

        var result = new
        {
            Version = Util.GetVersion(),
            Configuration = new
            {
                ServerAddressConfigured = !string.IsNullOrWhiteSpace(config.ServerAddress),
                LwaClientIdConfigured = !string.IsNullOrWhiteSpace(config.LwaClientId),
                LwaClientSecretConfigured = !string.IsNullOrWhiteSpace(config.LwaClientSecret),
                SslCertType = config.SslCertType.ToString(),
                UserCount = config.Users.Count,
                ValidationErrors = validationErrors
            },
            Users = users,
            Locales = locales,
            Metrics = new
            {
                _counters.TotalRequests,
                _counters.TotalErrors,
                PerType = _counters.PerType.ToDictionary(k => k.Key, v => v.Value),
                PerIntent = _counters.GetIntentMetrics()
            },
            Health = new
            {
                Status = DetermineHealthStatus(config, validationErrors),
                CheckedAt = DateTime.UtcNow
            }
        };

        return new JsonResult(result);
    }

    /// <summary>
    /// Get per-intent request metrics with timing data.
    /// </summary>
    /// <returns>A JSON object with per-intent counts, average/min/max response times, and error counts.</returns>
    [HttpGet("diagnostics/metrics")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetMetrics()
    {
        var result = new
        {
            TotalRequests = _counters.TotalRequests,
            TotalErrors = _counters.TotalErrors,
            ErrorRate = ComputeErrorRate(),
            Uptime = _counters.Uptime.ToString(),
            CacheHits = _counters.CacheHits,
            CacheMisses = _counters.CacheMisses,
            CacheHitRate = _counters.CacheHits + _counters.CacheMisses > 0
                ? Math.Round((double)_counters.CacheHits / (_counters.CacheHits + _counters.CacheMisses), 4)
                : 0,
            ResponseSizes = new
            {
                Small = _counters.ResponseSizeSmall,
                Medium = _counters.ResponseSizeMedium,
                Large = _counters.ResponseSizeLarge
            },
            PerType = _counters.PerType.ToDictionary(k => k.Key, v => v.Value),
            Intents = _counters.GetIntentMetrics()
                .OrderByDescending(kvp => kvp.Value.Count)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };

        return new JsonResult(result);
    }

    /// <summary>
    /// Get a lightweight health check for monitoring.
    /// </summary>
    /// <returns>A JSON object with health status, uptime, and error rate.</returns>
    [HttpGet("diagnostics/health")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> GetHealth()
    {
        var config = Plugin.Instance!.Configuration;
        var validationErrors = config.Validate();

        string status = DetermineHealthStatus(config, validationErrors);

        if (status == "Healthy" && _counters.TotalRequests > 10 && ComputeErrorRate() > 0.1)
        {
            status = "Degraded";
        }

        ConnectivityResult connectivity = await _connectivityChecker.CheckAsync().ConfigureAwait(false);

        if (!connectivity.IsReachable && status != "Unhealthy")
        {
            status = "Degraded";
        }

        var result = new
        {
            Status = status,
            Version = Util.GetVersion(),
            Uptime = _counters.Uptime.ToString(),
            TotalRequests = _counters.TotalRequests,
            TotalErrors = _counters.TotalErrors,
            ErrorRate = Math.Round(ComputeErrorRate(), 4),
            JellyfinConnectivity = new
            {
                connectivity.IsReachable,
                connectivity.Message,
                connectivity.ResponseTimeMs,
                connectivity.HttpStatusCode
            },
            CheckedAt = DateTime.UtcNow
        };

        return new JsonResult(result);
    }

    private double ComputeErrorRate() =>
        _counters.TotalRequests > 0 ? (double)_counters.TotalErrors / _counters.TotalRequests : 0;

    private static string DetermineHealthStatus(Configuration.PluginConfiguration config, IReadOnlyList<string> validationErrors)
    {
        if (string.IsNullOrWhiteSpace(config.ServerAddress))
        {
            return "Unhealthy";
        }

        if (validationErrors.Count > 0)
        {
            return "Degraded";
        }

        bool anyTokenExpired = config.Users.Any(u =>
            u.SmapiDeviceToken != null
            && u.SmapiDeviceToken.ExpireTimestamp > 0
            && DateTimeOffset.FromUnixTimeSeconds(u.SmapiDeviceToken.ExpireTimestamp) < DateTimeOffset.UtcNow);

        return anyTokenExpired ? "Degraded" : "Healthy";
    }
}
