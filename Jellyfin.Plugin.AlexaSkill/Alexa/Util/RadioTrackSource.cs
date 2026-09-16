using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Music;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The radio-track source (JF-315 batch 10, census cluster H): the item-seeded and
/// genre-seeded similar-track queries the radio queues are built from
/// (<see cref="FindRadioTracksAsync"/> / <see cref="FindRadioTracksByGenreAsync"/>),
/// moved verbatim from BaseHandler. COMPOSITION, not per-handler injection (the
/// PlaybackLaunchBuilder batch-4 precedent): BaseHandler constructs one instance
/// as the inherited <c>Radio</c> property so the handler ctors stay untouched;
/// callers are PlayRadioIntentHandler and PlaybackNearlyFinishedEventHandler.
/// Stateless (logger + the passed request budget). Carries a private RetryAsync
/// twin so the moved body keeps its verbatim call shape (the SearchService
/// batch-6 precedent; consolidation of the RetryAsync twins is tracked as JF-572).
/// </summary>
public sealed class RadioTrackSource
{
    private readonly ILogger _logger;
    private readonly int _requestTimeoutMs;

    /// <summary>
    /// Initializes a new instance of the <see cref="RadioTrackSource"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="requestTimeoutMs">The request timeout budget in milliseconds
    /// (composition-passed; the single-sourced 6s controller cancellation the whole
    /// request path shares, see JF-572 for the const's true-home consolidation).</param>
    public RadioTrackSource(ILogger logger, int requestTimeoutMs)
    {
        _logger = logger;
        _requestTimeoutMs = requestTimeoutMs;
    }

    /// <summary>
    /// Find tracks with genres matching the given audio item.
    /// Returns deduplicated results excluding the current item.
    /// </summary>
    /// <param name="current">The current audio item to match genres from.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A list of similar tracks.</returns>
    public async Task<IReadOnlyList<BaseItem>> FindRadioTracksAsync(
        MediaBrowser.Controller.Entities.Audio.Audio current,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
        => await FindRadioTracksByGenreAsync(
            current.Genres ?? Array.Empty<string>(),
            jellyfinUser,
            user,
            libraryManager,
            cancellationToken,
            current.Id).ConfigureAwait(false);

    /// <summary>
    /// Genre-seeded variant of <see cref="FindRadioTracksAsync"/> (JF-474): the identical
    /// radio-track query, seeded by genre WORDS captured from the station elicit instead
    /// of a playing item's Genres array. The Genres filter matches the server's cleaned
    /// genre names exactly (Jellyfin 10.11 BaseItemRepository: ItemValue CleanValue
    /// equality), so a spoken "jazz" matches the genre "Jazz" while a non-genre word
    /// matches nothing and the caller falls through to its not-found.
    /// </summary>
    /// <param name="genres">The genre names to seed the query from.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="user">The plugin user for library filtering.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <param name="excludeId">Optional item id to exclude from the results (the seeding item).</param>
    /// <returns>A deduplicated list of tracks in those genres.</returns>
    public async Task<IReadOnlyList<BaseItem>> FindRadioTracksByGenreAsync(
        string[] genres,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken,
        Guid? excludeId = null)
    {
        var allResults = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        if (excludeId.HasValue)
        {
            seen.Add(excludeId.Value);
        }

        if (genres.Length == 0)
        {
            return allResults;
        }

        void AddAll(IEnumerable<BaseItem> items)
        {
            foreach (BaseItem item in items)
            {
                if (seen.Add(item.Id))
                {
                    allResults.Add(item);
                }
            }
        }

        var budget = Stopwatch.StartNew();
        IReadOnlyList<BaseItem> byGenre = await QueryGenresAsync(
            genres, jellyfinUser, user, libraryManager,
            timeoutMs: _requestTimeoutMs, cancellationToken: cancellationToken).ConfigureAwait(false);
        AddAll(byGenre);

        // JF-576 similarity expansion, only when the primary-genre pool is thin
        // (<see cref="GenreSimilarityMap.ExpansionThreshold"/>): rich libraries keep
        // the exact single-genre pool and never pay the extra query (at most ONE
        // extra GetItemList per radio build). The seed genres lead the EXPANDED
        // FILTER array (result order is still Jellyfin's Random sort).
        if (allResults.Count < GenreSimilarityMap.ExpansionThreshold)
        {
            // ONE shared Alexa budget across BOTH queries: each RetryAsync call
            // starts its own stopwatch, so two full budgets back-to-back could
            // reach ~12s and blow the ~8s response window (review finding). The
            // expansion gets only the time the primary query left unspent, and
            // never fires at all when that remainder is under its minimum
            // operation estimate.
            int remainingMs = _requestTimeoutMs - (int)budget.ElapsedMilliseconds;
            if (remainingMs > RetryHelper.DefaultMinOperationMs)
            {
                string[] expanded = GenreSimilarityMap.ExpandGenres(genres);
                if (expanded.Length > genres.Length)
                {
                    IReadOnlyList<BaseItem> bySimilarGenre = await QueryGenresAsync(
                        expanded, jellyfinUser, user, libraryManager,
                        timeoutMs: remainingMs, cancellationToken: cancellationToken).ConfigureAwait(false);
                    AddAll(bySimilarGenre);
                }
            }
        }

        return allResults;
    }

    /// <summary>
    /// Runs the single genre-membership GetItemList query (Limit 50, Random order)
    /// for the given genre set.
    /// </summary>
    private async Task<IReadOnlyList<BaseItem>> QueryGenresAsync(
        string[] genres,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var genreQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            Genres = genres,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Limit = 50,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        LibraryFilter.ApplyLibraryFilter(genreQuery, user, libraryManager, _logger);

        return await RetryAsync(
            () => libraryManager.GetItemList(genreQuery),
            "GetRadioGenreTracks",
            timeoutMs: timeoutMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Retry twin copied from BaseHandler (JF-315 batch 10) so the moved body keeps
    /// its verbatim call shape; consolidation of the RetryAsync twins across the
    /// collaborators is tracked as JF-572.
    /// </summary>
    private Task<T> RetryAsync<T>(Func<T> operation, string operationName, int timeoutMs = 0, CancellationToken cancellationToken = default)
    {
        return RetryHelper.ExecuteWithRetryAsync(operation, _logger, operationName, cancellationToken: cancellationToken, timeoutMs: timeoutMs > 0 ? timeoutMs : _requestTimeoutMs);
    }
}
