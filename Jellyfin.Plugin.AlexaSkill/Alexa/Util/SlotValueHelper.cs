#nullable enable

using System;
using System.Collections.Generic;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;

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
    /// form ('ジャズ') to its canonical value ('Jazz', JF-642's ja-JP GenreType;
    /// 'クイーン' to the library artist name, JF-659's JellyfinArtist slots);
    /// free-text types and unmatched values return null so the caller keeps the
    /// raw slot value. Pairing rule (the JF-642 genre / JF-659 musician contract,
    /// the ONE owner of it): the canonical feeds the SEARCH VERBATIM regardless
    /// of script (a kana-named library's canonical IS kana and exact-self-matches
    /// its own name; romanizing it would destroy that hit) while the raw value
    /// keeps driving speech and session (never reassign the raw local).
    /// </summary>
    /// <param name="slot">The slot to read entity resolution from; null (an absent slot) reads as no match.</param>
    /// <returns>The canonical slot value, or null when no authority matched.</returns>
    public static string? GetCanonicalValue(Slot? slot)
    {
        if (slot?.Resolution?.Authorities is { Length: > 0 } authorities)
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

    /// <summary>
    /// Canonical value of a named slot on an intent request (JF-659): the same
    /// ER_SUCCESS_MATCH read as the slot overload, for call sites that hold the
    /// request rather than the slot object (GetSlotValue callers and indexer
    /// reads). Null when the slot is absent or unmatched, so the caller keeps
    /// the raw value.
    /// </summary>
    /// <param name="request">The intent request carrying the slots.</param>
    /// <param name="slotName">The slot name.</param>
    /// <returns>The canonical slot value, or null when no authority matched.</returns>
    public static string? GetCanonicalValue(IntentRequest request, string slotName)
        => request.Intent.Slots != null && request.Intent.Slots.TryGetValue(slotName, out Slot? slot)
            ? GetCanonicalValue(slot)
            : null;

    /// <summary>
    /// ALL distinct canonical names of the first ER_SUCCESS_MATCH authority on
    /// the slot (JF-690), de-duplicated by trimmed case-insensitive name (the
    /// ER values array lists distinct catalog values ordered by Amazon's
    /// likelihood rank; the de-dup is defensive against a pathological
    /// same-name catalog pair). Empty when no authority matched, exactly like a
    /// null <see cref="GetCanonicalValue(Slot)"/>; a single-entry list is the
    /// ordinary shape, and its first entry is byte-identical to
    /// <see cref="GetCanonicalValue(Slot)"/> (the raw name, never re-trimmed).
    /// Callers arbitrating a multi-entry list must resolve the names against
    /// the library themselves (a canonical name is catalog evidence, not a
    /// library item id).
    /// </summary>
    /// <param name="slot">The slot to read entity resolution from; null (an absent slot) reads as no match.</param>
    /// <returns>The distinct canonical names in authority order, or an empty list when no authority matched.</returns>
    public static IReadOnlyList<string> GetCanonicalValues(Slot? slot)
    {
        if (slot?.Resolution?.Authorities is { Length: > 0 } authorities)
        {
            foreach (var authority in authorities)
            {
                if (authority.Status?.Code == "ER_SUCCESS_MATCH" && authority.Values is { Length: > 0 })
                {
                    List<string> names = new();
                    HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var value in authority.Values)
                    {
                        if (value.Value?.Name is string name && !string.IsNullOrWhiteSpace(name) && seen.Add(name.Trim()))
                        {
                            names.Add(name);
                        }
                    }

                    if (names.Count > 0)
                    {
                        return names;
                    }
                }
            }
        }

        return Array.Empty<string>();
    }

    /// <summary>
    /// All distinct canonical names of a named slot on an intent request
    /// (JF-690): the same multi-value read as the slot overload, for call
    /// sites that hold the request rather than the slot object. Empty when the
    /// slot is absent or unmatched.
    /// </summary>
    /// <param name="request">The intent request carrying the slots.</param>
    /// <param name="slotName">The slot name.</param>
    /// <returns>The distinct canonical names in authority order, or an empty list when no authority matched.</returns>
    public static IReadOnlyList<string> GetCanonicalValues(IntentRequest request, string slotName)
        => request.Intent.Slots != null && request.Intent.Slots.TryGetValue(slotName, out Slot? slot)
            ? GetCanonicalValues(slot)
            : Array.Empty<string>();
}
