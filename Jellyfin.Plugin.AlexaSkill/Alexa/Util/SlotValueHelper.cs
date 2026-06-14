#nullable enable

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Utility methods for Alexa slot value constraints.
/// </summary>
public static class SlotValueHelper
{
    /// <summary>
    /// Alexa hard limit for slot value canonical name and synonym length.
    /// See: https://developer.amazon.com/en-US/docs/alexa/custom-skills/best-practices-for-skill-card-design.html
    /// SMAPI rejects values exceeding 140 characters with InvalidResponse.
    /// </summary>
    public const int MaxSlotValueLength = 140;

    /// <summary>
    /// Truncates a slot value to the Alexa maximum length, cutting at the last word boundary when possible.
    /// </summary>
    /// <param name="value">The slot value or synonym to truncate.</param>
    /// <returns>The value unchanged if within limit, or truncated to at most 140 characters.</returns>
    public static string Truncate(string value)
    {
        if (value.Length <= MaxSlotValueLength)
        {
            return value;
        }

        int cutAt = value.LastIndexOf(' ', MaxSlotValueLength - 1);
        return cutAt > 0 ? value[..cutAt] : value[..MaxSlotValueLength];
    }
}
