using System;
using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// JF-690: the multi-value ER arbitration gate, the ONE definition shared by the
/// musician-slot paths. When Alexa's entity resolution returns MORE THAN ONE
/// distinct canonical value for a slot (the JF-684 shared-first-word shape: the
/// live "pink" returning [P!nk, Pink Floyd] because both catalog entries carry a
/// "pink"-matching synonym), the plain single-value read
/// (<see cref="SlotValueHelper.GetCanonicalValue(Slot)"/>) keeps only Amazon's
/// rank #1 and the exact-name hit auto-plays through the JF-420.1 equality
/// bypass with no prompt, silently. This gate resolves EVERY candidate against
/// the user's library by exact-name equality and, when TWO OR MORE distinct
/// library artists resolve, fires the existing JF-420.2 yes/no disambiguation
/// (<see cref="DisambiguationHelper.AskMultipleArtists"/>) with real library ids
/// (the confirm leg resolves ids via GetItemById, so raw ER names without ids
/// are dead on "yes"). Candidates that no longer exist in the library (stale
/// catalog) drop out before the prompt, and an ambiguity that collapses to ONE
/// library artist returns that artist instead of letting the stale rank-#1
/// canonical drive a not-found. The gate closes without the in-memory index
/// (the cold/database path keeps today's rank-#1 behavior, the same accepted
/// trade-off class as the JF-420 alternative pool and the JF-652 near-tie pool)
/// and on single-value slots (the ordinary shape, byte-identical to before).
/// </summary>
internal static class MultiValueErDisambiguation
{
    /// <summary>
    /// The gate's outcome: the ask to return verbatim when the ambiguity is REAL
    /// (two or more distinct library artists), otherwise the SINGLE library
    /// artist the ER ambiguity collapsed to (the one real match among stale
    /// catalog candidates), or null on every closed-gate leg. <see cref="Pool"/>
    /// carries the pinned view's artist list whenever the gate fetched it, so a
    /// caller with its own pool cache can seed it instead of re-materializing.
    /// </summary>
    internal readonly record struct ErArtistArbitration(
        SkillResponse? Ask,
        BaseItem? ResolvedArtist,
        IReadOnlyList<BaseItem>? Pool);

    /// <summary>
    /// Arbitrate a multi-value ER match on the musician slot
    /// (<see cref="IntentNames.Slots.Musician"/>): return the yes/no multi-artist
    /// ask when the ER ambiguity is REAL in the user's library (at least two
    /// distinct library artists exactly named by the ER candidates); the ask's
    /// match order is the ER rank order, so "yes" keeps Amazon's rank-#1 ARTIST
    /// and "no" explores the alternative. When the list collapses to exactly ONE
    /// library artist (the other values are stale catalog entries), return that
    /// artist: it is proven library evidence and outranks a search driven by the
    /// stale rank-#1 canonical, which today answers not-found with the survivor
    /// sitting resolved. All closed-gate legs (single ER value, no ready index,
    /// zero resolves) return the default struct: the caller proceeds on its
    /// existing single-value path byte-identically.
    /// </summary>
    /// <param name="request">The intent request carrying the slot's entity resolution.</param>
    /// <param name="user">The plugin user (library scope resolution).</param>
    /// <param name="artistIndex">The handler's artist index: a live index or an
    /// already-pinned view (capture is idempotent, JF-448), so a caller that pinned
    /// for its own search chain hands the same view down; null or not ready closes the gate.</param>
    /// <param name="libraryManager">The library manager (scope resolution).</param>
    /// <param name="logger">The handler's logger (the gate logs every leg, JF-690's observability).</param>
    /// <param name="locale">The request locale (the ask's strings).</param>
    /// <returns>The arbitration outcome (see summary).</returns>
    internal static ErArtistArbitration TryArbitrate(
        IntentRequest request,
        Entities.User user,
        IArtistIndex? artistIndex,
        ILibraryManager libraryManager,
        ILogger logger,
        string locale)
    {
        IReadOnlyList<string> candidates = SlotValueHelper.GetCanonicalValues(request, IntentNames.Slots.Musician);
        if (candidates.Count <= 1)
        {
            return default;
        }

        IArtistIndex? pinned = artistIndex?.IsReady == true ? artistIndex.Pin() : null;
        if (pinned == null)
        {
            logger.LogDebug(
                "MultiValueEr: {Count} ER values on the musician slot but no ready artist index; Amazon's top rank arbitrates (JF-690 cold path)",
                candidates.Count);
            return default;
        }

        Guid[]? topParentIds = LibraryFilter.ResolveForUser(user, libraryManager, logger);
        IReadOnlyList<BaseItem> pool = pinned.GetArtists(topParentIds);
        List<BaseItem> resolved = ArtistSearch.ResolveExactNameMatches(candidates, pool);
        if (resolved.Count == 1)
        {
            logger.LogInformation(
                "MultiValueEr: {Count} ER values on the musician slot collapse to the single library artist '{Artist}'; playing it (JF-690)",
                candidates.Count, resolved[0].Name);
            return new ErArtistArbitration(null, resolved[0], pool);
        }

        if (resolved.Count < 2)
        {
            logger.LogDebug(
                "MultiValueEr: {Count} ER values on the musician slot resolve to no library artist; proceeding on the single-value path (JF-690)",
                candidates.Count);
            return new ErArtistArbitration(null, null, pool);
        }

        logger.LogInformation(
            "MultiValueEr: ER returned {Count} values on the musician slot naming {Resolved} library artists ({Names}); asking which to play (JF-690)",
            candidates.Count, resolved.Count, string.Join(", ", resolved.Select(a => a.Name)));
        var matches = resolved
            .Select(a => new DisambiguationHelper.MatchInfo { Id = a.Id.ToString(), Name = a.Name })
            .ToList();
        return new ErArtistArbitration(DisambiguationHelper.AskMultipleArtists(matches, locale), null, pool);
    }
}
