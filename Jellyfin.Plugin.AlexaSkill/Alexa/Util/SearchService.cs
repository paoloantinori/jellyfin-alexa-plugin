using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The JF-315 search collaborator (cluster E, extracted from BaseHandler batch 6):
/// the library-search and fuzzy-recall machinery: the NRE-safe query executor, the
/// ASR compound-word fallback wrapper, the plain and
/// phonetic fuzzy matchers, the shared artist-songs query (the JF-382 "no third
/// copy" home), the zero-result fuzzy fallback, and the search response-mode
/// resolution.
/// RECALL LAYER ONLY. The auto-play DECISION predicates stay at their decision
/// points, per the fuzzy-recall-vs-judgment-layers memory (JF-408: two relocation
/// attempts that pushed precision toward the recall layer were REVERTED):
/// <see cref="Handler.BaseHandler.HandleFuzzyMiss"/> (the big multi-turn
/// disambiguation/auto-play decision block, with the JF-508/JF-526 full-keyword
/// coverage auto-accept predicate) deliberately REMAINED on BaseHandler, and the
/// JF-420-family gates (FairComparisonScore, AlternativeFullNameThreshold, the
/// JF-377 downgrade) remain on the handlers that own those decisions. This class
/// must never grow an auto-play judgment; it returns candidates, the decision
/// points judge. (The rule bars NEW judgments: SearchItemsFuzzyAsync's JF-508/JF-526
/// coverage gate arrived here verbatim inside its moved body, a pre-existing decision
/// at its pre-existing point; relocating it would be a JF-408-class decision, not
/// cleanup.)
/// STATELESS by construction (readonly config + logger), so the
/// singleton-handlers constraint BaseHandler documents is preserved.
/// COMPOSITION DECISION (the PlaybackLaunchBuilder precedent): BaseHandler
/// constructs one instance per handler in its own ctor and exposes it as the
/// protected-internal readonly <c>Search</c> property; handlers consume it
/// through that inherited get-only property, so the 61 handler ctors stay
/// untouched.
/// </summary>
public sealed class SearchService
{
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private readonly int _requestTimeoutMs;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchService"/> class.
    /// </summary>
    /// <param name="config">The plugin configuration (search mode defaults, ASR fix toggle).</param>
    /// <param name="logger">The logger (BaseHandler passes its own instance so moved log statements keep their pre-extraction category).</param>
    /// <param name="requestTimeoutMs">The Alexa request timeout budget in milliseconds (BaseHandler passes its own const, single-sourced there: it matches the controller's 6-second cancellation). Passed at composition, the PlaybackLaunchBuilder delegate-seam precedent, so this Util class holds no Handler-layer reference.</param>
    public SearchService(PluginConfiguration config, ILogger logger, int requestTimeoutMs)
    {
        _config = config;
        _logger = logger;
        _requestTimeoutMs = requestTimeoutMs;
    }

    /// <summary>
    /// Executes GetItemsResult with a fallback to GetItemList on NullReferenceException.
    /// Jellyfin's GetItemsResult evaluates dbQuery.Count() after applying query filters
    /// and ordering. Certain combinations (e.g. ArtistIds + PopularitySort referencing
    /// User data) cause EF Core's Count() translation to NRE. GetItemList skips the
    /// Count() step entirely.
    /// </summary>
    public QueryResult<BaseItem> SafeGetItemsResult(ILibraryManager libraryManager, InternalItemsQuery query)
    {
        try
        {
            return libraryManager.GetItemsResult(query);
        }
        catch (NullReferenceException)
        {
            // Jellyfin's GetItemsResult evaluates dbQuery.Count() after applying query
            // filters + ordering. Certain combinations (e.g. ArtistIds + PopularitySort
            // referencing User data) cause EF Core's Count() translation to NRE.
            // Fall back to GetItemList which skips the Count() step entirely.
            _logger.LogWarning("GetItemsResult NRE — falling back to GetItemList");
            IReadOnlyList<BaseItem> items = libraryManager.GetItemList(query);
            return new QueryResult<BaseItem>(query.StartIndex ?? 0, items.Count, items);
        }
    }

