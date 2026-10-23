#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Characters Amazon REJECTS inside slot values and synonyms. The whole
    /// response fails with InvalidResponse "Invalid Slot Value" when one is
    /// present (issue #41, live: every LaunchRequest on a library carrying the
    /// MusicBrainz name '*NSYNC'). The set is EVIDENCE-BOUND, deliberately
    /// minimal: '*' is the only proven rejection; the punctuation proven to
    /// PASS (same library, same first-70 push: '!', '"', '&amp;', '(', ')', '.',
    /// accented letters) is left untouched so names like '(həd) p.e.' keep
    /// matching as they do today. Amazon documents no allowed-character list
    /// for slot values (checked: the slot-type page, the dialog reference and
    /// the model schema carry none), so new members join ONLY on a live
    /// rejection report, never by speculation; over-stripping is cosmetic but
    /// under-stripping bricks the skill for the affected library.
    /// </summary>
    private static readonly char[] RejectedSlotValueChars = { '*' };

    /// <summary>
    /// Truncates a slot value to the Alexa maximum length, cutting at the last
    /// word boundary when possible, AFTER stripping the characters Amazon
    /// rejects (issue #41): the ONE slot-value text preparation every catalog
    /// and dynamic-entity construction site already calls, so the sanitize and
    /// the length cap cannot drift apart. May return an EMPTY string when the
    /// value consisted only of rejected characters; the construction sites
    /// skip such entries.
    /// </summary>
    /// <param name="value">The slot value or synonym to prepare.</param>
    /// <returns>The sanitized value, truncated to at most 140 characters.</returns>
    public static string Truncate(string value)
    {
        string sanitized = value.IndexOfAny(RejectedSlotValueChars) < 0
            ? value
            : string.Join(' ', value
                .Split(RejectedSlotValueChars)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));

        if (sanitized.Length <= MaxSlotValueLength)
        {
            return sanitized;
        }

        int cutAt = sanitized.LastIndexOf(' ', MaxSlotValueLength - 1);
        return cutAt > 0 ? sanitized[..cutAt] : sanitized[..MaxSlotValueLength];
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
    /// JF-882: the ONE whole-span-escape predicate of the song-carrier guard
    /// family (JF-876 code-review F1): true when the slot is ER-resolved AND
    /// the raw fill IS the canonical title verbatim (case-insensitive), i.e.
    /// the leading song-carrier noun is part of the TITLE itself («the song of
    /// bernadette» names the movie «The Song of Bernadette»), not a misroute
    /// carrier. A true misroute's fill carries the EXTRA carrier noun and
    /// differs from the canonical («il brano sunny dancer» vs «Sunny Dancer»).
    /// Pins the null-guard + case-insensitive pairing once for every guard
    /// site; an unresolved slot (null canonical) never escapes. Both sides are
    /// TRIMMED before the compare (code-review round 2): detection trims the
    /// fill, so a whole-span title carrying leading/trailing whitespace (the
    /// documented whitespace-fill shape) must escape too, not delegate.
    /// </summary>
    /// <param name="rawSlotValue">The RAW slot fill (pre-ER).</param>
    /// <param name="canonicalValue">The canonical the caller read for the same slot.</param>
    /// <returns>True when the fill IS the canonical title verbatim.</returns>
    public static bool IsWholeSpanCanonicalFill(string? rawSlotValue, string? canonicalValue)
        => canonicalValue != null
           && rawSlotValue != null
           && string.Equals(rawSlotValue.Trim(), canonicalValue.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The raw spoken value of a named slot on an intent request (trimmed), or
    /// null when the slot is absent or whitespace (Alexa delivers
    /// partially-matched slots as empty or whitespace strings;
    /// IsNullOrWhiteSpace, not IsNullOrEmpty - CLAUDE.md anti-pattern #7).
    /// JF-594's one-home semantics for slot extraction, exposed at the
    /// request level beside the canonical reads so static non-handler
    /// consumers (the JF-885 ER gate) share the ONE definition with
    /// <see cref="Handler.BaseHandler.GetSlotValue"/> (which delegates here).
    /// </summary>
    /// <param name="request">The intent request carrying the slots.</param>
    /// <param name="slotName">The slot name.</param>
    /// <returns>The trimmed slot value, or null.</returns>
    public static string? GetSlotValue(IntentRequest request, string slotName)
        => request.Intent.Slots != null && request.Intent.Slots.TryGetValue(slotName, out Slot? slot)
            ? (string.IsNullOrWhiteSpace(slot.Value) ? null : slot.Value.Trim())
            : null;

    /// <summary>
    /// The full resolved entry (id AND canonical name) of the first
    /// ER_SUCCESS_MATCH authority on the slot (JF-869), or null when no
    /// authority matched. The id is the shared cross-locale key space the
    /// restricted custom types carry (the EpisodePosition/SeekVocabulary
    /// consuming pattern); the name is the localized canonical
    /// <see cref="GetCanonicalValue(Slot)"/> returns. The ONE owner of the
    /// authority walk for id-reading consumers.
    /// </summary>
    /// <param name="slot">The slot to read entity resolution from; null (an absent slot) reads as no match.</param>
    /// <returns>The resolved value entry, or null when no authority matched.</returns>
    public static ResolutionValue? GetResolvedValue(Slot? slot)
    {
        if (slot?.Resolution?.Authorities is { Length: > 0 } authorities)
        {
            foreach (var authority in authorities)
            {
                if (authority.Status?.Code == "ER_SUCCESS_MATCH"
                    && authority.Values is { Length: > 0 }
                    && authority.Values[0].Value is { } resolved)
                {
                    return resolved;
                }
            }
        }

        return null;
    }

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
