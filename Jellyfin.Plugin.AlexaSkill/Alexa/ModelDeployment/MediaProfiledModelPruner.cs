#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Management.InteractionModel;
using Alexa.NET.Management.Skills;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;

/// <summary>
/// The JF-960 prune step: when <see cref="Configuration.PluginConfiguration.MediaProfiledModels"/>
/// is on, drops from a locale model every intent whose media family census is zero,
/// BEFORE catalog injection touches the deployed model (the rebuild's PUT carries the
/// pruned model; the catalog GET-modify-PUT pass runs afterwards and preserves it).
/// Rules:
/// - only intents named in <see cref="IntentMediaFamilies.Table"/> are prunable; every
///   other intent (media-neutral, AMAZON built-ins, fallback) is NEVER touched;
/// - a pruned intent's <c>dialog.intents</c> entry is dropped too, so the model stays
///   self-consistent (a dangling dialog entry for a removed intent is dead weight);
/// - slot types are NEVER touched (types without intents are inert, the JF-952
///   declare-check philosophy);
/// - the caller logs per locale: the pruned intent names plus the family counts.
/// </summary>
public static class MediaProfiledModelPruner
{
    /// <summary>
    /// Prunes the interaction model against the media census counts.
    /// No-op (empty result, model untouched) when no tabled intent's family is absent.
    /// </summary>
    /// <param name="interaction">The locale's interaction model (mutated in place).</param>
    /// <param name="locale">The locale, for the log line.</param>
    /// <param name="familyCounts">The census: family name to presence count (0 = absent).</param>
    /// <param name="logger">Optional logger; the caller also surfaces the result.</param>
    /// <returns>The pruned intent names, empty when nothing was pruned.</returns>
    public static IReadOnlyList<string> Apply(
        SkillInteraction interaction,
        string locale,
        IReadOnlyDictionary<MediaFamily, int> familyCounts,
        ILogger? logger = null)
    {
        if (interaction?.Language?.IntentTypes is not { Length: > 0 } intents)
        {
            return Array.Empty<string>();
        }

        List<string> pruned = new();
        foreach (IntentType intent in intents)
        {
            MediaFamily? family = IntentMediaFamilies.FamilyOf(intent.Name);
            if (family.HasValue && familyCounts.TryGetValue(family.Value, out int count) && count == 0)
            {
                pruned.Add(intent.Name);
            }
        }

        if (pruned.Count == 0)
        {
            return pruned;
        }

        HashSet<string> prunedNames = new(pruned, StringComparer.Ordinal);

        // languageModel.intents: rebuild the array without the pruned entries.
        interaction.Language.IntentTypes =
            intents.Where(intent => !prunedNames.Contains(intent.Name)).ToArray();

        // dialog.intents: drop the entries of pruned intents so the model stays
        // self-consistent (a dialog entry for a removed intent is dead weight).
        Dialog? dialog = interaction.Dialog;
        if (dialog?.Intents is { Length: > 0 } dialogIntents)
        {
            DialogIntent[] remaining = dialogIntents
                .Where(dialogIntent => !prunedNames.Contains(dialogIntent.Name))
                .ToArray();
            if (remaining.Length != dialogIntents.Length)
            {
                dialog.Intents = remaining;
            }
        }

        string census = string.Join(
            ", ",
            familyCounts.OrderBy(pair => pair.Key)
                .Select(pair => $"{IntentMediaFamilies.DisplayName(pair.Key)}={pair.Value}"));

        logger?.LogInformation(
            "MediaProfiledModels[{Locale}]: pruned {Count} intent(s): {Intents}; family census: {Census} (JF-960)",
            locale,
            pruned.Count,
            string.Join(", ", pruned),
            census);

        return pruned;
    }
}
