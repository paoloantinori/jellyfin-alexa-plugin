using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Management.AccountLinking;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.EntryPoints;

/// <summary>
/// Setup the skill and update or create the skill in the Alexa cloud if it is outdated.
/// </summary>
public class SkillStartup : IHostedService, IDisposable
{
    private readonly ILogger<SkillStartup> _logger;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SearchResultCache _searchCache;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly RequestCounters _requestCounters;
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
    public SkillStartup(ISessionManager sessionManager, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, SearchResultCache searchCache, CircuitBreaker circuitBreaker, RequestCounters requestCounters)
    {
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _searchCache = searchCache;
        _circuitBreaker = circuitBreaker;
        _requestCounters = requestCounters;
        _logger = loggerFactory.CreateLogger<SkillStartup>();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Skill version (local): v{Version}", Util.GetVersion());

        Plugin.Instance!._httpClientFactory = _httpClientFactory;
        Plugin.Instance!.SearchCache = _searchCache;
        Plugin.Instance!.CircuitBreaker = _circuitBreaker;
        Plugin.Instance!.RequestCounters = _requestCounters;

        PluginConfiguration configuration = Plugin.Instance!.Configuration;

        if (string.IsNullOrEmpty(configuration.ServerAddress))
        {
            _logger.LogWarning("No server address configured. Skills will not be created or updated.");
            return;
        }

        ManifestSkill manifestSkill = new ManifestSkill("Jellyfin.Plugin.AlexaSkill.Alexa.Manifest.manifest.json", configuration.ServerAddress, configuration.SslCertType);
        Plugin.Instance.ManifestSkill = manifestSkill;

        Uri endpointUri = new Uri(new Uri(configuration.ServerAddress), AlexaSkillController.ApiBaseUri);
        string endpointUriString = new Uri(endpointUri, "account-linking").ToString();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        _runningTask = Task.Run(async () =>
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

                    if (user.UserSkill != null)
                    {
                        Collection<SkillInteractionModel> skillInteractionModels = Plugin.Instance.BuildSkillInteractionModels(user.UserSkill.InvocationName);

                        if (user.UserSkill.SkillId != null && user.SmapiManagement != null)
                        {
                            ManifestSkill cloudManifestSkill = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetSkillAsync(user.UserSkill.SkillId)).ConfigureAwait(false);
                            _logger.LogInformation("Skill version (cloud) for user {UserId}: {Version}", user.Id, cloudManifestSkill.GetVersionTag());

                            AccountLinkData accountLinkingData = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetAccountLinkDataAsync(user.UserSkill.SkillId)).ConfigureAwait(false);

                            SkillStatus status = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.GetSkillStatusAsync(user.UserSkill.SkillId)).ConfigureAwait(false);

                            if (cloudManifestSkill.GetVersionTag() != Util.GetVersion()
                                || status.Manifest.LastModified.Status == SkillStatusState.FAILED)
                            {
                                _logger.LogInformation("Skill for user {UserId} is outdated. Updating...", user.Id);
                                await AlexaUtil.CallAsync<object?>(user, () =>
                                {
                                    user.SmapiManagement.UpdateSkillAsync(user.UserSkill.SkillId, manifestSkill, skillInteractionModels);
                                    return Task.FromResult<object?>(null);
                                }).ConfigureAwait(false);
                            }

                            if (!accountLinkingData.AuthorizationUrl.Equals(endpointUriString, StringComparison.Ordinal)
                                || !Plugin.Instance.Configuration.AccountLinkingClientId.Equals(accountLinkingData.ClientId, StringComparison.Ordinal))
                            {
                                _logger.LogInformation("Account linking data for user {UserId} is outdated. Updating...", user.Id);
                                await AlexaUtil.CallAsync<object?>(user, () =>
                                {
                                    user.SmapiManagement.UpdateAccountLinkData(
                                        user.UserSkill.SkillId,
                                        configuration.ServerAddress,
                                        configuration.AccountLinkingClientId);
                                    return Task.FromResult<object?>(null);
                                }).ConfigureAwait(false);
                            }
                        }
                        else if (user.SmapiManagement != null)
                        {
                            user.UserSkill.UserSkillStatus = UserSkillStatus.SkillCreating;

                            _logger.LogInformation("Skill for user {UserId} not in cloud. Creating...", user.Id);
                            string skillId = await AlexaUtil.CallAsync(user, () => user.SmapiManagement.CreateSkillAsync(
                                manifestSkill,
                                skillInteractionModels,
                                configuration.ServerAddress,
                                configuration.AccountLinkingClientId)).ConfigureAwait(false);

                            user.UserSkill.SkillId = skillId;
                            user.UserSkill.UserSkillStatus = UserSkillStatus.AccountLinkPending;
                            Plugin.Instance.SaveConfiguration();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing skill for user {UserId}. Continuing with other users.", user.Id);
                }
            }
        }, token);

        await _runningTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SkillStartup stopping...");

        if (_cts != null)
        {
            _cts.Cancel();
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
}
