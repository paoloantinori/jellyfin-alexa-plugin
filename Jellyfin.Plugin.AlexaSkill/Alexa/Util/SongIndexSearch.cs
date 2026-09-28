using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// JF-440: the ONE song-index lookup chain (exact n-gram Search, then phonetic
/// SearchPhonetic when the flag is on and the exact stage missed). Was three
/// private copies with three index-readiness contracts (FindSong double-gated on
/// IsReady, PlaySong block-gated, the JF-439 artist fallback caught the exception):
/// a warming/flag semantics change landed in one copy and silently diverged the
/// others. Unified contract: a NULL index or a DISABLED one (gave up after repeated
/// load failures) returns an empty list so callers fall to their bounded DB paths;
/// a WARMING index throws <c>SkillWarmingUpException</c> from the index itself
/// (JF-419.3 layer 2) - callers with entry gates let it propagate to the pipeline's
/// single translation site, opportunistic fallbacks catch and degrade.
/// JF-654: this file also owns the song-side kana bar (see
/// <see cref="PassesKanaOriginSongAcceptance(string, BaseItem, double)"/>), the ONE shared acceptance
/// definition consumed by every song auto-play decision point fed by a
/// kana-origin query. JF-661: the bar's title-collision primitive
/// (<see cref="PassesLengthBandedTitleCollision"/>) is title-generic and also
/// backs the album bar (AlbumPlayService.PassesKanaOriginAlbumAcceptance);
/// songs and albums are both title-shaped candidates for the same DM-cap
/// rationale.
/// </summary>
internal static class SongIndexSearch
{
    /// <summary>
    /// JF-654: the near-exact plain-score leg of the song-side kana bar. On the
    /// KeywordMatcher coverage scale (exact full bidirectional coverage = 105:
    /// 0.7 keyword + 0.3 title coverage + the positional bonus; the legitimate
    /// full-coverage phonetic class = ~72-80; the live wrong-accept 'bitoruzu' ->
    /// 'Bitters &amp; Absolut' was a half-title-coverage phonetic match somewhere
    /// over the 65 coverage bar) a score of 95 means every query keyword is a
    /// VERBATIM title token AND at least ~2/3 of the title is covered: the
    /// near-exact class. The phonetic stage can never reach it (its ceiling is
    /// 75 + 5 positional + 10 residual = 90), so this leg admits exact-stage
    /// matches only. Mirrors the JF-652 review finding that plain 91-99 on the
    /// fuzzy scale does not prove a collision; 95+ on THIS scale is near-exact by
    /// construction, which is why it is an ALTERNATIVE acceptance leg here rather
    /// than collision evidence.
    /// </summary>
    internal const double KanaOriginSongPlainScoreBar = 95.0;

    /// <summary>
    /// JF-654: maximum length difference between the (parenthetical-stripped) song
    /// title and the romanized query for a Double Metaphone code collision to
    /// count as acceptance evidence. EMPIRICAL NECESSITY (verified against the
    /// production encoder; pinned by KanaOriginSongAcceptanceTests
    /// .PassesKanaOriginSongAcceptance_CollisionLeg): DM codes cap at 4 characters
    /// and are dominated by the FIRST word, so 'bitoruzu' and 'Bitters &amp;
    /// Absolut' BOTH encode to PTRS; the live wrong-accept IS a full-string code
    /// collision, and the bare artist-side collision predicate (JF-652) would have
    /// ACCEPTED it. The JF-381 lesson applies verbatim: a collision between
    /// strings of wildly different lengths is not the intended accent-drift shape,
    /// because the code cap collapses unrelated strings into the same skeleton.
    /// OWNED HERE (JF-654 review round 2): the value mirrors
    /// FuzzyMatcher.PhoneticFloorLengthBand (3) today, but the two bands are
    /// semantically distinct (the JF-381 artist accent-drift floor vs this song
    /// collision band) and tune separately; the mirroring is documented, not
    /// compiled. The band also bounds the album bar's shared title-collision
    /// helper (JF-661): one title-collision semantics, one band.
    /// </summary>
    internal const int KanaOriginSongCollisionLengthBand = 3;

