using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using MediaType = Jellyfin.Data.Enums.MediaType;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SearchMediaIntent — unified search across all Jellyfin content types.
/// </summary>
public class SearchMediaIntentHandler : BaseHandler
{
    private static readonly BaseItemKind[] _playableTypes =
    [
        BaseItemKind.Audio,
        BaseItemKind.MusicAlbum,
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Series,
        BaseItemKind.Playlist,
        BaseItemKind.AudioBook
    ];

    // JF-456 (GH #22 residual): playlists live outside every media library, so under a
    // library restriction they cannot ride the unified query (the TopParentIds filter
    // would drop them). The kinds split once here, from LibraryFilter's single decision
    // point, so the handler never hardcodes which kinds are exempt.
    private static readonly BaseItemKind[] _libraryScopedPlayableTypes =
        _playableTypes.Where(t => !Util.LibraryFilter.IsOutOfLibraryKind(t)).ToArray();

    private static readonly BaseItemKind[] _outOfLibraryPlayableTypes =
        _playableTypes.Where(Util.LibraryFilter.IsOutOfLibraryKind).ToArray();

    private const int ArtistFallbackThreshold = 3;

    /// <summary>
    /// The playable-kind scopes for one request: the ONE definition of the JF-456
    /// split, consumed by BOTH the primary search path
    /// (<see cref="SearchPlayableKindsAsync"/>, which wraps each scope in
    /// FilterByContentAccess) and the fuzzy fallback in HandleAsync (which passes the
    /// scopes to SearchItemsFuzzyAsync's kind-aware ApplyLibraryFilter). Unrestricted
    /// users search a single unified scope; restricted users get the library-scoped
    /// kinds first and the out-of-library kinds (playlists) as the sibling scope,
    /// whose all-exempt kind set keeps the TopParentIds filter off it.
    /// </summary>
    /// <param name="libraryRestricted">Whether the user carries a library restriction.</param>
    /// <returns>The primary scope and, under a restriction, the sibling scope.</returns>
    private static (BaseItemKind[] Primary, BaseItemKind[]? Sibling) KindScopes(bool libraryRestricted)
        => libraryRestricted
            ? (_libraryScopedPlayableTypes, _outOfLibraryPlayableTypes)
            : (_playableTypes, null);

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ISongNgramIndex? _songNgramIndex;

    public SearchMediaIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        ISongNgramIndex? songNgramIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _songNgramIndex = songNgramIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(
            intentRequest.Intent.Name, IntentNames.SearchMedia, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(
        Request request,
        Context context,
        Entities.User user,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? query = intentRequest.Intent.Slots?.TryGetValue("query", out var slot) == true
            ? slot.Value
            : null;

        Logger.LogDebug("SearchMedia: entered, locale={Locale}", locale);

        if (string.IsNullOrWhiteSpace(query))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("CouldNotUnderstand", locale));
        }

        // JF-643: the query feeds the SearchTerm index, the artist fallback, the fuzzy
        // pass, and the n-gram song-title retry; all compare against Latin-script
        // library names, so a katakana query is romanized once here.
        // JF-654: the kana-origin flag is captured on the RAW query, BEFORE the
        // romanization (the query slot is free-text: no ER canonical). It gates
        // every auto-play this handler can reach: the ARTIST fallback's resolved
        // artist (live a7d42b09: tier-4 plain-fuzzy 'Sator' for 'bitoruzu'
        // auto-played; the JF-652 decision points never covered SearchMedia),
        // the fuzzy pass's pick (the JF-781 walk's acceptance bar; the fuzzy scan
        // covers the playable kinds INCLUDING songs), the FuzzyMatch topMatch
        // full-coverage pre-check, and the JF-506 song-title retry. The primary
        // SearchTerm path stays ungated: a server-side index match on a romanized
        // string is literal, not the fuzzy wrong-accept class.
        bool kanaOrigin = Util.ArtistSearch.IsKanaOriginQuery(null, query);
        query = Util.KatakanaRomanizer.Romanize(query);

