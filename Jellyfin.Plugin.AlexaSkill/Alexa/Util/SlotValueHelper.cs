#nullable enable

using Alexa.NET.Request;

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

    /// <summary>
    /// Canonical value of the first ER_SUCCESS_MATCH authority on the slot, or
    /// null when no authority matched. A custom slot type resolves the spoken
    /// form ('ジャズ') to its canonical value ('Jazz', JF-642's ja-JP GenreType);
    /// free-text types and unmatched values return null so the caller keeps the
    /// raw slot value.
    /// </summary>
    /// <param name="slot">The slot to read entity resolution from.</param>
    /// <returns>The canonical slot value, or null when no authority matched.</returns>
    public static string? GetCanonicalValue(Slot slot)
    {
        if (slot.Resolution?.Authorities is { Length: > 0 } authorities)
        {
            foreach (var authority in authorities)
            {
                if (authority.Status?.Code == "ER_SUCCESS_MATCH"
                    && authority.Values is { Length: > 0 }
                    && authority.Values[0].Value?.Name is string canonical
                    && !string.IsNullOrWhiteSpace(canonical))
                {
                    return canonical;
                }
            }
        }

        return null;
    }
}