    /// <summary>
    /// JF-654 song-side kana bar, the ONE shared definition consumed by every song
    /// auto-play decision point fed by a kana-origin query (TrySongFallback's
    /// acceptance, FindSong's chain acceptance, PlaySong's title fallback,
    /// SearchMedia's song-title retry): a real (length-banded) Double Metaphone
    /// code collision between the romanized query and the song title (the JF-652
    /// collision predicate, the title encoded at the decision point; song titles
    /// have no pre-computed code index) OR a near-exact plain score (>=
    /// <see cref="KanaOriginSongPlainScoreBar"/>). The wrong-accept class the bar
    /// kills (live, deployed a9c57451): a romaji syllable soup clearing the
    /// Latin-calibrated keyword-coverage bars against short English titles
    /// ('bitoruzu' phonetic-matched 'Bitters &amp; Absolut' and auto-played it),
    /// the same plain-fuzzy class JF-652 killed on the artist path, one layer
    /// over. A plain-fuzzy-only match at the coverage bar is the honest
    /// not-found. Latin queries never reach the predicate (kanaOrigin false at
    /// every call site: the byte-identical Latin matrix is pinned by the
    /// pre-existing suite).
    /// </summary>
    /// <param name="romanizedQuery">The romanized (post-KatakanaRomanizer) query string.</param>
    /// <param name="song">The candidate song.</param>
    /// <param name="score">The candidate's KeywordMatcher score.</param>
    /// <returns>True when the candidate carries a length-banded code collision or a near-exact plain score.</returns>
    internal static bool PassesKanaOriginSongAcceptance(string romanizedQuery, BaseItem song, double score)
        => PassesKanaOriginSongAcceptance(
            DoubleMetaphone.Encode(romanizedQuery), romanizedQuery.Length, song, score);

    /// <summary>
    /// Codes-carried form of <see cref="PassesKanaOriginSongAcceptance(string, BaseItem, double)"/>
    /// for callers that encode the query once and reuse it across several candidate
    /// checks (the FuzzyMatcher/FindNearTiedRunnerUp encode-once shape).
    /// </summary>
    /// <param name="queryCodes">The Double Metaphone codes of the romanized query.</param>
    /// <param name="romanizedQueryLength">The romanized query's length (the band input).</param>
    /// <param name="song">The candidate song.</param>
    /// <param name="score">The candidate's KeywordMatcher score.</param>
    /// <returns>True when the candidate carries a length-banded code collision or a near-exact plain score.</returns>
    internal static bool PassesKanaOriginSongAcceptance(
        (string Primary, string? Alternate) queryCodes,
        int romanizedQueryLength,
        BaseItem song,
        double score)
        => score >= KanaOriginSongPlainScoreBar
            || PassesLengthBandedSongCollision(queryCodes, romanizedQueryLength, song);

    /// <summary>
    /// The collision leg: a Double Metaphone code collision (primary/alternate
    /// cross-compared, the JF-652 predicate's semantics) between the romanized
    /// query and the song title AND the length band, so the DM code cap cannot
    /// manufacture collisions between a short romaji query and a long multi-word
    /// title (see <see cref="KanaOriginSongCollisionLengthBand"/> for the live
    /// 'bitoruzu' / 'Bitters &amp; Absolut' PTRS evidence). Both the band and the
    /// codes read the title WITHOUT its trailing parenthetical groups (JF-654
    /// review round 2): '(2011 Remaster)' / '(Live)' suffixes are metadata, not
    /// phonetic content, and the first-word-dominated DM code cannot see them
    /// either, so banding on the raw name would refuse legitimately colliding
    /// titles on characters that carry no evidence. A consonant-bearing
    /// non-parenthetical suffix ('Bitters &amp; Absolut' itself) still widens the
    /// title past the band and stays the documented refusal shape.
    /// </summary>
    private static bool PassesLengthBandedSongCollision(
        (string Primary, string? Alternate) queryCodes,
        int romanizedQueryLength,
        BaseItem song)
        => PassesLengthBandedTitleCollision(queryCodes, romanizedQueryLength, song.Name ?? string.Empty);

