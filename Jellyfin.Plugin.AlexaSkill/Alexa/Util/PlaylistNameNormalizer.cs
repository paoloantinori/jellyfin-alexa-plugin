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
/// WHO WRITES PLAYLIST NAMES: the server-wide IPlaylistManager, i.e. this
/// plugin's create path AND the Jellyfin web UI AND .m3u imports, so a playlist
/// genuinely named "Chiamata Sei" is a REAL title this strip must not shadow.
/// USAGE CONTRACT (JF-602, mirrors the JF-469 album-strip fallback-only rule):
/// the EDIT family (FindPlaylist) tries the RAW name on every tier first and
/// only retries with this strip on a miss, interleaved so a stripped exact hit
/// beats a raw substring hit; the CREATE path keeps the stripped form for the
/// new playlist's name (the spoken intent) while the duplicate check runs
/// raw-first; the PLAY paths (PlayPlaylist/ShufflePlay, SearchTerm-contains
/// lookup) still apply the strip at read time; their raw-first rework is OPEN
/// debt (the JF-610 review kept it out: the AlbumPlayService lookup needs its
/// own tier design). SIBLING STRIP MECHANISMS (do not merge the word tables, they are
/// per-feature): AlbumPlayService carries the album-slot strip (JF-469) and
/// PlayVideoIntentHandler the media-noun strip (JF-509).
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
    /// AFTER the name (ja "{playlist} という...", hi "{playlist} नाम की...").
    /// DELIBERATE dual-table presence: hi ALSO carries leading "नाम की " forms
    /// because its speech admits both orders ("X नाम की प्लेलिस्ट" and the
    /// reversed carrier-first fill); the flat loop tries leading first, so the
    /// leading entry wins when both could apply. The
    /// leading space is baked in for the same reason as above. ja ALSO carries
    /// the unspaced form because Japanese ASR routinely drops the boundary space
    /// (JF-607); という is a particle, never a title ending, so the unspaced
    /// strip is safe.
    /// </summary>
    private static readonly Dictionary<string, string[]> TrailingCarriers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Unspaced form included: Japanese ASR routinely drops the boundary space,
        // and という is a particle, never a title ending (JF-607).
        ["ja"] = new[] { " という", "という" },
        ["hi"] = new[] { " नाम की", " नाम का" }
    };

    /// <summary>
    /// Strips leaked leading (and ja/hi trailing) "called" carriers from a raw
    /// playlist slot value; a value that IS the bare carrier word (plus optional
    /// whitespace) is preserved as-is. Returns null only for whitespace-only
    /// input (the caller's missing-slot branch re-prompts).
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

        string prefix = LocalePrefix.Of(locale);
        if (!LeadingCarriers.TryGetValue(prefix, out string[]? leading))
        {
            return raw.Trim();
        }

        string name = raw.Trim();
        TrailingCarriers.TryGetValue(prefix, out string[]? trailing);
        // The flat strip loop (JF-610): one cut per iteration on the shared
        // CarrierPhrase primitive until neither direction cuts. Leading first,
        // then trailing - the same order the old do/while enforced.
        while (CarrierPhrase.TryStripLeading(ref name, leading)
            || (trailing != null && CarrierPhrase.TryStripTrailing(ref name, trailing)))
        {
        }

        // A fully-carrier value never reaches here: the primitive treats an
        // empty remainder as no cut, so name always carries at least the raw
        // carrier text (the bare-carrier keep).
        return name;
    }
}