    /// <summary>
    /// Search using the original query first, then fall back to ASR compound-word
    /// variants if the feature is enabled and the original returned no results.
    /// Stops at the first non-empty result set.
    /// </summary>
    /// <typeparam name="T">The result item type.</typeparam>
    /// <param name="query">The original search query from ASR.</param>
    /// <param name="searchFunc">A function that executes a search for a given query string.</param>
    /// <returns>Results from the first successful search, or the original empty results.</returns>
    public async Task<IReadOnlyList<T>> SearchWithAsrFallbackAsync<T>(
        string query,
        Func<string, Task<IReadOnlyList<T>>> searchFunc,
        SearchResponseMode mode = SearchResponseMode.Thorough)
    {
        IReadOnlyList<T> results = await searchFunc(query).ConfigureAwait(false) ?? Array.Empty<T>();

        if (results.Count > 0)
        {
            return results;
        }

        if (!_config.AsrCompoundWordFixEnabled || mode == SearchResponseMode.Fast)
        {
            return results;
        }

        IReadOnlyList<string> variants = AsrVariantGenerator.GenerateAsrVariants(query);

        foreach (string variant in variants)
        {
            IReadOnlyList<T> variantResults = await searchFunc(variant).ConfigureAwait(false) ?? Array.Empty<T>();

            if (variantResults.Count > 0)
            {
                return variantResults;
            }
        }

        return results;
    }

