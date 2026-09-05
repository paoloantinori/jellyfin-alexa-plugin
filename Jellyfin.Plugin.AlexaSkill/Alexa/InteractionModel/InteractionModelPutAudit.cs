#nullable enable

using System;
using System.Text.Json;
using Alexa.NET.Management.Skills;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;

/// <summary>
/// Single audit point for every interaction-model PUT the plugin submits to SMAPI
/// (JF-495). Before the 2026-09-05 incident the deploy paths logged with wording
/// specific to each site, so a stale model PUT could land with no greppable trace:
/// the incident window had zero log lines naming a model submission. Every PUT site
/// (embedded redeploy, custom URL deploy, embedded restore, catalog sync
/// GET-modify-PUT) must call <see cref="LogModelPut"/> immediately before
/// submitting, so one grep for "MODEL PUT" finds every submission in the logs.
/// The canary helpers report, after the build settles, whether the live model
/// still carries the intent and sample counts that were submitted.
/// </summary>
public static class InteractionModelPutAudit
{
    /// <summary>Source label: models built from the DLL-embedded resources.</summary>
    public const string SourceEmbedded = "Embedded";

    /// <summary>Source label: model JSON fetched from a user-provided URL.</summary>
    public const string SourceCustomUrl = "CustomUrl";

    /// <summary>Source label: embedded model pushed by the restore path.</summary>
    public const string SourceRestore = "Restore";

    /// <summary>Source label: live model fetched, catalog refs injected, pushed back (catalog sync).</summary>
    public const string SourceGetModifyPut = "GetModifyPut";

    /// <summary>
    /// Logs the mandatory pre-PUT audit line. Call exactly once per locale PUT,
    /// before the first submit attempt (not inside retry loops).
    /// </summary>
    /// <param name="logger">The logging sink of the PUT site.</param>
    /// <param name="source">One of the <c>Source*</c> constants (or a caller-specific label).</param>
    /// <param name="locale">The locale being deployed.</param>
    /// <param name="skillId">The skill whose model is being replaced.</param>
    /// <param name="intentCount">Intent count of the payload being PUT.</param>
    /// <param name="sampleCount">Total sample count of the payload being PUT.</param>
    public static void LogModelPut(ILogger logger, string source, string locale, string skillId, int intentCount, int sampleCount)
    {
        logger.LogInformation(
            "MODEL PUT submitting interaction model: source={Source} locale={Locale} skill={SkillId} intents={Intents} samples={Samples}",
            source, locale, skillId, intentCount, sampleCount);
    }

    /// <summary>
    /// Logs a passing post-deploy canary: the live model carries the submitted counts.
    /// </summary>
    public static void LogCanaryOk(ILogger logger, string source, string locale, int intentCount, int sampleCount)
    {
        logger.LogInformation(
            "MODEL PUT canary OK: source={Source} locale={Locale} live model matches submission (intents={Intents} samples={Samples})",
            source, locale, intentCount, sampleCount);
    }

    /// <summary>
    /// Logs a FAILING post-deploy canary: the live model does not carry the submitted
    /// counts. This is the JF-495 regression signature (a stale deploy clobbering the
    /// live model, or a queued build landing late). Log-only by design: no rollback.
    /// </summary>
    public static void LogCanaryMismatch(
        ILogger logger,
        string source,
        string locale,
        string skillId,
        int putIntents,
        int putSamples,
        int liveIntents,
        int liveSamples)
    {
        logger.LogError(
            "MODEL PUT canary MISMATCH: source={Source} locale={Locale} skill={SkillId} submitted intents={PutIntents} samples={PutSamples} but live model reports intents={LiveIntents} samples={LiveSamples}; the live model does not match the submission, a stale or racing deploy may have replaced it (JF-495)",
            source, locale, skillId, putIntents, putSamples, liveIntents, liveSamples);
    }

    /// <summary>
    /// Counts intents and total samples in a raw interaction-model JSON string
    /// (wrapped or unwrapped in the "interactionModel" envelope). Returns (0, 0)
    /// for unparseable input or a missing intents array rather than throwing.
    /// </summary>
    /// <param name="modelJson">The raw model JSON.</param>
    /// <returns>Intent count and total sample count.</returns>
    public static (int IntentCount, int SampleCount) CountFromJson(string modelJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(modelJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (0, 0);
            }

            if (root.TryGetProperty("interactionModel", out var im) && im.ValueKind == JsonValueKind.Object)
            {
                root = im;
            }

            if (!root.TryGetProperty("languageModel", out var lm)
                || lm.ValueKind != JsonValueKind.Object
                || !lm.TryGetProperty("intents", out var intents)
                || intents.ValueKind != JsonValueKind.Array)
            {
                return (0, 0);
            }

            int samples = 0;
            foreach (JsonElement intent in intents.EnumerateArray())
            {
                if (intent.ValueKind == JsonValueKind.Object
                    && intent.TryGetProperty("samples", out var samplesEl)
                    && samplesEl.ValueKind == JsonValueKind.Array)
                {
                    samples += samplesEl.GetArrayLength();
                }
            }

            return (intents.GetArrayLength(), samples);
        }
        catch (JsonException)
        {
            return (0, 0);
        }
        catch (InvalidOperationException)
        {
            // GetValue on a non-string node; treat as absent.
            return (0, 0);
        }
    }

    /// <summary>
    /// Counts intents and total samples in a deserialized model container
    /// (the plugin's <see cref="SkillInteractionModel"/> and the SMAPI GET result
    /// both derive from <see cref="SkillInteractionContainer"/>).
    /// </summary>
    /// <param name="model">The model container, or null.</param>
    /// <returns>Intent count and total sample count.</returns>
    public static (int IntentCount, int SampleCount) Count(SkillInteractionContainer? model)
    {
        var intents = model?.InteractionModel?.Language?.IntentTypes;
        if (intents == null || intents.Length == 0)
        {
            return (0, 0);
        }

        int samples = 0;
        foreach (var intent in intents)
        {
            samples += intent?.Samples?.Length ?? 0;
        }

        return (intents.Length, samples);
    }
}
