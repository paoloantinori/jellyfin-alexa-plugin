using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller.Handler;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Controller;

/// <summary>
/// Controller class for LWA authorization code flow.
/// </summary>
[ApiController]
[Route("alexaskill/lwa/")]
public class LWAController : ControllerBase
{
    /// <summary>
    /// Uri of the plugin api.
    /// </summary>
    public const string ApiBaseUri = "alexaskill/lwa/";

    private readonly ILogger<LWAController> _logger;

    private readonly LwaAuthorizationRequestHandler lwaAuthorizationRequestHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="LWAController"/> class.
    /// </summary>
    /// <param name="loggerFactory">Instance of the <see cref="ILogger{RequestController}"/> interface.</param>
    public LWAController(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<LWAController>();

        lwaAuthorizationRequestHandler = Plugin.Instance!.LwaAuthorizationRequestHandler;
    }

    /// <summary>
    /// Redirect the user to Amazon's authorization page.
    /// </summary>
    /// <param name="token">The state token correlating this request to a user.</param>
    /// <returns>Redirect to Amazon authorization page.</returns>
    [HttpGet]
    public ActionResult GetLwaAuthorizationRedirect([FromQuery(Name = "token")] string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest("Token parameter is required.");
        }

        if (string.IsNullOrWhiteSpace(Plugin.Instance!.Configuration.LwaClientId)
            || string.IsNullOrWhiteSpace(Plugin.Instance!.Configuration.LwaClientSecret))
        {
            return LoadErrorPage("Login with Amazon (LWA) is not setup yet.<br>Please configure the LWA Client ID and Secret in the plugin settings.");
        }

        LwaAuthorizationRequest? lwaAuthorizationRequest =
            lwaAuthorizationRequestHandler.GetLwaAuthorizationRequest(token);
        if (!lwaAuthorizationRequestHandler.ValidatLwaAuthorizePageToken(token) || lwaAuthorizationRequest == null)
        {
            return LoadErrorPage("Invalid or expired page token. Please try authorizing again from the plugin settings.");
        }

        string serverAddress = Plugin.Instance.Configuration.ServerAddress.TrimEnd('/');
        string redirectUri = $"{serverAddress}/{Config.LwaCallbackPath}";

        string scope = $"{ScopeMethods.ScopeToString(Scope.SkillsReadWrite)} {ScopeMethods.ScopeToString(Scope.ModelsReadWrite)} {ScopeMethods.ScopeToString(Scope.CatalogsReadWrite)}";

        string authUrl = $"https://www.amazon.com/ap/oa?client_id={Uri.EscapeDataString(Plugin.Instance.Configuration.LwaClientId)}&scope={Uri.EscapeDataString(scope)}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(token)}";

