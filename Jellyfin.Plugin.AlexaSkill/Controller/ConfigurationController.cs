using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.AlexaSkill.Controller.Handler;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Controller class for api requests.
/// </summary>
[ApiController]
[Route("alexaskill/api/")]
public class ConfigurationController : ControllerBase
{
    /// <summary>
    /// Uri of the plugin api.
    /// </summary>
    public const string ApiBaseUri = "alexaskill/api/";

    private readonly IUserManager _userManager;
    private readonly ILogger<ConfigurationController> _logger;
    private LwaAuthorizationRequestHandler lwaAuthorizationRequestHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILogger{RequestController}"/> interface.</param>
    public ConfigurationController(
        IUserManager userManager,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory)
    {
        _userManager = userManager;
        _logger = loggerFactory.CreateLogger<ConfigurationController>();

        lwaAuthorizationRequestHandler = Plugin.Instance!.LwaAuthorizationRequestHandler;
    }

    /// <summary>
    /// Update the specified user skill.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="json">The json request body.</param>
    /// <returns>A <see cref="ActionResult"/>.</returns>
    [HttpPatch("user-skills/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult UpdateUserSkill([FromRoute] string userId, [FromBody] dynamic json)
    {
        if (!TryResolvePluginUser(userId, out var pluginUser, out var error))
        {
            return error!;
        }

        Dictionary<string, string> req = JsonConvert.DeserializeObject<Dictionary<string, string>>(json.ToString());
        if (req.TryGetValue("InvocationName", out var invocationName)
            && IsValidInvocationName(invocationName))
        {
            if (pluginUser!.UserSkill == null)
            {
                return new JsonResult(new { error = "User has no skill" }, StatusCode(404));
            }

            pluginUser.UserSkill.InvocationName = invocationName;
            Plugin.Instance!.SaveConfiguration();

            return new JsonResult(pluginUser);
        }
        else
        {
            return new JsonResult(new { error = "Invalid invocation name" }, StatusCode(400));
        }
    }

    /// <summary>
    /// Create a new user skill.
    /// </summary>
    /// <param name="json">The json request body.</param>
    /// <returns>A <see cref="ActionResult"/>.</returns>
    [HttpPost("user-skills")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult CreateNewUserSkill([FromBody] dynamic json)
    {
        Dictionary<string, string> req = JsonConvert.DeserializeObject<Dictionary<string, string>>(json.ToString());
        if (!req.TryGetValue("Username", out var username) || username.Length == 0)
        {
            return new JsonResult(new { error = "Invalid username" }) { StatusCode = 400 };
        }

        if (!req.TryGetValue("InvocationName", out var invocationName)
            || !IsValidInvocationName(invocationName))
        {
            return new JsonResult(new { error = "Invalid invocation name" }) { StatusCode = 400 };
        }

        Jellyfin.Database.Implementations.Entities.User? jellyfinUser = _userManager.GetUserByName(username);
        if (jellyfinUser == null)
        {
            return new JsonResult(new { error = "Could not find jellyfin user" }) { StatusCode = 404 };
        }

        UserSkill userSkill = new UserSkill
        {
            InvocationName = invocationName,
            UserSkillStatus = UserSkillStatus.LwaAuthPending
        };

        User user = new User
        {
            Id = jellyfinUser.Id,
            UserSkill = userSkill
        };

        try
        {
            Plugin.Instance!.Configuration.AddUser(user);
        }
        catch (ArgumentException)
        {
            return new JsonResult(new { error = "User skill already exists" }) { StatusCode = 400 };
        }

        Plugin.Instance!.SaveConfiguration();

        return new JsonResult(user);
    }

    /// <summary>
    /// Delete the specified user skill.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>A <see cref="ActionResult"/>.</returns>
    [HttpDelete("user-skills/{userId}")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> DeleteUserSkill([FromRoute] string userId)
    {
        if (!TryResolvePluginUser(userId, out var pluginUser, out var error))
        {
            return error!;
        }

        string? skillId = pluginUser!.UserSkill?.SkillId;
        Plugin.Instance!.Configuration.DeleteUser(pluginUser.Id);
        Plugin.Instance!.SaveConfiguration();

        if (!string.IsNullOrEmpty(skillId))
        {
            bool otherUsersWithSameSkill = Plugin.Instance!.Configuration.Users.Any(u =>
                u.UserSkill?.SkillId == skillId);

            if (!otherUsersWithSameSkill)
            {
                await TryDeleteCloudSkillAsync(pluginUser, skillId).ConfigureAwait(false);
            }
        }

        return new OkResult();
    }

    private async Task TryDeleteCloudSkillAsync(Jellyfin.Plugin.AlexaSkill.Entities.User user, string skillId)
    {
        try
        {
            var smapi = user.SmapiManagement;
            if (smapi == null)
            {
                _logger.LogWarning("Cannot delete skill {SkillId} from cloud: no SMAPI management for user {UserId}", skillId, user.Id);
                return;
            }

            await smapi.DeleteSkillAsync(skillId).ConfigureAwait(false);
            _logger.LogInformation("Deleted cloud skill {SkillId} (last user removed)", skillId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete cloud skill {SkillId}. User was removed locally but skill still exists in the cloud.", skillId);
        }
    }

    /// <summary>
    /// Get a new lwa authorization url.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>A <see cref="ActionResult"/>.</returns>
    [HttpPut("user-skills/{userId}/authorization")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetUserSkillAuthorisation([FromRoute] string userId)
    {
        if (!TryResolvePluginUser(userId, out var pluginUser, out var error))
        {
            return error!;
        }

        if (pluginUser!.UserSkill == null)
        {
            return new JsonResult(new { error = "User has no skill" }) { StatusCode = 404 };
        }

        return new JsonResult(
            new
            {
                verificationUrl = LWAController.ApiBaseUri
                    + "?token="
                    + HttpUtility.UrlEncode(lwaAuthorizationRequestHandler.GetNewLwaAuthorizationRequest(pluginUser.Id))
            })
        {
            StatusCode = 200
        };
    }

    /// <summary>
    /// Test connectivity to a Jellyfin server URL.
    /// </summary>
    /// <param name="address">The server address to test. Falls back to saved config if empty.</param>
    /// <returns>A JSON object with connection test results.</returns>
    [HttpGet("test-connection")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> TestConnection([FromQuery] string? address = null)
    {
        address = address?.Trim() ?? Plugin.Instance!.Configuration.ServerAddress?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(address))
        {
            return new JsonResult(new { success = false, error = "No server address configured" });
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new JsonResult(new { success = false, error = "Invalid URL (must be HTTP or HTTPS)" });
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            HttpResponseMessage response = await Plugin.HttpClient
                .GetAsync(uri.AbsoluteUri.TrimEnd('/') + "/System/Info/Public", cts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new JsonResult(new
                {
                    success = true,
                    status = (int)response.StatusCode,
                    scheme = uri.Scheme,
                    host = uri.Host,
                    port = uri.Port
                });
            }

            return new JsonResult(new
            {
                success = false,
                error = $"Server returned HTTP {(int)response.StatusCode} ({response.StatusCode})",
                status = (int)response.StatusCode
            });
        }
        catch (HttpRequestException ex)
        {
            return new JsonResult(new
            {
                success = false,
                error = ex.InnerException?.Message ?? ex.Message
            });
        }
        catch (OperationCanceledException)
        {
            return new JsonResult(new { success = false, error = "Connection timed out (10s)" });
        }
    }

    /// <summary>
    /// Validates that an invocation name meets Amazon's requirements (at least 2 words).
    /// </summary>
    private static bool IsValidInvocationName(string name) =>
        name.Length > 0 && name.Contains(' ');

    private bool TryResolvePluginUser(string userId, out Jellyfin.Plugin.AlexaSkill.Entities.User? pluginUser, out ActionResult? error)
    {
        pluginUser = null;
        error = null;

        if (!Guid.TryParse(userId, out Guid userIdGuid))
        {
            error = new JsonResult(new { error = "Invalid user id format" }) { StatusCode = 400 };
            return false;
        }

        pluginUser = Plugin.Instance!.Configuration.GetUserById(userIdGuid);
        if (pluginUser == null)
        {
            error = new JsonResult(new { error = "Could not find user" }) { StatusCode = 404 };
            return false;
        }

        return true;
    }
}
