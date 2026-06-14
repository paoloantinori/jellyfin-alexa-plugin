using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Management.AccountLinking;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Refit;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Setup the skill and update or create the skill in the Alexa cloud if it is outdated.
/// </summary>
public class SkillStartup : IHostedService, IDisposable
{
    private readonly ILogger<SkillStartup> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModelDeploymentManager _modelDeploymentManager;
    private readonly SearchResultCache _searchCache;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RequestCounters _requestCounters;
    private readonly JellyfinConnectivityChecker _connectivityChecker;
    private readonly Alexa.Playback.DeviceQueueManager _deviceQueueManager;
    private readonly Alexa.Playback.AudiobookPositionTracker _positionTracker;
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillStartup"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="loggerFactory">Logger.</param>
    /// <param name="httpClientFactory">HTTP client factory for outbound calls.</param>
    /// <param name="searchCache">Search result cache for fallback.</param>
    /// <param name="circuitBreaker">Circuit breaker for backend health tracking.</param>
    /// <param name="requestCounters">Request counters for metrics tracking.</param>
    /// <param name="connectivityChecker">Connectivity checker for Jellyfin server health.</param>
    /// <param name="deviceQueueManager">Per-device playback queue manager.</param>
    public SkillStartup(
        ISessionManager sessionManager,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory,
        ModelDeploymentManager modelDeploymentManager,
        SearchResultCache searchCache,
        CircuitBreaker circuitBreaker,
        RequestCounters requestCounters,
        JellyfinConnectivityChecker connectivityChecker,
        Alexa.Playback.DeviceQueueManager deviceQueueManager,
        Alexa.Playback.AudiobookPositionTracker positionTracker)
    {
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _modelDeploymentManager = modelDeploymentManager;
        _searchCache = searchCache;
        _circuitBreaker = circuitBreaker;
        _requestCounters = requestCounters;
        _connectivityChecker = connectivityChecker;
        _deviceQueueManager = deviceQueueManager;
        _positionTracker = positionTracker;
        _logger = loggerFactory.CreateLogger<SkillStartup>();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Skill version (local): v{Version}", Util.GetVersion());

        Plugin.Instance!.HttpClientFactory = _httpClientFactory;
        Plugin.Instance!.SearchCache = _searchCache;
        Plugin.Instance!.CircuitBreaker = _circuitBreaker;
        Plugin.Instance!.RequestCounters = _requestCounters;
        Plugin.Instance!.ConnectivityChecker = _connectivityChecker;
        Plugin.Instance!.DeviceQueueManager = _deviceQueueManager;
        Plugin.Instance!.AudiobookPositionTracker = _positionTracker;

        PluginConfiguration configuration = Plugin.Instance!.Configuration;

        if (string.IsNullOrEmpty(configuration.ServerAddress))
        {
            _logger.LogWarning("No server address configured. Skills will not be created or updated.");
            return;
        }

        ManifestSkill manifestSkill;
        try
        {
            manifestSkill = new ManifestSkill("Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json", configuration.ServerAddress, configuration.SslCertType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load local skill manifest. Skills will not be created or updated.");
            return;
        }

        Plugin.Instance.ManifestSkill = manifestSkill;

        Uri endpointUri = new Uri(new Uri(configuration.ServerAddress), AlexaSkillController.ApiBaseUri);
        string endpointUriString = new Uri(endpointUri, "account-linking").ToString();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _runningTask = Task.Run(
            async () =>
        {
            foreach (User user in configuration.Users)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    // Restart recovery: reconstruct in-memory token from persisted refresh token
                    if (user.SmapiDeviceToken == null && !string.IsNullOrEmpty(user.SmapiRefreshToken))
                    {
                        _logger.LogInformation("Recovering SMAPI token for user {UserId} from persisted refresh token", user.Id);
                        try
                        {
                            DeviceToken? tokenResult = await LwaClient.RefreshDeviceToken(
                                new DeviceToken(user.SmapiRefreshToken, user.SmapiRefreshToken, "Bearer", 0),
                                configuration.LwaClientId,
                                configuration.LwaClientSecret).ConfigureAwait(false);

                            if (tokenResult != null)
                            {
                                user.SmapiDeviceToken = tokenResult;
                                user.SmapiRefreshToken = tokenResult.RefreshToken;
                                Plugin.Instance.SaveConfiguration();
                                _logger.LogInformation("SMAPI token recovered for user {UserId}", user.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to recover SMAPI token for user {UserId}. Re-authorization required.", user.Id);
                            user.SmapiRefreshToken = null;
                            if (user.UserSkill != null)
                            {
                                user.UserSkill.UserSkillStatus = UserSkillStatus.LwaAuthPending;
                            }

                            Plugin.Instance.SaveConfiguration();
                            continue;
                        }
                    }

                    // Fetch and persist SMAPI vendor ID if missing
                    if (string.IsNullOrEmpty(user.VendorId) && user.SmapiManagement != null)
                    {
                        try
                        {
                            user.VendorId = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetVendorIdAsync()).ConfigureAwait(false);
                            Plugin.Instance.SaveConfiguration();
                            _logger.LogInformation("Persisted vendor ID {VendorId} for user {UserId}", user.VendorId, user.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to fetch vendor ID for user {UserId}. Catalog sync will be unavailable.", user.Id);
                        }
                    }

                    if (user.UserSkill != null)
                    {
                        Collection<SkillInteractionModel> skillInteractionModels = Plugin.Instance.BuildSkillInteractionModels(user.UserSkill.InvocationName);

                        ValidateLocaleRestrictions(skillInteractionModels);

                        if (!string.IsNullOrEmpty(user.UserSkill.SkillId) && user.SmapiManagement != null)
                        {
                            ManifestSkill? cloudManifestSkill = null;
                            try
                            {
                                cloudManifestSkill = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetSkillAsync(user.UserSkill.SkillId!)).ConfigureAwait(false);
                            }
                            catch (Refit.ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                _logger.LogWarning("Skill {SkillId} no longer exists in the cloud for user {UserId}. Will recreate.", user.UserSkill.SkillId, user.Id);
                                cloudManifestSkill = null;
                            }

                            if (cloudManifestSkill != null)
                            {
                                string? cloudVersion = cloudManifestSkill.GetVersionTag();
                                _logger.LogInformation("Skill version (cloud) for user {UserId}: {Version}", user.Id, cloudVersion ?? "(no tag)");

                                AccountLinkData accountLinkingData = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetAccountLinkDataAsync(user.UserSkill.SkillId!)).ConfigureAwait(false);

                                SkillStatus status = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetSkillStatusAsync(user.UserSkill.SkillId!)).ConfigureAwait(false);

                                if (cloudVersion != Util.GetVersion()
                                    || status.Manifest.LastModified.Status == SkillStatusState.FAILED)
                                {
                                    _logger.LogInformation("Skill for user {UserId} is outdated. Updating...", user.Id);
                                    await AlexaUtil.CallAsync<object?>(user, async () =>
                                    {
                                        await user.SmapiManagement.UpdateSkillAsync(user.UserSkill.SkillId!, manifestSkill, skillInteractionModels).ConfigureAwait(false);
                                        return null;
                                    }).ConfigureAwait(false);

                                    await CaptureLocaleModelStatusesAsync(user, user.UserSkill.SkillId!).ConfigureAwait(false);
                                }

                                if (!accountLinkingData.AuthorizationUrl.Equals(endpointUriString, StringComparison.Ordinal)
                                    || !Plugin.Instance.Configuration.AccountLinkingClientId.Equals(accountLinkingData.ClientId, StringComparison.Ordinal))
                                {
                                    _logger.LogInformation("Account linking data for user {UserId} is outdated. Updating...", user.Id);
                                    await AlexaUtil.CallAsync<object?>(user, () =>
                                    {
                                        user.SmapiManagement.UpdateAccountLinkData(
                                            user.UserSkill.SkillId!,
                                            configuration.ServerAddress,
                                            configuration.AccountLinkingClientId);
                                        return Task.FromResult<object?>(null);
                                    }).ConfigureAwait(false);
                                }

                                if (user.TryTransitionToReady())
                                {
                                    _logger.LogInformation("Transitioning user {UserId} from AccountLinkPending to Ready (skill exists, token present)", user.Id);
                                    Plugin.Instance.SaveConfiguration();
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Skill {SkillId} not found in cloud for user {UserId}. Clearing stored skill ID to trigger recreation.", user.UserSkill.SkillId, user.Id);
                                user.UserSkill.SkillId = null;
                                user.UserSkill.UserSkillStatus = UserSkillStatus.SkillCreating;
                                Plugin.Instance.SaveConfiguration();
                            }
                        }

                        if (string.IsNullOrEmpty(user.UserSkill.SkillId) && user.SmapiManagement != null)
                        {
                            _logger.LogInformation("Skill for user {UserId} not in cloud. Creating...", user.Id);
                            string skillId = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.CreateSkillAsync(
                                manifestSkill,
                                skillInteractionModels,
                                configuration.ServerAddress,
                                configuration.AccountLinkingClientId)).ConfigureAwait(false);

                            user.UserSkill.SkillId = skillId;
                            user.UserSkill.UserSkillStatus = UserSkillStatus.AccountLinkPending;
                            Plugin.Instance.SaveConfiguration();

                            await CaptureLocaleModelStatusesAsync(user, skillId).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is Refit.ApiException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(
                        "SMAPI API error for user {UserId} during startup — skill sync deferred. Error: {Message}",
                        user.Id, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing skill for user {UserId}. Continuing with other users.", user.Id);
                }
            }
        },
        token);

        try
        {
            await _runningTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Skill startup task failed. Jellyfin will continue but skill management may be unavailable until the issue is resolved.");
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SkillStartup stopping...");

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_runningTask != null)
        {
            try
            {
                await Task.WhenAny(_runningTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("SkillStartup stop timed out or was cancelled");
            }
        }

        _logger.LogInformation("SkillStartup stopped");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Validates each locale's embedded interaction model against SMAPI restrictions.
    /// Logs warnings for any violations found, but does not block startup.
    /// </summary>
    private void ValidateLocaleRestrictions(Collection<SkillInteractionModel> models)
    {
        foreach (var sim in models)
        {
            string? modelJson = _modelDeploymentManager.GetDefaultModelJson(sim.Locale);
            if (modelJson == null)
            {
                continue;
            }

            var restrictionErrors = _modelDeploymentManager.ValidateSMAPIRestrictions(modelJson, sim.Locale);
            if (restrictionErrors.Count > 0)
            {
                _logger.LogWarning(
                    "SMAPI restriction violations for locale {Locale}: {Errors}",
                    sim.Locale, string.Join("; ", restrictionErrors));
            }
        }
    }

    /// <summary>
    /// Captures per-locale interaction model build status from SMAPI and stores it in configuration.
    /// </summary>
    private async Task CaptureLocaleModelStatusesAsync(Entities.User user, string skillId)
    {
        try
        {
            var status = await AlexaUtil.CallAsync(user, () => user.SmapiManagement!.GetSkillStatusAsync(skillId)).ConfigureAwait(false);

            var config = Plugin.Instance!.Configuration;
            var now = DateTime.UtcNow;

            foreach (var kvp in status.InteractionModel)
            {
                string locale = kvp.Key;
                var localeStatus = kvp.Value;
                string state = localeStatus.LastModified.Status.ToString();

                string? error = null;
                if (localeStatus.Errors is { Length: > 0 })
                {
                    error = string.Join("; ", localeStatus.Errors.Select(e => $"{e.Code}: {e.Message}"));
                    _logger.LogWarning(
                        "Interaction model build {Status} for locale {Locale}: {Error}",
                        state, locale, error);
                }

                config.SetLocaleModelStatus(locale, new Configuration.LocaleModelStatus
                {
                    Status = state,
                    LastUpdated = now,
                    Error = error,
                    Source = "Embedded",
                });
            }

            Plugin.Instance.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture per-locale model status for skill {SkillId}. Non-critical.", skillId);
        }
    }
}
