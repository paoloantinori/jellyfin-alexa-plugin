using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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

        if (genres.Length > 0)
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

            IReadOnlyList<BaseItem> byGenre = await RetryAsync(
                () => libraryManager.GetItemList(genreQuery),
                "GetRadioGenreTracks",
                cancellationToken).ConfigureAwait(false);

            foreach (BaseItem item in byGenre)
            {
                if (seen.Add(item.Id))
                {
                    allResults.Add(item);
                }
            }
        }

        return allResults;
    }

    /// <summary>
    /// Retry twin copied from BaseHandler (JF-315 batch 10) so the moved body keeps
    /// its verbatim call shape; consolidation of the RetryAsync twins across the
    /// collaborators is tracked as JF-572.
    /// </summary>
    private Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
    {
        return RetryHelper.ExecuteWithRetryAsync(operation, _logger, operationName, cancellationToken: cancellationToken, timeoutMs: _requestTimeoutMs);
    }
}