    /// <summary>
    /// Find the best fuzzy match from a list of items when exact search fails.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="query">The search query from the user.</param>
    /// <param name="candidates">Items to match against.</param>
    /// <param name="selector">Function to extract the comparable string.</param>
    /// <param name="threshold">Minimum similarity score (0-100).</param>
    /// <returns>The best matching item, or null.</returns>
    public T? FuzzyMatch<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, Entities.User? user = null, int threshold = -1)
        where T : class
    {
        int effectiveThreshold = threshold >= 0 ? threshold : FuzzyMatcher.GetDefaultThreshold(user);
        var result = FuzzyMatcher.FindBestMatch(query, candidates, selector, effectiveThreshold);
        _logger.LogDebug("FuzzyMatch: query={Query}, best={BestMatch}, threshold={Threshold}, matched={Matched}",
            query, result != null ? selector(result) : "(null)", effectiveThreshold, result != null);
        return result;
    }

    /// <summary>
    /// Phonetic-aware fuzzy match: like <see cref="FuzzyMatch{T}"/> but prefers Double
    /// Metaphone code collisions for cross-language accent drift (e.g. "Koop" heard as
    /// "cup", both code "KP"). When codes collide AND the candidate is within a length
    /// band, the score is floored above ContainmentScore so it beats coincidental
    /// substring matches. JF-381.
    /// <para>
    /// JF-448 (review F2) contract: callers whose candidates came from the artist index
    /// MUST pass the index's pinned view (<see cref="IArtistIndex.CaptureSnapshot"/>) so
    /// the candidate list and the phonetic codes resolve from the same publish; passing
    /// the live service re-reads the snapshot field per lookup and a mid-search refresh
    /// can null a code (the cross-snapshot window this fixes).
    /// </para>
    /// </summary>
    /// <typeparam name="T">The candidate item type.</typeparam>
    public T? FuzzyMatchPhonetic<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, Func<T, Guid> idSelector, IArtistIndex? artistIndex, Entities.User? user = null, int threshold = -1)
        where T : class
    {
        if (artistIndex == null)
        {
            return FuzzyMatch(query, candidates, selector, user, threshold);
        }

        int effectiveThreshold = threshold >= 0 ? threshold : FuzzyMatcher.GetDefaultThreshold(user);
        var result = FuzzyMatcher.FindBestMatch(
            query,
            candidates,
            selector,
            idSelector,
            id => artistIndex.TryGetPhoneticCode(id, out var codes) ? codes : null,
            effectiveThreshold);

        _logger.LogDebug("FuzzyMatchPhonetic: query={Query}, best={BestMatch}, threshold={Threshold}, matched={Matched}",
            query, result != null ? selector(result) : "(null)", effectiveThreshold, result != null);
        return result;
    }

    /// <summary>
    /// Fetches an artist's (or artists') songs with the shared query shape: ArtistIds +
    /// IncludeItemTypes=Audio (JF-358: never MediaTypes=Audio) + library filter + retry.
    /// Single helper for all artist-scoped song fetches (FindSong's keyword search,
    /// PlaySong's title fallback), so the query shape stays consistent (JF-382 rule:
    /// no third copy of the artist-search path). Pass <paramref name="nameContains"/>
    /// for a server-side substring pre-filter, or leave it null for the unfiltered
    /// (keyword-matcher-scored) form; <paramref name="limit"/> bounds the fetch for
    /// aggregate artists ("Various Artists" can hold 10k+ tracks).
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user (for query scoping).</param>
    /// <param name="user">The plugin user (for the library filter).</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="artistIds">The artist IDs to scope to.</param>
    /// <param name="retryLabel">Label for RetryAsync logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="nameContains">Optional server-side NameContains pre-filter.</param>
    /// <param name="limit">Optional row cap (e.g. 500, like SearchItemsFuzzyAsync).</param>
    /// <returns>The artist's songs matching the query.</returns>
    public async Task<IReadOnlyList<BaseItem>> GetArtistSongsAsync(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        Guid[] artistIds,
        string retryLabel,
        CancellationToken cancellationToken,
        string? nameContains = null,
        int? limit = null)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ArtistIds = artistIds,
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        };
        if (nameContains != null)
        {
            query.NameContains = nameContains;
        }

        if (limit.HasValue)
        {
            query.Limit = limit.Value;
        }

        LibraryFilter.ApplyLibraryFilter(query, user, libraryManager, _logger);

        return await RetryAsync(() => libraryManager.GetItemList(query), retryLabel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bridges ASR accent/transcription variants (e.g. "caffè" vs "Cafe") that
    /// Jellyfin's search index doesn't normalize. Cold path only (exact miss).
    /// JF-337.
    /// </summary>
    /// <param name="query">The user-spoken name (slot value).</param>
    /// <param name="jellyfinUser">The Jellyfin user (for query scoping).</param>
    /// <param name="user">The plugin user (for threshold + library filter).</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="itemTypes">The item types to search (e.g. Audio, MusicAlbum). Queries whose kinds are ALL out-of-library skip the TopParentIds filter (<see cref="LibraryFilter.IsOutOfLibraryKind"/>, JF-456).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="operationLabel">Label for logging.</param>
    /// <param name="locale">The request locale, for the JF-508/JF-526 short-query coverage gate on the match return.</param>
    /// <returns>The best match + score, or null if nothing above threshold.</returns>
    public async Task<(BaseItem Item, int Score)?> SearchItemsFuzzyAsync(
        string query,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        BaseItemKind[] itemTypes,
        CancellationToken cancellationToken,
        string operationLabel = "FuzzyFallback",
        Guid[]? artistIds = null,
        int minQueryLength = 3,
        MediaType[]? mediaTypes = null,
        string locale = "en-US")
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < minQueryLength)
        {
            return null;
        }

        var fallbackQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = itemTypes,
            DtoOptions = new DtoOptions(true),
            Limit = 500
        };
        if (artistIds is { Length: > 0 })
        {
            fallbackQuery.ArtistIds = artistIds;
        }

        if (mediaTypes is { Length: > 0 })
        {
            fallbackQuery.MediaTypes = mediaTypes;
        }

        LibraryFilter.ApplyLibraryFilter(fallbackQuery, user, libraryManager, _logger);

        IReadOnlyList<BaseItem> allItems = await RetryAsync(
            () => libraryManager.GetItemList(fallbackQuery),
            operationLabel,
            cancellationToken).ConfigureAwait(false);

        if (allItems.Count == 0)
        {
            return null;
        }

        var match = FuzzyMatcher.FindBestMatchWithScore(query, allItems, item => item.Name);
        // JF-526 (JF-508 sibling): this zero-result fallback feeds callers that
        // auto-play the returned item (PlayBook/PlayPodcast/PlayVideo/PlayPlaylist/
        // SearchMedia/SeriesFuzzyFallback), so the short-query full-coverage gate
        // applies to the match return too. A gated miss returns null, this method's
        // existing below-threshold outcome: callers speak their own not-found instead
        // of auto-playing a partial-coverage pick ("soul coffee" -> "Starfish & Coffee").
        if (match.HasValue && match.Value.Score >= FuzzyMatcher.GetDefaultThreshold(user))
        {
            // AutoPlay users are exempt from the coverage gate (they asked to never
            // be prompted): the same exemption HandleFuzzyMiss's AutoPlay disjunct
            // and PlayAlbum's guard apply (JF-526 review F1 - policy parity).
            if (KeywordMatcher.HasFullKeywordCoverage(KeywordMatcher.Tokenize(query, locale), match.Value.Item.Name, locale)
                || (user?.FuzzyMatchBehavior ?? FuzzyMatchBehavior.Confirm) == FuzzyMatchBehavior.AutoPlay)
            {
                _logger.LogInformation(
                    "{Op}: fuzzy fallback matched '{Name}' score={Score} for query='{Query}'",
                    operationLabel, match.Value.Item.Name, match.Value.Score, query);
                return match;
            }

            // JF-526: the score bar was crossed but the gate withheld the auto-play
            // (partial keyword coverage on a short query). Logged so triage can tell
            // a withheld match from a below-threshold one (the JF-508/corr=269e622d class).
            _logger.LogDebug(
                "{Op}: coverage gate withheld partial-coverage match '{Name}' score={Score} for query='{Query}'",
                operationLabel, match.Value.Item.Name, match.Value.Score, query);
        }

        return null;
    }

    /// <summary>
    /// Gets the effective search response mode for a user, falling back to the global default.
    /// Per-user setting (when explicitly set, i.e. non-null) takes precedence.
    /// </summary>
    public SearchResponseMode GetSearchResponseMode(Entities.User? user)
    {
        if (user?.SearchResponseMode.HasValue == true)
        {
            _logger.LogDebug("SearchResponseMode: user={UserId} mode={Mode} source=PerUser", user.Id, user.SearchResponseMode.Value);
            return user.SearchResponseMode.Value;
        }

        _logger.LogDebug("SearchResponseMode: user={UserId} mode={Mode} source=GlobalDefault", user?.Id, _config.DefaultSearchResponseMode);
        return _config.DefaultSearchResponseMode;
    }

    /// <summary>
    /// Execute a synchronous Jellyfin API call with retry logic and exponential backoff.
    /// Copied, not moved (JF-315 batch 6): BaseHandler retains its own RetryAsync for
    /// its remaining callers, and the moved members call this one by their original
    /// name so their bodies keep the pre-extraction call shape (modulo the batch's
    /// declared receiver swaps). The budget is the composition-passed
    /// <c>_requestTimeoutMs</c> field (BaseHandler's const, single-sourced
    /// there as the 6s controller cancellation the whole request path shares).
    /// </summary>
    private Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
    {
        return RetryHelper.ExecuteWithRetryAsync(operation, _logger, operationName, cancellationToken: cancellationToken, timeoutMs: _requestTimeoutMs);
    }
}
