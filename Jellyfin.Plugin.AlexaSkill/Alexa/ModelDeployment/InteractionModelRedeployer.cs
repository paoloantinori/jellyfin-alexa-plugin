#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;

/// <summary>
/// Redeploys the embedded interaction models for a user's existing skill to Amazon via SMAPI,
/// applying the supplied invocation name to every locale model. Default implementation of
/// <see cref="IInteractionModelRedeployer"/>.
/// </summary>
public class InteractionModelRedeployer : IInteractionModelRedeployer
{
    private const int MaxStatusPolls = 60;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<InteractionModelRedeployer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InteractionModelRedeployer"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public InteractionModelRedeployer(ILogger<InteractionModelRedeployer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ModelRedeployResult> RedeployAsync(User user, string invocationName, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.ManifestSkill == null)
        {
            throw new InvalidOperationException("Plugin manifest not loaded. Restart Jellyfin first.");
        }

        string? skillId = user.UserSkill?.SkillId;
        if (string.IsNullOrEmpty(skillId))
        {
            throw new ArgumentException("User has no skill ID. Complete skill creation first.", nameof(user));
        }

        var interactionModels = Plugin.Instance.BuildSkillInteractionModels(invocationName);

        var updateFailures = await AlexaUtil.CallAsync(
            user,
            () => user.SmapiManagement!.UpdateSkillAsync(skillId, Plugin.Instance!.ManifestSkill!, interactionModels))
            .ConfigureAwait(false);

        var localeResults = await PollLocaleBuildStatusAsync(user, skillId, cancellationToken).ConfigureAwait(false);

        int succeeded = localeResults.Count(r => r.Value.Success);
        bool success = localeResults.All(r => r.Value.Success) && updateFailures.Count == 0;
        string status = success ? "rebuilt" : "rebuilt_with_errors";

        _logger.LogInformation(
            "Redeployed {LocaleCount} interaction models for skill {SkillId}: {Succeeded} succeeded, {Failed} failed ({UpdateFailures} rejected at update)",
            interactionModels.Count,
            skillId,
            succeeded,
            localeResults.Count - succeeded,
            updateFailures.Count);

        return new ModelRedeployResult(
            success,
            status,
            interactionModels.Count,
            succeeded,
            updateFailures,
            localeResults);
    }

    /// <summary>
    /// Polls SMAPI until all locale model builds leave IN_PROGRESS.
    /// Returns per-locale success/failure status.
    /// </summary>
    /// <param name="user">The user whose skill status to poll.</param>
    /// <param name="skillId">The skill ID to poll.</param>
    /// <param name="cancellationToken">Token to abort polling.</param>
    /// <returns>A map of locale to its build result.</returns>
    private async Task<Dictionary<string, ModelLocaleBuildResult>> PollLocaleBuildStatusAsync(
        User user, string skillId, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, ModelLocaleBuildResult>(StringComparer.Ordinal);

        for (int i = 0; i < MaxStatusPolls; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await AlexaUtil.CallAsync(
                user,
                () => user.SmapiManagement!.GetSkillStatusAsync(skillId)).ConfigureAwait(false);

            bool allDone = true;
            foreach (var kvp in status.InteractionModel)
            {
                string locale = kvp.Key;
                var localeStatus = kvp.Value;
                string state = localeStatus.LastModified.Status.ToString();

                if (state == "IN_PROGRESS")
                {
                    allDone = false;
                    continue;
                }

                if (!results.ContainsKey(locale))
                {
                    string? error = localeStatus.Errors is { Length: > 0 }
                        ? string.Join("; ", localeStatus.Errors.Select(e => $"{e.Code}: {e.Message}"))
                        : null;

                    results[locale] = new ModelLocaleBuildResult(state == "SUCCEEDED", state, error);
                }
            }

            if (allDone && results.Count >= status.InteractionModel.Count)
            {
                break;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }
}
