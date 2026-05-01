using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsController"/> class.
    /// </summary>
    /// <param name="counters">Instance of the <see cref="RequestCounters"/> service.</param>
    public DiagnosticsController(RequestCounters counters)
    {
        _counters = counters;
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
                && DateTimeOffset.FromUnixTimeSeconds(u.SmapiDeviceToken.ExpireTimestamp) < DateTimeOffset.UtcNow
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
                PerType = _counters.PerType.ToDictionary(k => k.Key, v => v.Value)
            },
            Health = new
            {
                Status = DetermineHealthStatus(config, validationErrors),
                CheckedAt = DateTime.UtcNow
            }
        };

        return new JsonResult(result);
    }

    private static string DetermineHealthStatus(Configuration.PluginConfiguration config, List<string> validationErrors)
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
