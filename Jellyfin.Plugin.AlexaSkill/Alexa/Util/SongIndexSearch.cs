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
/// </summary>
internal static class SongIndexSearch
{
    /// <summary>
    /// Exact n-gram search, then the phonetic stage on miss. Callers own the
    /// <c>phoneticEnabled</c> flag (per-user/global config) and the library filter.
    /// </summary>
    /// <param name="index">The song n-gram index (null in minimal setups: empty result).</param>
    /// <param name="keywordTokens">The tokenized query.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="topParentIds">Resolved library filter (see LibraryFilter.ResolveTopParentIds).</param>
    /// <param name="phoneticEnabled">Whether the phonetic fallback stage may run.</param>
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