        return Redirect(authUrl);
    }

    /// <summary>
    /// Callback endpoint receiving the authorization code from Amazon.
    /// </summary>
    /// <param name="code">The authorization code from Amazon.</param>
    /// <param name="state">The state token correlating back to the user.</param>
    /// <returns>Success or error HTML page.</returns>
    [HttpGet("callback")]
    public async Task<ActionResult> Callback([FromQuery(Name = "code")] string? code, [FromQuery(Name = "state")] string? state)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return LoadErrorPage("Missing authorization code or state token.");
        }

        LwaAuthorizationRequest? lwaAuthorizationRequest =
            lwaAuthorizationRequestHandler.GetLwaAuthorizationRequest(state);
        if (!lwaAuthorizationRequestHandler.ValidatLwaAuthorizePageToken(state) || lwaAuthorizationRequest == null)
        {
            return LoadErrorPage("Invalid or expired authorization request. Please try authorizing again from the plugin settings.");
        }

        PluginConfiguration configuration = Plugin.Instance!.Configuration;
        string serverAddress = configuration.ServerAddress.TrimEnd('/');
        string redirectUri = $"{serverAddress}/{Config.LwaCallbackPath}";

        DeviceToken? deviceToken;
        try
        {
            deviceToken = await LwaClient.ExchangeAuthorizationCode(
                code,
                configuration.LwaClientId,
                configuration.LwaClientSecret,
                redirectUri).ConfigureAwait(false);

            if (deviceToken == null)
            {
                return LoadErrorPage("Failed to exchange authorization code for tokens.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exchanging authorization code");
            return LoadErrorPage($"Authorization code exchange failed: {ex.Message}");
        }

        // Update the user
        User? user = configuration.GetUserById(lwaAuthorizationRequest.UserId);
        if (user == null)
        {
            return LoadErrorPage("Could not find the user associated with this authorization request.");
        }

        user.SmapiDeviceToken = deviceToken;
        user.SmapiRefreshToken = deviceToken.RefreshToken;
        if (user.UserSkill == null)
        {
            user.UserSkill = new UserSkill { InvocationName = Config.InvocationName };
        }

        Plugin.Instance!.SaveConfiguration();
        lwaAuthorizationRequestHandler.RemoveLwaAuthorizeRequest(state);

        // If the skill already exists, this is a re-authorization — just refresh the token
        bool skillExists = !string.IsNullOrEmpty(user.UserSkill.SkillId);
        if (skillExists)
        {
            _logger.LogInformation("Re-authorization completed for user {UserId}, token refreshed with updated scopes", user.Id);
        }
        else
        {
            user.UserSkill.UserSkillStatus = UserSkillStatus.SkillCreating;
            Plugin.Instance!.SaveConfiguration();

            // Create the skill in background
            Guid userId = user.Id;
            _ = Task.Run(async () =>
            {
                // Re-resolve the user from the current config to avoid stale references
                // if a concurrent UI save replaced the configuration object.
                User? currentUser = Plugin.Instance!.Configuration.Users.FirstOrDefault(u => u.Id == userId);
                if (currentUser == null)
                {
                    _logger.LogError("User {UserId} no longer exists in config after LWA callback", userId);
                    return;
                }

                if (currentUser.SmapiManagement == null)
                {
                    _logger.LogError("SmapiManagement is null for user with id {UserId}", userId);
                    return;
                }

                _logger.LogInformation("Creating skill for user with id {UserId}", userId);
                Collection<SkillInteractionModel> skillInteractionModels = Plugin.Instance.BuildSkillInteractionModels(currentUser.UserSkill.InvocationName);

                try
                {
                    Uri endpointUri = new Uri(new Uri(Plugin.Instance.Configuration.ServerAddress), AlexaSkillController.ApiBaseUri);
                    string endpointUriString = new Uri(endpointUri, "account-linking").ToString();

                    string skillId = await AlexaUtil.CallAsync(currentUser, () => currentUser.SmapiManagement.CreateSkillAsync(
                        Plugin.Instance.ManifestSkill!,
                        skillInteractionModels,
                        endpointUriString,
                        Plugin.Instance.Configuration.AccountLinkingClientId)).ConfigureAwait(false);

                    currentUser.UserSkill.SkillId = skillId;
                    currentUser.UserSkill.UserSkillStatus = UserSkillStatus.AccountLinkPending;
                    Plugin.Instance!.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating skill for user {UserId}", userId);
                    currentUser.UserSkill.UserSkillStatus = UserSkillStatus.LwaAuthPending;
                    Plugin.Instance!.SaveConfiguration();
                }
            });
        }

        // Return success page
        var assembly = typeof(Util).Assembly;
        Stream? resource = assembly.GetManifestResourceStream("Jellyfin.Plugin.AlexaSkill.Controller.Pages.lwa_success.html");
        if (resource == null)
        {
            return new JsonResult(new { error = "Could not load success page" }) { StatusCode = 500 };
        }

        string page = await new StreamReader(resource).ReadToEndAsync().ConfigureAwait(false);
        return Content(page, "text/html");
    }

    private ActionResult LoadErrorPage(string errorMessage)
    {
        var assembly = typeof(Util).Assembly;
        Stream? resource = assembly.GetManifestResourceStream("Jellyfin.Plugin.AlexaSkill.Controller.Pages.lwa_error.html");
        if (resource == null)
        {
            return new JsonResult(new { error = errorMessage }) { StatusCode = 500 };
        }

        string page = new StreamReader(resource).ReadToEnd();
        page = page.Replace("{{ error }}", errorMessage, StringComparison.Ordinal);
        return Content(page, "text/html");
    }
}