        // Layer-1 gate (GuardIndexReady): before the "searching" announcement.
        GuardIndexReady(_artistIndex);

        RunFireAndForget(SendProgressiveResponse(
            context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // Library scope resolved ONCE per request, the ArtistSearch hoisted shape
        // (code-review F6): one ResolveForUser shared by the primary, sibling, and
        // artist-items queries instead of a resolution inside every ApplyLibraryFilter
        // call. A null scope = unrestricted user; those keep the single unified query,
        // which already returns out-of-library kinds, so no sibling query is needed.
        Guid[]? topParentIds = Util.LibraryFilter.ResolveForUser(user, _libraryManager, Logger);
        bool libraryRestricted = topParentIds != null;

        IReadOnlyList<BaseItem> results = await Search.SearchWithAsrFallbackAsync(query,
            searchTerm => SearchPlayableKindsAsync(searchTerm, jellyfinUser, topParentIds, cancellationToken)).ConfigureAwait(false);

        Logger.LogDebug("SearchMedia: Jellyfin returned {ResultCount} results for query='{Query}'", results.Count, query);

        if (results.Count <= ArtistFallbackThreshold)
        {
            IReadOnlyList<BaseItem> artistResults = await SearchByArtistNameAsync(query, jellyfinUser!, user, topParentIds, locale, kanaOrigin, cancellationToken).ConfigureAwait(false);
            if (artistResults.Count > 0)
            {
                Logger.LogInformation("Artist fallback for '{Query}': found {Count} items via artist lookup", query, artistResults.Count);
                results = results.Concat(artistResults).ToList();
            }
        }

        if (results.Count == 0)
        {
            // Under a restriction the fuzzy pass runs per kind-scope too: the mixed
            // array keeps the TopParentIds filter, so out-of-library kinds would again
            // be invisible (JF-456). Each call's ApplyLibraryFilter decides via the
            // kind-aware predicate, no flag at the call site.
            (BaseItemKind[] fuzzyPrimaryTypes, BaseItemKind[]? fuzzySiblingTypes) = KindScopes(libraryRestricted);
            // JF-781: the kana bar rides the fuzzy pass as the acceptance predicate
            // of the JF-776 B1 refuse-and-continue walk (SearchItemsFuzzyAsync's
            // optional bar, the playlist site's JF-777 idiom) instead of the
            // post-pick refuse-and-stop this site kept: a refused single pick left
            // results empty with the exact item still in the scan, and the only
            // recovery leg, the JF-506 song-title retry below, is Audio-ONLY, so
            // every other playable kind (Movie/Episode/Series/Playlist/AudioBook/
            // MusicAlbum) answered the honest MediaNotFound with the exact item in
            // the library (the JF-777 shadow; the JF-777 pins used song fixtures,
            // so the retry masked the gap). The query's tokens and DM codes are
            // derived once when the bar is armed (the encode-once idiom the
            // codes-carried overloads document: the walk evaluates the bar once
            // per refused winner); a disarmed (Latin) query passes a null bar and
            // keeps the byte-identical single-pick path.
            Func<BaseItem, bool>? fuzzyPassBar = null;
            if (kanaOrigin)
            {
                var fuzzyQueryTokens = KeywordMatcher.Tokenize(query, locale);
                var fuzzyQueryCodes = DoubleMetaphone.Encode(query);
                fuzzyPassBar = item => PassesKanaSongGate(item, fuzzyQueryTokens, fuzzyQueryCodes, query.Length, locale);
            }

            var fuzzy = await Search.SearchItemsFuzzyAsync(query, jellyfinUser, user, _libraryManager, fuzzyPrimaryTypes, cancellationToken, "SearchMediaFuzzyFallback", locale: locale, acceptanceBar: fuzzyPassBar).ConfigureAwait(false);
            if (fuzzy == null && fuzzySiblingTypes != null)
            {
                fuzzy = await Search.SearchItemsFuzzyAsync(query, jellyfinUser, user, _libraryManager, fuzzySiblingTypes, cancellationToken, "SearchMediaFuzzyOutOfLibrary", locale: locale, acceptanceBar: fuzzyPassBar).ConfigureAwait(false);
            }

            if (fuzzy != null)
            {
                results = new List<BaseItem> { fuzzy.Value.Item };
            }
            else
            {
                // JF-506: song-title retry on the confirmed miss (no candidates,
                // below threshold, coverage-withheld, or the JF-781 bar walked the
                // whole scan out). Live evidence
                // (corr=3240220d): a song title that reached this handler through a
                // generic carrier ran the SearchTerm query (0), the 4-tier artist
                // fallback (0), and the fuzzy pass, which scans only the FIRST 500
                // rows of the playable kinds and so misses most of a large song
                // catalog; the not-found fired while the song existed. The n-gram
                // index is the O(1) complete song-title lookup (the same chain
                // PlaySong's title fallback uses, JF-440); found songs feed the
                // normal result flow below (single auto-play / fuzzy / disambiguate).
                IReadOnlyList<BaseItem> songTitleHits = TrySongTitleRetry(query, locale, topParentIds, kanaOrigin);
                if (songTitleHits.Count == 0)
                {
                    Logger.LogInformation("Search for '{Query}' returned no results", query);
                    return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
                }

                Logger.LogInformation("Song-title retry for '{Query}': matched {Count} songs via the n-gram index", query, songTitleHits.Count);
                results = songTitleHits;
            }
        }

        var deduped = results.GroupBy(i => i.Id).Select(g => g.First()).ToList();
        Logger.LogInformation("Search for '{Query}' returned {Count} deduplicated results", query, deduped.Count);

        if (deduped.Count == 1)
        {
            Logger.LogInformation("Single result — auto-playing '{Item}'", deduped[0].Name);
            return await PlayItem(deduped[0], user, session, context, request, locale, jellyfinUser).ConfigureAwait(false);
        }

        // Disambiguation uses MediaTypeSong; YesIntentHandler will play matches as audio.
        // Mixed-type results (audio + video) are rare for search disambiguation.
        // JF-776: the string FuzzyMatch leg scores through the romaji reading
        // (ScoringName): the coverage pre-check below and the kana bar's plain
        // score are already reading-side aware (the JF-755 TitleTokens union /
        // ScoringTokens), so only this leg was raw.
        // JF-777: the kana bar rides the pre-check as the JF-776 B1
        // refuse-and-continue walk (the album arms' pattern): a refused pick is
        // removed from the result list and the FuzzyMatch re-runs on the
        // remainder, so a suffixed sibling listed before the exact song no
        // longer shadows it. A coverage-WITHHELD pick still falls through to
        // HandleFuzzyMiss below (the JF-526 gate's own outcome); only the BAR
        // refusal walks. The removal is also the fall-through's filter: a
        // refused bait must not ride into HandleFuzzyMiss, whose >= 90
        // auto-accept would play the very item the bar just refused (the
        // pre-fix refusal comment's doctrine), and a walked-out list is the
        // honest not-found for the same reason. The bar's query codes encode
        // once and only when armed (the encode-once idiom the codes-carried
        // overloads document).
        Func<BaseItem, double, bool>? songBar = null;
        if (kanaOrigin)
        {
            var queryCodes = DoubleMetaphone.Encode(query);
            songBar = (song, score) => Util.SongIndexSearch.PassesKanaOriginSongAcceptance(queryCodes, query.Length, song, score);
        }

        var queryTokens = KeywordMatcher.Tokenize(query, locale);
        while (true)
        {
            BaseItem? topMatch = Search.FuzzyMatch(query, deduped, i => Util.KeywordMatcher.ScoringName(i.Name), user);
            // JF-526 (JF-508 sibling): this site-level pre-check returns before
            // HandleFuzzyMiss, so the short-query full-coverage gate must be applied here
            // too; a gated miss falls into HandleFuzzyMiss below, whose Confirm mode asks
            // the yes/no "did you mean" prompt.
            if (topMatch == null || !KeywordMatcher.HasFullKeywordCoverage(queryTokens, topMatch.Name, locale))
            {
                break;
            }

            // JF-654 review round 2: the full-coverage pre-check is a song
            // auto-play on this handler (the fuzzy pass's sibling, one branch
            // later); a kana-origin query takes the same shared bar.
            var topScored = KeywordMatcher.Score(new[] { topMatch }, queryTokens, locale);
            double topScore = topScored.Count > 0 ? topScored[0].Score : 0;
            if (songBar == null || songBar(topMatch, topScore))
            {
                Logger.LogInformation("Fuzzy match hit '{Item}' — auto-playing", topMatch.Name);
                return await PlayItem(topMatch, user, session, context, request, locale, jellyfinUser).ConfigureAwait(false);
            }

            Logger.LogInformation(
                "Fuzzy match hit '{Item}' for kana-origin query '{Query}' carries no length-banded Double Metaphone collision or near-exact score, skipping and walking down the ranking (JF-654/JF-777)",
                topMatch.Name, query);
            deduped.Remove(topMatch);
        }

        if (deduped.Count == 0)
        {
            Logger.LogInformation(
                "Every full-coverage fuzzy pick for kana-origin query '{Query}' was refused by the kana bar, honest not-found (JF-654/JF-777)",
                query);
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        var (missOutcome, missResponse) = await HandleFuzzyMiss(
            query,
            deduped,
            i => Util.KeywordMatcher.ScoringName(i.Name),
            best => new List<(Guid, string)> { (best.Id, FormatWithTypeLabel(best)) },
            DisambiguationHelper.MediaTypeSong,
            locale,
            async best =>
            {
                // JF-777 code review CR1: the AutoPlay disjunct bypasses the
                // coverage gate, so a pick that only that disjunct can reach
                // (a partial-coverage survivor an AutoPlay user opted into)
                // never met the pre-check's bar; judge it here so the bar
                // protects the exemption exactly as the sibling walk inside
                // SearchItemsFuzzyAsync does. A refused pick answers the
                // honest not-found (the pre-check doctrine); a bar-passing
                // pick plays normally.
                if (songBar != null)
                {
                    var delegateScored = KeywordMatcher.Score(new[] { best }, queryTokens, locale);
                    double delegateScore = delegateScored.Count > 0 ? delegateScored[0].Score : 0;
                    if (!songBar(best, delegateScore))
                    {
                        Logger.LogInformation(
                            "AutoPlay pick '{Item}' for kana-origin query '{Query}' carries no length-banded Double Metaphone collision or near-exact score, honest not-found (JF-654/JF-777)",
                            best.Name, query);
                        return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
                    }
                }

                return await PlayItem(best, user, session, context, request, locale, jellyfinUser).ConfigureAwait(false);
            },
            user: user,
            context: context,
            request: request,
            speechSelector: i => i.Name).ConfigureAwait(false);

        if (missOutcome != FuzzyMissOutcome.NotFound)
        {
            // JF-777 gate-marker F4: the delegate may return a MediaNotFound Tell
            // from inside the auto-accept path (a bar-refused AutoPlay pick); the
            // outcome enum still reads SuggestionHandled for that shape - correlate
            // with the bar's refusal Debug line, not this label alone.
            Logger.LogInformation("Fuzzy miss outcome: {Outcome} (bar-refused not-found logs as SuggestionHandled, JF-777 F4)", missOutcome);
            return missResponse!;
        }

        var topItems = deduped.Take(3).ToList();
        var matches = topItems.Select(i => (i.Id, FormatWithTypeLabel(i), (string?)Launch.GetImageUrl(i.Id.ToString("N"), user))).ToList();
        Logger.LogInformation("Disambiguating top {Count} items: {Items}", topItems.Count, string.Join(", ", topItems.Select(i => i.Name)));
        Logger.LogDebug("SearchMedia: returning disambiguation AskFirstMatch with {Count} choices", topItems.Count);
        SkillResponse response = DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeSong, locale, context);

        return response;
    }

    /// <summary>
    /// Runs the unified search term over the playable kinds. Unrestricted users get a
    /// single query over <see cref="_playableTypes"/>. Restricted users get two: the
    /// library-scoped kinds carry the TopParentIds filter, while the out-of-library
    /// kinds (playlists) run as a sibling query whose all-exempt kind set makes
    /// <see cref="Util.LibraryFilter.ApplyLibraryFilter(InternalItemsQuery, Guid[], bool)"/>
    /// skip the filter; in one mixed query the filter would drop them (JF-456, GH
    /// #22 residual). The sibling
    /// runs only when the scoped query came back SHORT of its Limit: a full scoped
    /// page cannot be improved by playlist rows, which the union cap below would
    /// discard anyway, and skipping saves one DB roundtrip per attempt on the
    /// ASR-variant miss paths inside the 8s window (code-review round 2 item 3).
    /// Both queries live inside the ASR-variant wrapper, so a playlist-only hit on
    /// the original term stops the variant retry.
    /// </summary>
    /// <param name="searchTerm">The search term (original or ASR variant).</param>
    /// <param name="jellyfinUser">The Jellyfin user (for query scoping).</param>
    /// <param name="topParentIds">Library scope resolved once per request, or null when unrestricted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The combined results of both scopes, re-sorted and capped at the query Limit.</returns>
    private async Task<IReadOnlyList<BaseItem>> SearchPlayableKindsAsync(
        string searchTerm,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Guid[]? topParentIds,
        CancellationToken cancellationToken)
    {
        int limit = Plugin.Instance?.Configuration?.MaxSearchResults ?? 20;

        InternalItemsQuery BuildQuery(BaseItemKind[] types) => new()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = searchTerm,
            IncludeItemTypes = types,
            Limit = limit,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };

        async Task<IReadOnlyList<BaseItem>> RunAsync(InternalItemsQuery q, string label) =>
            await RetryAsync(() => _libraryManager.GetItemList(q), label, cancellationToken).ConfigureAwait(false);

        // One KindScopes resolution for both branches: the sibling scope is non-null
        // exactly under the restriction, so the unrestricted branch uses only the
        // primary (unified) scope.
        (BaseItemKind[] primaryKinds, BaseItemKind[]? siblingKinds) = KindScopes(topParentIds != null);

        if (topParentIds == null)
        {
            // Unrestricted: one unified query over all playable kinds (Playlist is
            // always content-access-allowed, so the array is never empty here). A
            // null scope means ApplyLibraryFilter would be a no-op; nothing to apply.
            var unifiedQuery = BuildQuery(FilterByContentAccess(primaryKinds));
            return await RunAsync(unifiedQuery, "UnifiedSearch").ConfigureAwait(false);
        }

        // Restricted: the out-of-library kinds run as a sibling query the kind-aware
        // pre-resolved ApplyLibraryFilter leaves unfiltered. Sequential on purpose:
        // RetryAsync executes the query synchronously on this thread, so WhenAll
        // would add thread hops without parallelism (code-review F2).
        var scopedTypes = FilterByContentAccess(primaryKinds);
        IReadOnlyList<BaseItem> scoped = Array.Empty<BaseItem>();
        if (scopedTypes.Length > 0)
        {
            var scopedQuery = BuildQuery(scopedTypes);
            Util.LibraryFilter.ApplyLibraryFilter(scopedQuery, topParentIds);
            scoped = await RunAsync(scopedQuery, "UnifiedSearch").ConfigureAwait(false);
        }

        // Saturation skip (code-review round 2 item 3): only query the sibling when
        // the scoped page came back short of its Limit. scopedTypes.Length == 0
        // leaves scoped empty (0 < limit), so the sibling still runs when it is the
        // only permissible query (code-review F3).
        if (scoped.Count < limit)
        {
            // An EMPTY IncludeItemTypes means "all kinds" to Jellyfin, so when every
            // library-scoped kind is disabled by content access the sibling is the
            // only permissible query; issuing the scoped query would bypass the
            // content gating (code-review F3).
            var outOfLibraryQuery = BuildQuery(FilterByContentAccess(siblingKinds!));
            Util.LibraryFilter.ApplyLibraryFilter(outOfLibraryQuery, topParentIds);
            IReadOnlyList<BaseItem> outOfLibrary = await RunAsync(outOfLibraryQuery, "UnifiedSearchOutOfLibrary").ConfigureAwait(false);

            // Re-sort the union (client-side approximation of the server-side
            // SortName ordering, via Name) so the combined list is not block-ordered
            // and restricted users see the same candidate order as unrestricted
            // ones (code-review F5), then re-cap at the same Limit so both user
            // classes share first-pick semantics: without the cap a restricted user
            // could see up to 2x Limit candidates (code-review F8).
            // Divergence, deliberate and documented: client-side sorting by
            // BaseItem.SortName is NOT available. The getter reads the server's
            // static BaseItem.ConfigurationManager (SortRemoveWords/chunks) to
            // rebuild the value, which is unset in off-host contexts and config-
            // dependent in-process; Name is the stable projection every returned
            // item carries. Server-side both queries order by the SortName column,
            // which this approximates.
            return scoped
                .Concat(outOfLibrary)
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        return scoped;
    }

    /// <summary>
    /// JF-506: last-tier song-title recovery on the not-found path. Consults the
    /// in-memory n-gram song index (exact bigram lookup, then the phonetic stage,
    /// the SongIndexSearch chain shared with PlaySong/FindSong) because the fuzzy
    /// pass above scans only the first 500 rows of the playable kinds and cannot
    /// see most of a large song catalog. Bounded and self-protecting:
    /// a null/disabled index returns empty (the extension's contract), a WARMING
    /// index throws from the choke point and is caught here (this is an
    /// opportunistic fallback, not a title-only path with an entry gate: a unified
    /// search must not degrade to a warming refusal), and music-disabled configs
    /// hard-zero via FilterByContentAccess before the index is touched.
    /// </summary>
    /// <param name="query">The raw query slot text.</param>
    /// <param name="locale">The request locale (tokenizer).</param>
    /// <param name="topParentIds">The library scope resolved once in HandleAsync.</param>
    /// <returns>Scored song matches best-first, capped at MaxSearchResults; empty when nothing matched.</returns>
    private IReadOnlyList<BaseItem> TrySongTitleRetry(string query, string locale, Guid[]? topParentIds, bool kanaOrigin)
    {
        // JF-466 contract: an empty FilterByContentAccess result is a hard zero, never
        // a "no type filter" query. A music-disabled user must not get song results.
        if (FilterByContentAccess(new[] { BaseItemKind.Audio }).Length == 0)
        {
            return Array.Empty<BaseItem>();
        }

        string[] keywordTokens = KeywordMatcher.Tokenize(query, locale);
        if (keywordTokens.Length == 0)
        {
            return Array.Empty<BaseItem>();
        }

        List<(BaseItem Item, double Score)> scored;
        try
        {
            scored = _songNgramIndex.SearchWithPhoneticFallback(
                keywordTokens, locale, topParentIds, _config.PhoneticSongSearchEnabled);
        }
        catch (SkillWarmingUpException)
        {
            Logger.LogDebug("Song-title retry skipped for '{Query}': song index warming", query);
            return Array.Empty<BaseItem>();
        }

        // JF-654: the shared song-side kana bar before the retry's hits feed the
        // result flow (whose single-result branch auto-plays).
        scored = Util.SongIndexSearch.ApplyKanaOriginBar(scored, query, kanaOrigin);

        int cap = Plugin.Instance?.Configuration?.MaxSearchResults ?? 20;
        return scored.Take(cap).Select(s => s.Item).ToList();
    }

    /// <summary>
    /// JF-654 review round 2: the shared song-side kana bar applied to the fuzzy
    /// pass's pick, since JF-781 as the acceptance predicate of the walk inside
    /// SearchItemsFuzzyAsync (the JF-776 B1 refuse-and-continue idiom; the shadow
    /// the former post-pick refuse-and-stop left behind is documented at the
    /// call site). The fuzzy score rides the PartialRatio scale the bar's plain
    /// leg cannot trust (the JF-652 review: plain 91-99 there proves no
    /// collision), so the pick is re-scored through the same KeywordMatcher
    /// chain the &gt;= 95 leg is calibrated on (ScoreWithPhoneticFallback, the
    /// phonetic-stage aware form, deliberately NOT the plain Score the pre-check
    /// bar uses), and a pick that chain does not even admit is a refusal (the
    /// pre-conversion gate's semantics, kept). The tokens and codes are
    /// caller-hoisted per the encode-once rule (the walk evaluates the bar once
    /// per refused winner). False removes the pick from the walk's candidate set
    /// and the pick re-runs on the remainder; a fully refused scan walks out
    /// (null) and the caller falls through to the gated song-title retry. The
    /// walk's own refusal line is the one-line triage surface (this predicate
    /// deliberately logs nothing; its semantics live in this doc, the shared
    /// walk's convention).
    /// </summary>
    /// <param name="item">The fuzzy walk's current best pick.</param>
    /// <param name="romanizedQueryTokens">The romanized query's pre-tokenized
    /// keywords (hoisted once per request by the caller).</param>
    /// <param name="queryCodes">The romanized query's Double Metaphone codes
    /// (hoisted once per request by the caller).</param>
    /// <param name="romanizedQueryLength">The romanized query's length (the band input).</param>
    /// <param name="locale">The request locale (title-side tokenization).</param>
    /// <returns>True when the pick may feed the result flow's auto-play.</returns>
    private bool PassesKanaSongGate(
        BaseItem item,
        string[] romanizedQueryTokens,
        (string Primary, string? Alternate) queryCodes,
        int romanizedQueryLength,
        string locale)
    {
        var scored = KeywordMatcher.ScoreWithPhoneticFallback(
            new[] { item },
            romanizedQueryTokens,
            locale,
            _config.PhoneticSongSearchEnabled);
        if (scored.Count == 0)
        {
            return false;
        }

        return Util.SongIndexSearch.PassesKanaOriginSongAcceptance(queryCodes, romanizedQueryLength, item, scored[0].Score);
    }

    private async Task<IReadOnlyList<BaseItem>> SearchByArtistNameAsync(
        string query,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        Guid[]? topParentIds,
        string locale,
        bool kanaOrigin,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
            query, user, _libraryManager, _artistIndex, Logger,
            (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "ArtistLookup", ct),
            locale, cancellationToken).ConfigureAwait(false);

        if (artists.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        // JF-654 live-battery round 3: the artist arm's acceptance. JF-652's
        // decision points never covered SearchMedia (live a7d42b09: tier-4
        // InMemoryFuzzyAll plain-fuzzy-matched 'Sator' for 'bitoruzu', the
        // artist items concat'd to a single deduplicated result and auto-played).
        // The shared JF-471 decision-point predicate (its kana leg is the JF-652
        // artist bar: user threshold AND a real DM code collision) gates the
        // resolved artist; a refusal is the honest miss, so the flow continues
        // to the gated fuzzy pass and song-title retry below.
        if (kanaOrigin && !CrossMedia.PassesArtistMatchAcceptance(artists[0], query, user, _artistIndex, out _, kanaOrigin: true))
        {
            Logger.LogInformation(
                "Artist fallback for '{Query}' matched '{Artist}' without kana-origin acceptance evidence (no Double Metaphone collision), treating as a miss (JF-654)",
                query, artists[0].Name);
            return Array.Empty<BaseItem>();
        }

        var artistItemsQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            // JF-358: ArtistIds filters via IncludeItemTypes only; the MediaTypes term
            // here was redundant at best and the broken combination at worst.
            ArtistIds = new[] { artists[0].Id },
            IncludeItemTypes = FilterByContentAccess(_playableTypes),
            Limit = Plugin.Instance?.Configuration?.MaxSearchResults ?? 20,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        // Pre-resolved scope hoisted in HandleAsync (code-review F6).
        Util.LibraryFilter.ApplyLibraryFilter(artistItemsQuery, topParentIds);

        return await RetryAsync(
            () => _libraryManager.GetItemList(artistItemsQuery),
            "ArtistItemsLookup",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<SkillResponse> PlayItem(
        BaseItem item, Entities.User user, SessionInfo session, Context context, Request request, string locale, Jellyfin.Database.Implementations.Entities.User? jellyfinUser)
    {
        string itemId = item.Id.ToString();

        // JF-699 item 5: the session writes follow the launch build in both arms
        // (the ordering policy lives on EnsureStreamTokenDeliverable; a refusal
        // must not leave a phantom now-playing).
        SkillResponse response;
        if (IsVideoType(item))
        {
            // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
            // JF-538: progressive-announce variant (JF-501 contract), reached on every
            // PlayItem entry point (single result, site-level fuzzy pre-check, and the
            // HandleFuzzyMiss auto-play delegate, which is why that delegate is async):
            // the video-launch announce is spoken as an awaited progressive response
            // and the final launch response carries the directive only. Exception: the
            // fuzzy QUALIFIER band (score below ContainmentScore) makes HandleFuzzyMiss
            // speak its closest-match qualifier progressively too, keeping the same
            // directive-only contract there (JF-538 review finding).
            // JF-587: the episode screenless degrade (audio-only on Dots); movies keep
            // the capability refusal inside the builder. Fresh play: no resume ticks.
            response = await Launch.BuildEpisodeLaunchResponseAsync(
                context,
                request,
                locale,
                item,
                user,
                Launch.GetVideoAppLaunchUrl(item, user),
                resumeTicks: 0,
                Launch.BuildVideoLaunchSpeech(item, locale, _userDataManager, jellyfinUser, Launch.GetAnnounceNowPlaying(user))).ConfigureAwait(false);
        }
        else
        {
            response = Launch.BuildAudioPlayerResponse(
                global::Alexa.NET.Response.Directive.PlayBehavior.ReplaceAll,
                Launch.GetStreamUrl(itemId, user),
                itemId,
                item,
                user,
                context,
                libraryManager: _libraryManager);
        }

        // JF-714/JF-718: the now-playing writes ride a DELIVERED launch; the
        // rationale lives on AttachNowPlayingIfLaunched (the video arm can answer
        // the capability Tell on a screenless device).
        PlaybackLaunchBuilder.AttachNowPlayingIfLaunched(response, session, item);
        return response;
    }

    private static bool IsVideoType(BaseItem item)
    {
        return item is global::MediaBrowser.Controller.Entities.Movies.Movie
            || (item is global::MediaBrowser.Controller.Entities.TV.Episode ep
                && ep.MediaType == MediaType.Video);
    }

    private static string GetTypeName(BaseItem item)
    {
        if (item is Audio)
        {
            return "song";
        }

        if (item is MusicAlbum)
        {
            return "album";
        }

        if (item is global::MediaBrowser.Controller.Entities.Movies.Movie)
        {
            return "movie";
        }

        if (item is global::MediaBrowser.Controller.Entities.TV.Episode)
        {
            return "episode";
        }

        if (item is global::MediaBrowser.Controller.Entities.TV.Series series)
        {
            return series.MediaType == MediaType.Audio ? "podcast" : "series";
        }

        if (AudiobookItems.IsAudioBook(item))
        {
            return "audiobook";
        }

        var runtimeName = item.GetType().Name;
        if (runtimeName.Contains("Playlist", StringComparison.Ordinal))
        {
            return "playlist";
        }

        return "media";
    }

    private static string FormatWithTypeLabel(BaseItem item)
    {
        return $"{GetTypeName(item)}: {item.Name}";
    }
}
