using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Management.InteractionModel;
using Alexa.NET.Management.Skills;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.InteractionModel;

/// <summary>
/// Represents the interaction model of the skill.
/// </summary>
public class SkillInteractionModel : SkillInteractionContainer
{
    private const string MoodSlotTypeName = "Mood";

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillInteractionModel"/> class
    /// from an embedded resource file.
    /// </summary>
    /// <param name="locale">Locale of this interaction model.</param>
    /// <param name="ressourcePath">Path to the manifest ressource.</param>
    /// <param name="invocationName">Invocation name of this interaction model.</param>
    public SkillInteractionModel(string locale, string ressourcePath, string invocationName)
    {
        InteractionModel = global::Jellyfin.Plugin.AlexaSkill.Util.DeserializeFromFile<SkillInteraction>(ressourcePath);
        Locale = locale;
        InvocationName = invocationName;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillInteractionModel"/> class
    /// from a pre-deserialized <see cref="SkillInteraction"/> object.
    /// </summary>
    /// <param name="locale">Locale of this interaction model.</param>
    /// <param name="interaction">The deserialized interaction model data.</param>
    public SkillInteractionModel(string locale, SkillInteraction interaction)
    {
        InteractionModel = interaction;
        Locale = locale;
    }

    /// <summary>
    /// Gets or sets the locale of this interaction model.
    /// </summary>
    [JsonIgnore]
    public string Locale { get; set; }

    /// <summary>
    /// Gets or sets the invocation name of this interaction model.
    /// Overwrites the template's invocation name loaded from the JSON resource file.
    /// </summary>
    [JsonIgnore]
    public string InvocationName
    {
        get
        {
            return InteractionModel.Language.InvocationName;
        }

        set
        {
            InteractionModel.Language.InvocationName = value;
        }
    }

    /// <summary>
    /// Appends admin-defined mood words to this model's Mood slot type so the NLU
    /// fills the slot one-shot for custom moods (e.g. "coding"). No-op when the
    /// model has no Mood slot type (locales without the custom Mood type) or when
    /// <paramref name="moodWords"/> is empty. Words already present (case-insensitive
    /// on the value) are not duplicated.
    /// </summary>
    /// <param name="moodWords">Custom mood words to inject, verbatim across locales.</param>
    public void InjectMoodSlotValues(IEnumerable<string> moodWords)
    {
        if (moodWords == null)
        {
            return;
        }

        List<string> words = moodWords
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .Select(w => w.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (words.Count == 0)
        {
            return;
        }

        // SlotTypes is an array (Alexa.NET.Management.InteractionModel.Language).
        SlotType[]? slotTypes = InteractionModel.Language.SlotTypes;
        if (slotTypes == null || slotTypes.Length == 0)
        {
            return; // this locale has no Mood slot type (pre-JF-356 locales)
        }

        SlotType? moodType = slotTypes.FirstOrDefault(t => t != null && string.Equals(t.Name, MoodSlotTypeName, StringComparison.Ordinal));
        if (moodType == null)
        {
            return;
        }

        // Append the non-duplicate words to the existing values in one pass.
        var existing = (moodType.Values ?? Enumerable.Empty<SlotTypeValue>())
            .Where(v => v?.Name?.Value != null)
            .Select(v => v.Name!.Value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var additions = words
            .Where(w => !existing.Contains(w))
            .Select(w => new SlotTypeValue { Name = new SlotTypeValueName { Value = w } });

        // Values is an array; rebuild it with the appended entries.
        moodType.Values = (moodType.Values ?? Enumerable.Empty<SlotTypeValue>())
            .Concat(additions)
            .ToArray();
    }
}