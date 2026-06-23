#nullable enable

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;

/// <summary>
/// Redeploys the embedded interaction models for a user's existing skill to Amazon via SMAPI,
/// applying the supplied invocation name to every locale model.
/// </summary>
/// <remarks>
/// Used when a setting that affects the published skill changes (for example the invocation
/// name) so the change reaches Amazon without requiring a full skill re-authorization. See JF-297.
/// </remarks>
public interface IInteractionModelRedeployer
{
    /// <summary>
    /// Builds the embedded interaction models with <paramref name="invocationName"/> and pushes
    /// them to the user's existing skill via SMAPI, polling until every locale build settles.
    /// </summary>
    /// <param name="user">The user whose skill to update. Must have a non-empty <see cref="UserSkill.SkillId"/> and a SMAPI device token.</param>
    /// <param name="invocationName">The invocation name to apply to every locale model.</param>
    /// <param name="cancellationToken">Token to cancel the build-status poll.</param>
    /// <returns>The redeploy outcome, including per-locale build results.</returns>
    Task<ModelRedeployResult> RedeployAsync(User user, string invocationName, CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of an interaction-model redeploy.
/// </summary>
/// <param name="Success"><see langword="true"/> only when every locale built and no locale update was rejected.</param>
/// <param name="Status">Short status string for persistence in <c>LastModelDeployStatus</c> (e.g. "rebuilt", "rebuilt_with_errors").</param>
/// <param name="LocaleCount">Number of locale models processed.</param>
/// <param name="SucceededCount">Number of locale builds that succeeded.</param>
/// <param name="UpdateFailures">Locales rejected at the <c>UpdateSkillAsync</c> step, mapped to their error message.</param>
/// <param name="Locales">Per-locale build status once builds settle.</param>
public sealed record ModelRedeployResult(
    bool Success,
    string Status,
    int LocaleCount,
    int SucceededCount,
    IReadOnlyDictionary<string, string> UpdateFailures,
    IReadOnlyDictionary<string, ModelLocaleBuildResult> Locales);

/// <summary>
/// Build status for a single locale after a redeploy.
/// </summary>
/// <param name="Success">Whether the locale build succeeded.</param>
/// <param name="Status">Raw SMAPI build status (e.g. "SUCCEEDED", "FAILED").</param>
/// <param name="Error">Error detail, if any.</param>
public sealed record ModelLocaleBuildResult(bool Success, string Status, string? Error);
