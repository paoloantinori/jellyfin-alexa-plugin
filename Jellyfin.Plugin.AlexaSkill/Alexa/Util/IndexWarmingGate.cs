using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// JF-419/JF-419.3: the warming gates, two layers. Layer 1: handlers gate at entry
/// on the index their request path actually uses (artist-search paths on
/// <see cref="IArtistIndex"/>, song-title paths on <see cref="ISongNgramIndex"/>),
/// before their "searching" announcement. Layer 2: <see cref="ArtistSearch.SearchAsync"/>
/// re-checks the artist gate at its entry, the choke point covering every caller
/// including BaseHandler fallbacks and future handlers. While an index is present
/// but still loading, the alternative is the cold database path that can exceed
/// Alexa's ~8-second window (live incident 2026-08-31 07:59); throwing lets the
/// request pipeline answer with the SkillWarmingUp Tell for every intent at once.
/// Enrichment-only callers may catch the exception and degrade (see MediaInfo).
/// </summary>
internal static class IndexWarmingGate
{
    /// <summary>Throws when the artist index exists but is still loading.</summary>
    /// <param name="artistIndex">The artist index (null in test/minimal setups: no gate; disabled: degrade, no gate).</param>
    public static void EnsureReady(IArtistIndex? artistIndex)
    {
        if (artistIndex != null && !artistIndex.IsReady && !artistIndex.IsDisabled)
        {
            throw new SkillWarmingUpException("artist");
        }
    }

    /// <summary>Throws when the song n-gram index exists but is still loading.</summary>
    /// <param name="songIndex">The song index (null in test/minimal setups: no gate; disabled: degrade, no gate).</param>
    public static void EnsureReady(ISongNgramIndex? songIndex)
    {
        if (songIndex != null && !songIndex.IsReady && !songIndex.IsDisabled)
        {
            throw new SkillWarmingUpException("song n-gram");
        }
    }
}
