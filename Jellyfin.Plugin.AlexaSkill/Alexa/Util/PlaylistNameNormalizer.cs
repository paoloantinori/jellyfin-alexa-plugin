using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Strips a leaked leading "called" carrier from a raw playlist slot value so the
/// tiered match (and the create path's new-playlist name) sees the spoken name
/// (JF-600: live incident 2026-09-20, "crea una playlist chiamata prova echo" filled
/// playlist_target with "chiamata prova echo"). Keyed by language prefix of the
/// request locale; ja also strips the TRAILING carrier because its sample order
/// puts the qualifier after the name.
/// SIBLING STRIP MECHANISMS (do not merge the word tables, they are per-feature):
/// AlbumPlayService carries the album-slot strip (JF-469) and
/// PlayVideoIntentHandler the media-noun strip (JF-509). Those two are
/// FALLBACK-ONLY (strip after a confirmed miss) because real album titles collide
/// with carrier words ("Called Out in the Dark"). This one is a PREEMPTIVE strip:
/// playlist names are user-coined free text, and the plugin's own create path is
/// the only writer in that namespace, so a carrier-prefixed playlist name is a
/// slot-fill artifact, not a title to protect. ACCEPTED TRADE-OFF: a user who
/// deliberately names a playlist "Chiamata Sei" gets "Sei" (the strip is
/// unconditional and repeated); no recovery path exists for that name.
/// </summary>
public static class PlaylistNameNormalizer
{
    /// <summary>
    /// Leading "called" carriers with the trailing separator space baked into the
    /// entry (allocation-free prefix checks; a trimmed name equal to the bare
    /// carrier word can never match, so "Chiamata" as a full name is preserved).
    /// </summary>
    private static readonly Dictionary<string, string[]> LeadingCarriers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["it"] = new[] { "chiamata ", "chiamato ", "chiamate ", "chiamati " },
        ["en"] = new[] { "called ", "named " },
        ["de"] = new[] { "namens " },
        ["es"] = new[] { "llamada ", "llamado " },
        ["fr"] = new[] { "appelée ", "appelé ", "appelee ", "appele " },
        ["pt"] = new[] { "chamada ", "chamado " },
        ["nl"] = new[] { "genaamd " },
        ["ar"] = new[] { "باسم " },
        ["hi"] = new[] { "नाम की ", "नाम का " },
        ["ja"] = new[] { "という " }
    };

    /// <summary>
    /// Trailing carriers for the locales whose create samples put the qualifier
    /// AFTER the name (ja "{playlist} という...", hi "{playlist} नाम की..."). The
    /// leading space is baked in for the same reason as above; Japanese ASR often
    /// emits the unspaced form, which simply does not match and keeps today's
    /// behavior (fail-safe).
    /// </summary>
    private static readonly Dictionary<string, string[]> TrailingCarriers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = new[] { " という" },
        ["hi"] = new[] { " नाम की", " नाम का" }
    };

    /// <summary>
    /// Strips leaked leading (and ja trailing) "called" carriers from a raw playlist
    /// slot value. Returns null when nothing survives the strip (the caller's
    /// missing-slot branch re-prompts). A name that IS the carrier word alone is
    /// preserved.
    /// </summary>
    /// <param name="raw">The raw slot value (null/whitespace allowed).</param>
    /// <param name="locale">The request locale (carrier table is per language).</param>
    /// <returns>The stripped name, or null when empty.</returns>
    public static string? NormalizePlaylistName(string? raw, string locale)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Locale-prefix extraction: the fourth private copy of this two-liner
        // (KeywordMatcher, PhoneticSynonymGenerator, ResponseStrings); retire into
        // a shared helper when a fifth appears.
        string prefix = locale.Split('-')[0];
        if (!LeadingCarriers.TryGetValue(prefix, out string[]? leading))
        {
            return raw.Trim();
        }

        string name = raw.Trim();
        TrailingCarriers.TryGetValue(prefix, out string[]? trailing);
        bool stripped;
        do
        {
            stripped = false;
            foreach (string carrier in leading)
            {
                if (name.StartsWith(carrier, StringComparison.OrdinalIgnoreCase))
                {
                    name = name[carrier.Length..].TrimStart();
                    stripped = true;
                    break;
                }
            }

            if (!stripped && trailing != null)
            {
                foreach (string carrier in trailing)
                {
                    if (name.EndsWith(carrier, StringComparison.OrdinalIgnoreCase))
                    {
                        name = name[..^carrier.Length].TrimEnd();
                        stripped = true;
                        break;
                    }
                }
            }
        }
        while (stripped);

        return name.Length == 0 ? null : name;
    }
}
