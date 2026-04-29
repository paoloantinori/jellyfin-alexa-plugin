using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Management.AccountLinking;
using Alexa.NET.Management.Skills;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;
using Jellyfin.Plugin.AlexaSkill.Alexa.Manifest;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Controller;
using Jellyfin.Plugin.AlexaSkill.Entities;
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
    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillStartup"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="loggerFactory">Logger.</param>
    public SkillStartup(ISessionManager sessionManager, ILoggerFactory loggerFactory)
    {
        _sessionManager = sessionManager;
        _logger = loggerFactory.CreateLogger<SkillStartup>();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Skill version (local): v{Version}", Util.GetVersion());

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

        _runningTask = Task.Run(() =>
        {
            foreach (User user in configuration.Users)
            {
                token.ThrowIfCancellationRequested();

                if (user.UserSkill != null)
                {
                    Collection<SkillInteractionModel> skillInteractionModels = new Collection<SkillInteractionModel>();
                    foreach (Tuple<string, string> model in Plugin.Instance.InteractionModels)
                    {
                        skillInteractionModels.Add(new SkillInteractionModel(model.Item1, model.Item2, user.UserSkill.InvocationName));
                    }

                    // check if the skill is created in the cloud
                    if (user.UserSkill.SkillId != null && user.SmapiManagement != null)
                    {
                        ManifestSkill cloudManifestSkill = AlexaUtil.Call(user, () => user.SmapiManagement.GetSkill(user.UserSkill.SkillId));
                        _logger.LogInformation("Skill version (cloud) for user {UserId}: {Version}", user.Id, cloudManifestSkill.GetVersionTag());

                        AccountLinkData accountLinkingData = AlexaUtil.Call(user, () => user.SmapiManagement.GetAccountLinkData(user.UserSkill.SkillId));

                        SkillStatus status = AlexaUtil.Call(user, () => user.SmapiManagement.GetSkillStatus(user.UserSkill.SkillId));

                        // check if the skill is diverged from the local model
                        if (cloudManifestSkill.GetVersionTag() != Util.GetVersion()
                            || status.Manifest.LastModified.Status == SkillStatusState.FAILED)
                        {
                            _logger.LogInformation("Skill for user {UserId} is outdated. Updating...", user.Id);
                            AlexaUtil.Call<object?>(user, () =>
                            {
                                user.SmapiManagement.UpdateSkill(user.UserSkill.SkillId, manifestSkill, skillInteractionModels);
                                return null;
                            });
                        }

                        if (!accountLinkingData.AuthorizationUrl.Equals(endpointUriString, StringComparison.Ordinal)
                            || !Plugin.Instance.Configuration.AccountLinkingClientId.Equals(accountLinkingData.ClientId, StringComparison.Ordinal))
                        {
                            _logger.LogInformation("Account linking data for user {UserId} is outdated. Updating...", user.Id);
                            AlexaUtil.Call<object?>(user, () =>
                            {
                                user.SmapiManagement.UpdateAccountLinkData(
                                    user.UserSkill.SkillId,
                                    configuration.ServerAddress,
                                    configuration.AccountLinkingClientId);
                                return null;
                            });
                        }
                    }
                    else if (user.SmapiManagement != null)
                    {
                        user.UserSkill.UserSkillStatus = UserSkillStatus.SkillCreating;

                        _logger.LogInformation("Skill for user {UserId} not in cloud. Creating...", user.Id);
                        string skillId = AlexaUtil.Call(user, () => user.SmapiManagement.CreateSkill(
                            manifestSkill,
                            skillInteractionModels,
                            configuration.ServerAddress,
                            configuration.AccountLinkingClientId));

                        user.UserSkill.SkillId = skillId;
                        user.UserSkill.UserSkillStatus = UserSkillStatus.AccountLinkPending;
                        Plugin.Instance.SaveConfiguration();
                    }
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
