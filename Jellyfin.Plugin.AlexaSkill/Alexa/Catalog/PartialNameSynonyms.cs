#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// JF-684: adds the bare first substantive word of a multi-word ARTIST name as a
/// catalog synonym ("Pink" on Pink Floyd's entry, "Beatles" on The Beatles'). Why:
/// a catalog-backed slot type (valueSupplier.valueCatalog) gates NLU intent
/// SELECTION itself. When the spoken value matches no catalog value or synonym,
/// Alexa selects NO intent at all, so the request never reaches the skill and the
/// handler-side fuzzy chain (the 4-tier artist search, the JF-377/JF-420 gates)
/// is voice-unreachable for partial/truncated names. Live A/B proof (2026-09-30,
/// skill 33dfacd5, it-IT, artist catalog v1188): "suona la musica di pink" and
/// "suona la musica di beatles" selected NO intent; a throwaway catalog version
/// identical to v1188 except "pink" on Pink Floyd and "beatles" on The Beatles
/// made both select an intent with ER_SUCCESS_MATCH ("suona la cantante pink"
/// selected PlayArtistSongsIntent directly), while the not-added control "norah"
/// still selected nothing. This refines the JF-642 finding: static custom types
/// restrict only ER resolution, CATALOG types constrain intent selection.
/// The bare word behaves like any other catalog synonym (the long-working "cup"
/// velar-stop variant of Koop is the same shape). A word two artists share ("pink"
/// matching P!nk AND Pink Floyd) yields a multi-value ER match whose WINNER is
/// Amazon's ranking: GetCanonicalValue returns authority.Values[0] only, the JF-659
/// contract feeds that canonical to the artist search verbatim, and the exact-name
/// hit auto-plays through the JF-420.1 equality bypass. The yes/no disambiguation
/// prompt does NOT fire on this path (it arbitrates only the raw-text path, where
/// no canonical exists); the shared-word consequence is tracked as JF-690.
/// Known misses, both in the safe direction (no synonym rather than a wrong one):
/// the stop-word union includes the ja/hi romaji function words ("made", "kara",
/// "yori", "nado", "mein"), so a Latin first word colliding with them ("Made in
/// Heights" -> "made") yields nothing; and "Al Green"-class names yield nothing
/// because "al" is deliberately NOT a stop word (a name prefix, JF-389) and then
/// fails the length bar.
/// Scope: catalog payloads only. DynamicEntityBuilder (turn-2+ in-session entities)
/// deliberately does NOT append the partial word: it serves open-session ER, not
/// intent selection, and its own entry budget would evict at the margin.
/// </summary>
internal static class PartialNameSynonyms
{
    /// <summary>
    /// Minimum length for the partial word. "pink"/"crash" pass; the "led"-class
    /// short words ("Led Zeppelin" -> "led") must never become synonyms (they are
    /// too generic to own), so a too-short first substantive word yields NO synonym
    /// rather than skipping deeper into the name.
    /// </summary>
    private const int MinWordLength = 4;

    /// <summary>
    /// Computes the partial synonym for a name: the first substantive (non-stop-word)
    /// word of a multi-word name, preserving its casing in the name. A leading article
    /// or other function word is skipped ("The Beatles" -> "Beatles") using the union
    /// of KeywordMatcher's stop-word sets across all 17 locales. Returns null when the
    /// name is single-word, the candidate is shorter than <see cref="MinWordLength"/>,
    /// the candidate itself is a stop word, or the candidate carries non-letter
    /// characters (digits/punctuation, e.g. a leading "50 Cent" or "5.6.7.8's").
    /// </summary>
    /// <param name="name">The full artist name.</param>
    /// <returns>The bare first word, or null when the gate rejects the name.</returns>
    internal static string? Generate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return null;
        }

        // Skip a leading article/function word to the first substantive word. Only one
        // skip: "The Beatles" -> "Beatles". A second stop word ("Da La Soul" -> "La")
        // rejects the name instead of digging to a mid-name word.
        string candidate = words[0];
        if (KeywordMatcher.IsStopWordInAnyLocale(candidate))
        {
            candidate = words[1];
        }

        if (candidate.Length < MinWordLength
            || KeywordMatcher.IsStopWordInAnyLocale(candidate)
            || !candidate.All(char.IsLetter))
        {
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// Appends the partial synonym to a catalog entry's synonym list, no-op for
    /// non-Artist catalog types (JF-684 scope: the musician slot only; album first
    /// words stay untouched because the AlbumName anchors already steal artist
    /// queries, the JF-508 family, and widening that surface was not probed).
    /// Dedup and the 140-char slot cap ride the shared coverage-variant idiom
    /// (<see cref="PhoneticSynonymGenerator.AppendDistinct"/>) like every other
    /// synonym builder.
    /// </summary>
    /// <param name="name">The entry's name block to enrich in place.</param>
    /// <param name="type">The catalog type the entry belongs to.</param>
    internal static void AppendTo(CatalogValueName name, CatalogType type)
    {
        if (type != CatalogType.Artist)
        {
            return;
        }

        string? partial = Generate(name.Value);
        if (partial == null)
        {
            return;
        }

        var synonyms = name.Synonyms ??= new List<string>();
        PhoneticSynonymGenerator.AppendDistinct(synonyms, new[] { SlotValueHelper.Truncate(partial) });
    }
}
