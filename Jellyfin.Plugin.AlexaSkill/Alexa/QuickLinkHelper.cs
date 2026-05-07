using System;
using System.Web;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Generates Alexa Quick Link URLs for the Jellyfin skill.
/// Quick Links allow users to launch the skill or a specific task via a clickable URL.
/// </summary>
public static class QuickLinkHelper
{
    private const string QuickLinkBaseUrl = "https://alexa.amazon.com/spa/index.html#skill/";

    /// <summary>
    /// Generate a basic Quick Link URL that opens the skill.
    /// </summary>
    /// <param name="skillId">The Alexa skill ID (e.g. "amzn1.ask.skill.xxx").</param>
    /// <returns>A clickable URL that launches the skill.</returns>
    public static string GetSkillUrl(string skillId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        return $"{QuickLinkBaseUrl}{Uri.EscapeDataString(skillId)}";
    }

    /// <summary>
    /// Generate a Quick Link URL that deep-links to a specific task.
    /// </summary>
    /// <param name="skillId">The Alexa skill ID.</param>
    /// <param name="taskName">The task name (must match a task declared in the manifest).</param>
    /// <param name="taskVersion">The task version (default "1").</param>
    /// <returns>A clickable URL that launches the skill with the specified task.</returns>
    public static string GetTaskUrl(string skillId, string taskName, string taskVersion = "1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        return $"{QuickLinkBaseUrl}{Uri.EscapeDataString(skillId)}?task={Uri.EscapeDataString(taskName)}&version={Uri.EscapeDataString(taskVersion)}";
    }

    /// <summary>
    /// Generate a Quick Link URL for the PlayFavorites task.
    /// </summary>
    /// <param name="skillId">The Alexa skill ID.</param>
    /// <returns>A clickable URL that triggers playing favorites.</returns>
    public static string GetPlayFavoritesUrl(string skillId)
    {
        return GetTaskUrl(skillId, "PlayFavorites");
    }

    /// <summary>
    /// Generate a Quick Link URL for the PlayMedia task.
    /// </summary>
    /// <param name="skillId">The Alexa skill ID.</param>
    /// <returns>A clickable URL that triggers the PlayMedia task.</returns>
    public static string GetPlayMediaUrl(string skillId)
    {
        return GetTaskUrl(skillId, "PlayMedia");
    }
}
