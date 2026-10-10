using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The multi-match policy of the shared named-item resolve chain
/// (<see cref="SearchService.ResolveNamedItemAsync"/>, JF-862): what the chain does
/// when the SearchTerm tier returns more than one match. The three inline chains the
/// helper hoisted genuinely DIFFER here (the JF-329 completion round's
/// extraction-on-convergence finding), so the policy is a required parameter, never
/// a default: a new caller must pick its shape instead of silently inheriting one.
/// </summary>
public enum NamedItemMultiMatchPolicy
{
    /// <summary>
    /// Jellyfin's SearchTerm relevance order is the authority: the first row wins,
    /// no arbitration (the TvNextUp.ResolveSeriesForPlaybackAsync shape, the
    /// first-match precedent the photo-album below-bar fallback cites).
    /// </summary>
    FirstMatchWins,

    /// <summary>
    /// Fuzzy arbitration first (KeywordMatcher.ScoringName, the JF-776 romaji
    /// reading, at the user's default threshold); when nothing clears the fuzzy bar
    /// the TOP-RANKED SearchTerm match still wins (the ShowPhotoAlbum shape: a hard
    /// not-found would contradict the matches the query just returned, and the
    /// resolved item is announced by name).
    /// </summary>
    ArbitrateThenTopRanked,

    /// <summary>
    /// The chain returns the raw SearchTerm matches unarbitrated
    /// (<see cref="NamedItemResolution.Matches"/>) and the CALLER arbitrates (the
    /// PlayBook shape: NormalizeBookCandidates, then HandleFuzzyMiss, then
    /// AskFirstMatch). The prompt flow is Handler-layer (session state,
    /// SkillResponse building), which this Util-layer chain cannot host (the
    /// namespace discipline documented on TvNextUpService), so the decision stays
    /// at its decision point per the JF-408 fuzzy-recall-vs-judgment-layers memory.
    /// </summary>
    CallerArbitrates
}

/// <summary>
/// The terminal state of <see cref="SearchService.ResolveNamedItemAsync"/>. Exactly
/// one payload is non-null: <see cref="Item"/> on a resolved chain (the single
/// SearchTerm match short-circuit, the policy's pick, or the fuzzy fallback's hit),
/// <see cref="Matches"/> on an ambiguous one (the raw SearchTerm matches under
/// <see cref="NamedItemMultiMatchPolicy.CallerArbitrates"/>), and neither on a
/// not-found (both tiers missed). The chain never builds a response: each caller
/// speaks its own localized not-found, whose wording and media noun differ by site.
/// </summary>
public sealed class NamedItemResolution
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NamedItemResolution"/> class.
    /// Private so the factory methods own the one-payload invariant.
    /// </summary>
    /// <param name="item">The resolved item, or null.</param>
    /// <param name="matches">The raw matches, or null.</param>
    private NamedItemResolution(BaseItem? item, IReadOnlyList<BaseItem>? matches)
    {
        Item = item;
        Matches = matches;
    }

    /// <summary>
    /// Gets the resolved item, or null when the chain is ambiguous or not-found.
    /// </summary>
    public BaseItem? Item { get; }

    /// <summary>
    /// Gets the raw SearchTerm matches for the caller to arbitrate, or null unless
    /// the chain ran under <see cref="NamedItemMultiMatchPolicy.CallerArbitrates"/>
    /// and the SearchTerm tier returned more than one match.
    /// </summary>
    public IReadOnlyList<BaseItem>? Matches { get; }

    /// <summary>Factory for the resolved state.</summary>
    /// <param name="item">The resolved item.</param>
    /// <returns>The resolution.</returns>
    public static NamedItemResolution Resolved(BaseItem item) => new(item, null);

    /// <summary>Factory for the ambiguous state.</summary>
    /// <param name="matches">The raw SearchTerm matches.</param>
    /// <returns>The resolution.</returns>
    public static NamedItemResolution Ambiguous(IReadOnlyList<BaseItem> matches) => new(null, matches);

    /// <summary>Factory for the not-found state (both tiers missed).</summary>
    /// <returns>The resolution.</returns>
    public static NamedItemResolution NotFound() => new(null, null);
}
