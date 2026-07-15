using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Alexa.NET.Management;
using Alexa.NET.Management.Api;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller.Handler;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    private readonly ModelDeploymentManager _modelDeploymentManager;
    private readonly IInteractionModelRedeployer _redeployer;
    private LwaAuthorizationRequestHandler lwaAuthorizationRequestHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationController"/> class.
    /// </summary>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILogger{RequestController}"/> interface.</param>
    /// <param name="modelDeploymentManager">Instance of the <see cref="ModelDeploymentManager"/> class.</param>
    /// <param name="redeployer">Redeploys interaction models to Amazon when a published-skill setting changes.</param>
    public ConfigurationController(
        IUserManager userManager,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        ModelDeploymentManager modelDeploymentManager,
        IInteractionModelRedeployer redeployer)
    {
        _userManager = userManager;
        _logger = loggerFactory.CreateLogger<ConfigurationController>();
        _modelDeploymentManager = modelDeploymentManager;
        _redeployer = redeployer;

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
    public async Task<ActionResult> UpdateUserSkill([FromRoute] string userId, [FromBody] dynamic json)
    {
        if (!TryResolvePluginUser(userId, out var pluginUser, out var error, autoProvision: true))
        {
            return error!;
        }

        JObject req = JObject.Parse(json.ToString());
        bool updated = false;
        bool invocationNameChanged = false;

        // Handle InvocationName (requires UserSkill to exist)
        if (req.TryGetValue("InvocationName", out var invocationToken)
            && invocationToken.Type == JTokenType.String)
        {
            string invocationName = invocationToken.Value<string>()!;
            if (!IsValidInvocationName(invocationName))
            {
                return new JsonResult(new { error = "Invalid invocation name" }) { StatusCode = 400 };
            }

            if (pluginUser!.UserSkill == null)
            {
                return new JsonResult(new { error = "User has no skill" }) { StatusCode = 404 };
            }

            invocationNameChanged = !string.Equals(
                pluginUser.UserSkill.InvocationName, invocationName, StringComparison.Ordinal);
            pluginUser.UserSkill.InvocationName = invocationName;
            updated = true;
        }

        // Handle AllowedLibraryIds (can be updated regardless of UserSkill)
        if (req.TryGetValue("AllowedLibraryIds", out var libraryIdsToken))
        {
            if (libraryIdsToken.Type == JTokenType.Array)
            {
                var ids = libraryIdsToken.ToObject<List<string>>();
                pluginUser!.AllowedLibraryIds = ids?.Count > 0 ? ids : null;
                LibraryFilter.InvalidateCache();
                updated = true;
            }
        }

        // Handle FuzzyMatchBehavior (string enum)
        if (req.TryGetValue("FuzzyMatchBehavior", out var behaviorToken)
            && behaviorToken.Type == JTokenType.String)
        {
            if (Enum.TryParse<Configuration.FuzzyMatchBehavior>(behaviorToken.Value<string>(), ignoreCase: true, out var behavior))
            {
                pluginUser!.FuzzyMatchBehavior = behavior;
                updated = true;
            }
        }

        // Handle FuzzyMatchThreshold (integer, 0-100)
        if (req.TryGetValue("FuzzyMatchThreshold", out var thresholdToken)
            && thresholdToken.Type == JTokenType.Integer)
        {
            int val = thresholdToken.Value<int>();
            if (val < 0 || val > 100)
            {
                return new JsonResult(new { error = "FuzzyMatchThreshold must be between 0 and 100" }) { StatusCode = 400 };
            }

            pluginUser!.FuzzyMatchThreshold = val;
            updated = true;
        }

        // Handle FuzzySuggestionThreshold (integer, 0-100)
        if (req.TryGetValue("FuzzySuggestionThreshold", out var suggestionToken)
            && suggestionToken.Type == JTokenType.Integer)
        {
            int val = suggestionToken.Value<int>();
            if (val < 0 || val > 100)
            {
                return new JsonResult(new { error = "FuzzySuggestionThreshold must be between 0 and 100" }) { StatusCode = 400 };
            }

            pluginUser!.FuzzySuggestionThreshold = val;
            updated = true;
        }

        // Handle AnnouncePositionOnResume (boolean)
        if (req.TryGetValue("AnnouncePositionOnResume", out var announceToken)
            && announceToken.Type == JTokenType.Boolean)
        {
            pluginUser!.AnnouncePositionOnResume = announceToken.Value<bool>();
            updated = true;
        }

        // Handle SearchResponseMode (string enum: "Thorough", "Fast", or null for global default)
        if (req.TryGetValue("SearchResponseMode", out var modeToken))
        {
            if (modeToken.Type == JTokenType.String
                && Enum.TryParse<Configuration.SearchResponseMode>(modeToken.Value<string>(), ignoreCase: true, out var mode))
            {
                pluginUser!.SearchResponseMode = mode;
                updated = true;
            }
            else if (modeToken.Type == JTokenType.Null)
            {
                pluginUser!.SearchResponseMode = null;
                updated = true;
            }
        }

        // Handle PostPlayBehavior (string enum: "Stop", "AutoPlay", "Ask", or null for global default)
        if (req.TryGetValue("PostPlayBehavior", out var ppbToken))
        {
            if (ppbToken.Type == JTokenType.String
                && Enum.TryParse<Configuration.PostPlayBehavior>(ppbToken.Value<string>(), ignoreCase: true, out var ppb))
            {
                pluginUser!.PostPlayBehavior = ppb;
                updated = true;
            }
            else if (ppbToken.Type == JTokenType.Null)
            {
                pluginUser!.PostPlayBehavior = null;
                updated = true;
            }
        }

        // Handle VideoAppForAudio (boolean, or null to inherit the global NativeControlsForAudio default)
        if (req.TryGetValue("VideoAppForAudio", out var vaaToken))
        {
            if (vaaToken.Type == JTokenType.Boolean)
            {
                pluginUser!.VideoAppForAudio = vaaToken.Value<bool>();
                updated = true;
            }
            else if (vaaToken.Type == JTokenType.Null)
            {
                pluginUser!.VideoAppForAudio = null;
                updated = true;
            }
        }

        if (!updated)
        {
            return new JsonResult(new { error = "No valid fields to update" }) { StatusCode = 400 };
        }

        Plugin.Instance!.SaveConfiguration();

        // JF-297: an invocation-name change must reach Amazon. Rebuild and redeploy the
        // interaction models for the user's existing skill. Skipped when the name did not
        // actually change or when the user has no live skill yet (nothing to deploy).
        object? redeployInfo = null;
        if (invocationNameChanged
            && !string.IsNullOrEmpty(pluginUser!.UserSkill?.SkillId)
            && pluginUser.SmapiDeviceToken != null)
        {
            try
            {
                ModelRedeployResult redeployResult = await _redeployer.RedeployAsync(
                    pluginUser,
                    pluginUser.UserSkill!.InvocationName,
                    CancellationToken.None,
                    Plugin.Instance!.Configuration.CustomModelLocale).ConfigureAwait(false);

                var config = Plugin.Instance!.Configuration;
                config.LastModelDeployTime = DateTime.UtcNow;
                config.LastModelDeployStatus = redeployResult.Status;
                Plugin.Instance!.SaveConfiguration();

                redeployInfo = new
                {
                    success = redeployResult.Success,
                    status = redeployResult.Status,
                    localeCount = redeployResult.LocaleCount,
                    succeededCount = redeployResult.SucceededCount,
                    failedCount = redeployResult.Locales.Count(r => !r.Value.Success)
                };
            }
            catch (Exception ex)
            {
                // The local invocation name was already updated and saved; do not fail the
                // whole request if the Amazon-side redeploy errors. The change can be retried.
                _logger.LogError(ex, "Failed to redeploy interaction models after invocation-name change for user {UserId}", userId);
                redeployInfo = new { success = false, status = "error", error = ex.Message };
            }
        }

        return new JsonResult(new { user = pluginUser, redeploy = redeployInfo });
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
    /// Update general (non-user) configuration fields on the live config object.
    /// Preserves Users collection and all [JsonIgnore] properties (SmapiDeviceToken, JellyfinToken).
    /// </summary>
    /// <param name="json">JSON with config fields to update.</param>
    /// <returns>Success result.</returns>
    [HttpPatch("config")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult UpdateGeneralConfig([FromBody] dynamic json)
    {
        JObject req = JObject.Parse(json.ToString());
        var config = Plugin.Instance!.Configuration;
        bool updated = false;

        if (req.TryGetValue("ServerAddress", out var serverToken) && serverToken.Type == JTokenType.String)
        {
            config.ServerAddress = serverToken.Value<string>()!;
            updated = true;
        }

        if (req.TryGetValue("SslCertType", out var sslToken) && sslToken.Type == JTokenType.String)
        {
            if (Enum.TryParse<SslCertificateType>(sslToken.Value<string>(), ignoreCase: true, out var ssl))
            {
                config.SslCertType = ssl;
                updated = true;
            }
        }

        if (req.TryGetValue("LwaClientId", out var clientIdToken) && clientIdToken.Type == JTokenType.String)
        {
            config.LwaClientId = clientIdToken.Value<string>()!;
            updated = true;
        }

        if (req.TryGetValue("LwaClientSecret", out var clientSecretToken) && clientSecretToken.Type == JTokenType.String)
        {
            config.LwaClientSecret = clientSecretToken.Value<string>()!;
            updated = true;
        }

        if (req.TryGetValue("RadioModeEnabled", out var radioToken) && radioToken.Type == JTokenType.Boolean)
        { config.RadioModeEnabled = radioToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("PodcastsEnabled", out var podcastsToken) && podcastsToken.Type == JTokenType.Boolean)
        { config.PodcastsEnabled = podcastsToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("LiveTvEnabled", out var liveTvToken) && liveTvToken.Type == JTokenType.Boolean)
        { config.LiveTvEnabled = liveTvToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("SleepTimerEnabled", out var sleepToken) && sleepToken.Type == JTokenType.Boolean)
        { config.SleepTimerEnabled = sleepToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("QueueManagementEnabled", out var queueToken) && queueToken.Type == JTokenType.Boolean)
        { config.QueueManagementEnabled = queueToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("BrowseLibraryEnabled", out var browseToken) && browseToken.Type == JTokenType.Boolean)
        { config.BrowseLibraryEnabled = browseToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("RecommendationsEnabled", out var recToken) && recToken.Type == JTokenType.Boolean)
        { config.RecommendationsEnabled = recToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("AplVisualsEnabled", out var aplToken) && aplToken.Type == JTokenType.Boolean)
        { config.AplVisualsEnabled = aplToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("VideoPlaybackEnabled", out var videoToken) && videoToken.Type == JTokenType.Boolean)
        { config.VideoPlaybackEnabled = videoToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("MusicEnabled", out var musicToken) && musicToken.Type == JTokenType.Boolean)
        { config.MusicEnabled = musicToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("VideosEnabled", out var videosToken) && videosToken.Type == JTokenType.Boolean)
        { config.VideosEnabled = videosToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("BooksEnabled", out var booksToken) && booksToken.Type == JTokenType.Boolean)
        { config.BooksEnabled = booksToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("SimulatorEnabled", out var simToken) && simToken.Type == JTokenType.Boolean)
        { config.SimulatorEnabled = simToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("ResumeOfferEnabled", out var resumeOfferToken) && resumeOfferToken.Type == JTokenType.Boolean)
        { config.ResumeOfferEnabled = resumeOfferToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("ResumeAnnounceTitle", out var resumeAnnounceToken) && resumeAnnounceToken.Type == JTokenType.Boolean)
        { config.ResumeAnnounceTitle = resumeAnnounceToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("AsrCompoundWordFixEnabled", out var asrToken) && asrToken.Type == JTokenType.Boolean)
        { config.AsrCompoundWordFixEnabled = asrToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("SeekEnabled", out var seekToken) && seekToken.Type == JTokenType.Boolean)
        { config.SeekEnabled = seekToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("PauseAnnouncePosition", out var pauseAnnounceToken) && pauseAnnounceToken.Type == JTokenType.Boolean)
        { config.PauseAnnouncePosition = pauseAnnounceToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("NativeControlsForAudio", out var nativeControlsToken) && nativeControlsToken.Type == JTokenType.Boolean)
        { config.NativeControlsForAudio = nativeControlsToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("NativeControlsForBooks", out var nativeBooksToken) && nativeBooksToken.Type == JTokenType.Boolean)
        { config.NativeControlsForBooks = nativeBooksToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("InitialFetchSize", out var fetchToken) && fetchToken.Type == JTokenType.Integer)
        { config.InitialFetchSize = fetchToken.Value<int>(); updated = true; }

        if (req.TryGetValue("ContinuationBatchSize", out var batchToken) && batchToken.Type == JTokenType.Integer)
        { config.ContinuationBatchSize = batchToken.Value<int>(); updated = true; }

        if (req.TryGetValue("PrefetchThreshold", out var prefetchToken) && prefetchToken.Type == JTokenType.Integer)
        { config.PrefetchThreshold = prefetchToken.Value<int>(); updated = true; }

        if (req.TryGetValue("MaxSearchResults", out var searchToken) && searchToken.Type == JTokenType.Integer)
        { config.MaxSearchResults = searchToken.Value<int>(); updated = true; }

        if (req.TryGetValue("MaxBrowseResults", out var browseResultsToken) && browseResultsToken.Type == JTokenType.Integer)
        { config.MaxBrowseResults = browseResultsToken.Value<int>(); updated = true; }

        if (req.TryGetValue("MaxRecentlyAddedResults", out var recentToken) && recentToken.Type == JTokenType.Integer)
        { config.MaxRecentlyAddedResults = recentToken.Value<int>(); updated = true; }

        if (req.TryGetValue("MaxRecommendationResults", out var maxRecToken) && maxRecToken.Type == JTokenType.Integer)
        { config.MaxRecommendationResults = maxRecToken.Value<int>(); updated = true; }

        if (req.TryGetValue("VideoAudioCacheSizeMB", out var cacheSizeToken) && cacheSizeToken.Type == JTokenType.Integer)
        {
            int val = cacheSizeToken.Value<int>();
            if (val < 256 || val > 16384)
            {
                return new JsonResult(new { error = "VideoAudioCacheSizeMB must be between 256 and 16384" }) { StatusCode = 400 };
            }

            config.VideoAudioCacheSizeMB = val;
            updated = true;
        }

        if (req.TryGetValue("CustomModelEnabled", out var customEnabledToken) && customEnabledToken.Type == JTokenType.Boolean)
        { config.CustomModelEnabled = customEnabledToken.Value<bool>(); updated = true; }

        if (req.TryGetValue("CustomModelUrl", out var customUrlToken) && customUrlToken.Type == JTokenType.String)
        {
            config.CustomModelUrl = customUrlToken.Value<string>();
            updated = true;
        }

        if (req.TryGetValue("CustomModelLocale", out var customLocaleToken) && customLocaleToken.Type == JTokenType.String)
        {
            config.CustomModelLocale = customLocaleToken.Value<string>() ?? "en-US";
            updated = true;
        }

        // Handle DefaultSearchResponseMode (string enum: "Thorough" or "Fast")
        if (req.TryGetValue("DefaultSearchResponseMode", out var defaultModeToken)
            && defaultModeToken.Type == JTokenType.String)
        {
            if (Enum.TryParse<SearchResponseMode>(defaultModeToken.Value<string>(), ignoreCase: true, out var defaultMode))
            {
                config.DefaultSearchResponseMode = defaultMode;
                updated = true;
            }
        }

        if (!updated)
        {
            return new JsonResult(new { error = "No valid fields to update" }) { StatusCode = 400 };
        }

        var errors = config.Validate();
        if (errors.Count > 0)
        {
            return new JsonResult(new { error = string.Join("; ", errors) }) { StatusCode = 400 };
        }

        Plugin.Instance!.SaveConfiguration();
        return new OkResult();
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
    /// Deploys a custom interaction model from a URL to SMAPI.
    /// </summary>
    /// <param name="json">JSON with userId, optional url, and optional locale.</param>
    /// <returns>A JSON object with the deployment result.</returns>
    [HttpPost("custom-model/deploy")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> DeployCustomModel([FromBody] dynamic json)
    {
        JObject req = JObject.Parse(json.ToString());

        if (!TryResolveModelDeploymentContext(req, out var pluginUser, out var skillId, out var locale, out var error))
        {
            return error!;
        }

        var config = Plugin.Instance!.Configuration;
        string url = req.TryGetValue("url", out var urlToken) && urlToken.Type == JTokenType.String
            ? urlToken.Value<string>()!
            : config.CustomModelUrl ?? string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            return new JsonResult(new { error = "No model URL provided and CustomModelUrl is not configured" }) { StatusCode = 400 };
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string modelJson = await _modelDeploymentManager.FetchModelJsonAsync(url, cts.Token).ConfigureAwait(false);

            var validationResult = _modelDeploymentManager.ValidateModelJson(modelJson);
            if (!validationResult.IsValid)
            {
                return new JsonResult(new { error = $"Invalid model: {validationResult.ErrorMessage}" }) { StatusCode = 400 };
            }

            var result = await _modelDeploymentManager.DeployCustomModelAsync(
                modelJson, locale!, pluginUser!, skillId!, cts.Token).ConfigureAwait(false);

            config.LastModelDeployTime = DateTime.UtcNow;
            config.LastModelDeployStatus = result.Success ? $"deployed ({result.BuildStatus})" : $"failed: {result.Message}";
            Plugin.Instance!.SaveConfiguration();

            if (!result.Success)
            {
                return new JsonResult(new { error = result.Message, buildStatus = result.BuildStatus }) { StatusCode = 500 };
            }

            return new JsonResult(new
            {
                success = true,
                message = result.Message,
                buildStatus = result.BuildStatus,
                invocationName = validationResult.InvocationName,
                intentCount = validationResult.IntentCount,
                locale = locale!
            });
        }
        catch (ArgumentException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = 400 };
        }
        catch (TimeoutException)
        {
            config.LastModelDeployTime = DateTime.UtcNow;
            config.LastModelDeployStatus = "timed out";
            Plugin.Instance!.SaveConfiguration();
            return new JsonResult(new { error = "Deployment timed out" }) { StatusCode = 504 };
        }
        catch (HttpRequestException ex)
        {
            return new JsonResult(new { error = $"Failed to fetch model: {ex.Message}" }) { StatusCode = 502 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during custom model deployment");
            return new JsonResult(new { error = $"Internal error: {ex.Message}" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Restores the default (embedded) interaction model for a locale via SMAPI.
    /// </summary>
    /// <param name="json">JSON with userId and optional locale.</param>
    /// <returns>A JSON object with the deployment result.</returns>
    [HttpPost("custom-model/restore")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> RestoreDefaultModel([FromBody] dynamic json)
    {
        JObject req = JObject.Parse(json.ToString());

        if (!TryResolveModelDeploymentContext(req, out var pluginUser, out var skillId, out var locale, out var error))
        {
            return error!;
        }

        var config = Plugin.Instance!.Configuration;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await _modelDeploymentManager.RestoreDefaultModelAsync(
                locale!, pluginUser!, skillId!, cts.Token).ConfigureAwait(false);

            config.LastModelDeployTime = DateTime.UtcNow;
            config.LastModelDeployStatus = result.Success ? "restored" : $"restore failed: {result.Message}";
            Plugin.Instance!.SaveConfiguration();

            if (!result.Success)
            {
                return new JsonResult(new { error = result.Message }) { StatusCode = 500 };
            }

            return new JsonResult(new
            {
                success = true,
                message = "Default model restored",
                buildStatus = result.BuildStatus,
                locale = locale!
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during default model restore");
            return new JsonResult(new { error = $"Internal error: {ex.Message}" }) { StatusCode = 500 };
        }
    }

    [HttpPost("custom-model/rebuild")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> RebuildModels([FromBody] dynamic json)
    {
        JObject req = JObject.Parse(json.ToString());

        if (!TryResolveSmapiUser(req, out var pluginUser, out var skillId, out var userError))
        {
            return userError!;
        }

        if (Plugin.Instance!.ManifestSkill == null)
        {
            return new JsonResult(new { error = "Plugin manifest not loaded. Restart Jellyfin first." }) { StatusCode = 503 };
        }

        string? localeFilter = Plugin.Instance!.Configuration.CustomModelLocale;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var result = await _redeployer.RedeployAsync(
                pluginUser!,
                pluginUser!.UserSkill!.InvocationName,
                cts.Token,
                localeFilter).ConfigureAwait(false);

            var config = Plugin.Instance.Configuration;
            config.LastModelDeployTime = DateTime.UtcNow;
            config.LastModelDeployStatus = result.Status;
            Plugin.Instance!.SaveConfiguration();

            return new JsonResult(new
            {
                success = result.Success,
                message = $"Rebuilt {result.LocaleCount} models — {result.SucceededCount} succeeded, {result.Locales.Count(r => !r.Value.Success)} failed",
                locales = result.Locales.ToDictionary(r => r.Key, r => new { success = r.Value.Success, status = r.Value.Status, error = r.Value.Error }),
                updateFailures = result.UpdateFailures
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during model rebuild");
            return new JsonResult(new { error = $"Internal error: {ex.Message}" }) { StatusCode = 500 };
        }
    }

    /// <summary>
    /// Gets the current custom model deployment configuration and status.
    /// </summary>
    /// <returns>A JSON object with the custom model config values.</returns>
    [HttpGet("custom-model/status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetCustomModelStatus()
    {
        var config = Plugin.Instance!.Configuration;
        return new JsonResult(new
        {
            customModelUrl = config.CustomModelUrl,
            customModelLocale = config.CustomModelLocale,
            customModelEnabled = config.CustomModelEnabled,
            lastModelDeployTime = config.LastModelDeployTime,
            lastModelDeployStatus = config.LastModelDeployStatus,
            localeModelStatuses = config.LocaleModelStatuses
                .ToDictionary(e => e.Locale, e => new
                {
                    status = e.ToStatus().Status,
                    lastUpdated = e.ToStatus().LastUpdated,
                    error = e.ToStatus().Error,
                    source = e.ToStatus().Source,
                })
        });
    }

    /// <summary>
    /// Validates the invocation name. An empty/whitespace value is valid — it means
    /// "use locale defaults" (it-IT → "mia collezione", other locales → "jellyfin player";
    /// the effective name deployed to Amazon is always a valid 2+ word default). A
    /// non-empty value must still be at least two words (Amazon's requirement).
    /// </summary>
    private static bool IsValidInvocationName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        return trimmed.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves userId to a plugin user with valid SMAPI credentials and skill ID.
    /// Shared by model deployment endpoints and RebuildModels.
    /// </summary>
    private bool TryResolveSmapiUser(
        JObject body,
        out Jellyfin.Plugin.AlexaSkill.Entities.User? pluginUser,
        out string? skillId,
        out ActionResult? error)
    {
        pluginUser = null;
        skillId = null;
        error = null;

        if (!body.TryGetValue("userId", out var userIdToken) || string.IsNullOrWhiteSpace(userIdToken.Value<string>()))
        {
            error = new JsonResult(new { error = "userId is required" }) { StatusCode = 400 };
            return false;
        }

        if (!TryResolvePluginUser(userIdToken.Value<string>()!, out pluginUser, out error))
        {
            return false;
        }

        if (pluginUser!.SmapiDeviceToken == null)
        {
            error = new JsonResult(new { error = "User has no SMAPI device token. Complete Alexa authorization first." }) { StatusCode = 400 };
            return false;
        }

        if (pluginUser.UserSkill == null || string.IsNullOrEmpty(pluginUser.UserSkill.SkillId))
        {
            error = new JsonResult(new { error = "User has no skill ID. Complete skill creation first." }) { StatusCode = 400 };
            return false;
        }

        skillId = pluginUser.UserSkill.SkillId;
        return true;
    }

    /// <summary>
    /// Resolves and validates the common context needed by model deployment endpoints.
    /// Extracts userId from the request body, resolves the plugin user, and validates
    /// that the user has SMAPI credentials and a skill ID. Also resolves the locale.
    /// </summary>
    private bool TryResolveModelDeploymentContext(
        JObject body,
        out Jellyfin.Plugin.AlexaSkill.Entities.User? pluginUser,
        out string? skillId,
        out string? locale,
        out ActionResult? error)
    {
        locale = null;

        if (!TryResolveSmapiUser(body, out pluginUser, out skillId, out error))
        {
            return false;
        }

        var config = Plugin.Instance!.Configuration;
        locale = body.TryGetValue("locale", out var localeToken) && localeToken.Type == JTokenType.String
            ? localeToken.Value<string>()!
            : config.CustomModelLocale;

        if (string.IsNullOrWhiteSpace(locale))
        {
            error = new JsonResult(new { error = "No locale provided and CustomModelLocale is not configured" }) { StatusCode = 400 };
            return false;
        }

        return true;
    }

    private bool TryResolvePluginUser(string userId, out Jellyfin.Plugin.AlexaSkill.Entities.User? pluginUser, out ActionResult? error, bool autoProvision = false)
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
            if (!autoProvision)
            {
                error = new JsonResult(new { error = "Could not find user" }) { StatusCode = 404 };
                return false;
            }

            // Validate the GUID corresponds to a real Jellyfin user
            var jellyfinUser = _userManager.GetUserById(userIdGuid);
            if (jellyfinUser == null)
            {
                error = new JsonResult(new { error = "Could not find Jellyfin user" }) { StatusCode = 404 };
                return false;
            }

            // Auto-provision a new plugin user with default settings
            pluginUser = new Jellyfin.Plugin.AlexaSkill.Entities.User
            {
                Id = userIdGuid,
            };

            try
            {
                Plugin.Instance.Configuration.AddUser(pluginUser);
            }
            catch (ArgumentException)
            {
                // Race condition: user was added between GetUserById and AddUser.
                // Re-resolve and continue.
                pluginUser = Plugin.Instance.Configuration.GetUserById(userIdGuid);
                if (pluginUser == null)
                {
                    error = new JsonResult(new { error = "Could not find user" }) { StatusCode = 404 };
                    return false;
                }
            }

            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("Auto-provisioned plugin user for Jellyfin user {UserId}", userIdGuid);
        }

        return true;
    }
}