    /// <summary>
    /// Title-generic form of the collision leg (JF-661): album names carry the
    /// same '(Deluxe Edition)' parenthetical-metadata suffixes and the same
    /// DM-cap collapse risk, so the album bar
    /// (AlbumPlayService.PassesKanaOriginAlbumAcceptance) shares this ONE
    /// strip+band+encode implementation with the song bar instead of a private
    /// copy. Takes the title STRING so the song wrapper and the album predicate
    /// each feed their own entity shape.
    /// </summary>
    /// <param name="queryCodes">The Double Metaphone codes of the romanized query.</param>
    /// <param name="romanizedQueryLength">The romanized query's length (the band input).</param>
    /// <param name="title">The raw candidate title (song or album name).</param>
    /// <returns>True when the stripped title falls inside the band and its codes collide with the query's.</returns>
    internal static bool PassesLengthBandedTitleCollision(
        (string Primary, string? Alternate) queryCodes,
        int romanizedQueryLength,
        string title)
    {
        string stripped = StripTrailingParentheticalGroups(title);
        if (Math.Abs(stripped.Length - romanizedQueryLength) > KanaOriginSongCollisionLengthBand)
        {
            return false;
        }

        var titleCodes = DoubleMetaphone.Encode(stripped);
        return FuzzyMatcher.PhoneticCodesMatch(
            queryCodes.Primary, queryCodes.Alternate, titleCodes.Primary, titleCodes.Alternate);
    }

    /// <summary>
    /// Removes trailing parenthetical groups ('(2011 Remaster)', stacked
    /// '(Deluxe) (Live)') from a title, the JF-654 band's evidence input. A title
    /// that is entirely parenthetical strips to empty and fails the band (the
    /// honest miss).
    /// </summary>
    /// <param name="title">The raw song title.</param>
    /// <returns>The title without trailing parenthetical groups.</returns>
    private static string StripTrailingParentheticalGroups(string title)
    {
        string stripped = title.TrimEnd();
        while (stripped.EndsWith(')'))
        {
            int open = stripped.LastIndexOf('(');
            if (open < 0)
            {
                break;
            }

            stripped = stripped[..open].TrimEnd();
        }

        return stripped;
    }

    /// <summary>
    /// JF-654 list form of the kana bar for the scored-candidate chains: kana-origin
    /// queries keep only bar-passing candidates (the downstream auto-play,
    /// disambiguation, and not-found flows then operate on evidence only); every
    /// other query gets the list back unchanged. No score bar is imposed here for
    /// non-kana queries: the callers' existing gates (TrySongFallback's
    /// CrossMediaSongThreshold, the matchers' internal coverage gates) are
    /// untouched.
    /// </summary>
    /// <param name="scored">The scored candidates, best first.</param>
    /// <param name="romanizedQuery">The romanized query string.</param>
    /// <param name="kanaOrigin">Whether the query carried kana pre-romanization.</param>
    /// <returns>The filtered list for kana-origin queries, else the input list.</returns>
    internal static List<(BaseItem Item, double Score)> ApplyKanaOriginBar(
        this List<(BaseItem Item, double Score)> scored,
        string romanizedQuery,
        bool kanaOrigin)
    {
        if (!kanaOrigin)
        {
            return scored;
        }

        // Encode the query once for the whole loop (the encode-once rule the
        // codes-carried ArtistSearch overloads document).
        var queryCodes = DoubleMetaphone.Encode(romanizedQuery);
        var kept = new List<(BaseItem Item, double Score)>(scored.Count);
        foreach (var candidate in scored)
        {
            if (PassesKanaOriginSongAcceptance(queryCodes, romanizedQuery.Length, candidate.Item, candidate.Score))
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    /// <summary>
    /// Exact n-gram search, then the phonetic stage on miss. Callers own the
    /// <c>phoneticEnabled</c> flag (per-user/global config) and the library filter.
    /// </summary>
    /// <param name="index">The song n-gram index (null in minimal setups: empty result).</param>
    /// <param name="keywordTokens">The tokenized query.</param>
    /// <param name="locale">The locale string for tokenizing song titles.</param>
    /// <param name="topParentIds">Resolved library scope (see LibraryFilter.ResolveForUser, the canonical entry).</param>
    /// <param name="phoneticEnabled">Whether the phonetic fallback stage may run (caller's PhoneticSongSearchEnabled).</param>
    /// <returns>Scored candidates, best first; empty when neither stage matched.</returns>
    internal static List<(BaseItem Item, double Score)> SearchWithPhoneticFallback(
        this ISongNgramIndex? index,
        string[] keywordTokens,
        string locale,
        Guid[]? topParentIds,
        bool phoneticEnabled)
    {
        if (index == null)
        {
            return new List<(BaseItem, double)>();
        }

        var scored = index.Search(keywordTokens, locale, topParentIds);
        if (scored.Count == 0 && phoneticEnabled)
        {
            scored = index.SearchPhonetic(keywordTokens, locale, topParentIds);
        }

        return scored;
    }
}
