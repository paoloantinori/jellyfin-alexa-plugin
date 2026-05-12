namespace Jellyfin.Plugin.AlexaSkill.Configuration;

/// <summary>
/// Controls how the skill handles fuzzy matches that are close but below the confidence threshold.
/// </summary>
public enum FuzzyMatchBehavior
{
    /// <summary>
    /// Ask the user to confirm the suggestion: "Did you mean X?"
    /// </summary>
    Confirm = 0,

    /// <summary>
    /// Auto-play the closest match with an announcement.
    /// </summary>
    AutoPlay = 1
}
