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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// The JF-315 TV next-up collaborator (census cluster K's TV trio, extracted from
/// BaseHandler batch 11): the shared series-by-name resolution
/// (<see cref="ResolveSeriesForPlaybackAsync"/>), the Jellyfin NextUp query core
/// (<see cref="GetNextUpEpisodesAsync"/>), and the next-up episode launch
/// (<see cref="PlayNextUpEpisodeAsync"/> with the JF-324 latest-episode fallback
/// and the resume-aware announce). Consumed by PlayEpisodeIntentHandler and
/// PlayNextEpisodeIntentHandler, the two handlers whose payoff is TV episodes;
/// no other handler touches it, which is why it left the base class.
/// NAMESPACE PLACEMENT (the CrossMediaFallback/AlbumPlayService/ProgressReporter
/// precedent, not the Util home of SearchService/PlaybackLaunchBuilder): the
/// series resolution gates through <see cref="BaseHandler.FilterByContentAccess"/>,
/// which lives in THIS namespace; no Util file references the Handler namespace
/// and this extraction keeps it that way.
/// COMPOSITION DECISION (the SearchService/PlaybackLaunchBuilder precedent):
/// BaseHandler constructs one instance per handler in its own ctor and exposes it
/// as the protected-internal readonly <c>TvNextUp</c> property; handlers consume
/// it through that inherited get-only property, so the handler ctors stay
/// untouched. STATELESS by construction (readonly logger + the two composed
/// collaborators + the composition-passed request budget), so the
/// singleton-handlers constraint BaseHandler documents is preserved.
/// </summary>
public sealed class TvNextUpService
{
    private readonly ILogger _logger;
    private readonly SearchService _search;
    private readonly PlaybackLaunchBuilder _launch;
    private readonly int _requestTimeoutMs;

    /// <summary>
    /// Initializes a new instance of the <see cref="TvNextUpService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="search">The search collaborator (the series fuzzy fallback).</param>
    /// <param name="launch">The playback-launch collaborator (URL building, the launch response, the announce speech).</param>
    /// <param name="requestTimeoutMs">The Alexa request timeout budget in milliseconds (composition-passed <see cref="RetryHelper.AlexaRequestTimeoutMs"/>, the batch-6 precedent; single-sourced on RetryHelper since JF-572).</param>
    public TvNextUpService(ILogger logger, SearchService search, PlaybackLaunchBuilder launch, int requestTimeoutMs)
    {
        _logger = logger;
        _search = search;
        _launch = launch;
        _requestTimeoutMs = requestTimeoutMs;
    }

    /// <summary>
    /// Resolves a series by spoken name for playback (JF-324): content-access-gated
    /// SearchTerm query with the per-user library filter, then the shared fuzzy
    /// fallback. Shared by PlayEpisodeIntentHandler and PlayNextEpisodeIntentHandler
    /// so the explicit season+episode path and the next-up paths match series the
    /// same way.
    /// </summary>
    /// <param name="libraryManager">The library manager for the series query.</param>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library access filtering.</param>
    /// <param name="seriesName">The spoken series name.</param>
    /// <param name="locale">The request locale for error response strings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matched series, or an error response (content disabled or series not found).</returns>
    public async Task<(BaseItem? Series, SkillResponse? Error)> ResolveSeriesForPlaybackAsync(
        ILibraryManager libraryManager,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        string seriesName,
        string locale,
        CancellationToken cancellationToken)
    {
        // JF-466: an EMPTY IncludeItemTypes means "all kinds" to Jellyfin, so a
        // videos-disabled configuration must hard-zero here instead of querying.
        BaseItemKind[] seriesKinds = BaseHandler.FilterByContentAccess(new[] { BaseItemKind.Series });
        if (seriesKinds.Length == 0)
        {
            _logger.LogInformation("Series resolution skipped: no series kind allowed by configuration");
            return (null, ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", locale)));
        }

        var seriesQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = seriesName,
            IncludeItemTypes = seriesKinds,
            DtoOptions = new DtoOptions(true)
        };
        Util.LibraryFilter.ApplyLibraryFilter(seriesQuery, user, libraryManager, _logger);
        _logger.LogDebug("ResolveSeries: querying Jellyfin with searchTerm='{SeriesName}', types=Series", seriesName);
        IReadOnlyList<BaseItem> seriesList = await RetryAsync(() => libraryManager.GetItemList(seriesQuery), "GetSeries", cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("ResolveSeries: Jellyfin returned {ResultCount} series", seriesList.Count);

        if (seriesList.Count > 0)
        {
            return (seriesList[0], null);
        }

        var fuzzy = await _search.SearchItemsFuzzyAsync(seriesName, jellyfinUser, user, libraryManager, seriesKinds, cancellationToken, "SeriesFuzzyFallback", locale: locale).ConfigureAwait(false);
        if (fuzzy != null)
        {
            return (fuzzy.Value.Item, null);
        }

        return (null, ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSeries", locale, seriesName)));
    }

    /// <summary>
    /// JF-324 shared NextUp query core: Jellyfin's per-user next-unwatched episodes of
    /// a series via <c>ITVSeriesManager.GetNextUp</c> (EnableResumable so an in-progress
    /// episode counts as the next one). INTENT-PATH core only (single candidate,
    /// <see cref="PlayNextUpEpisodeAsync"/>): PlaybackNearlyFinishedEventHandler's
    /// episode auto-advance deliberately does NOT use NextUp, because a
    /// SeriesId-scoped GetNextUp returns at most one item on Jellyfin 10.11 and on
    /// the event path that item is always the finishing episode itself; the event
    /// path queries the series' unplayed episodes directly instead (C1).
    /// Content and library gating stay in the caller: the intent path gates at series
    /// resolution (<see cref="ResolveSeriesForPlaybackAsync"/>).
    /// </summary>
    /// <param name="tvSeriesManager">The Jellyfin TV series manager (NextUp source).</param>
    /// <param name="jellyfinUser">The Jellyfin user (per-user watched state).</param>
    /// <param name="seriesId">The series to advance within.</param>
    /// <param name="seriesName">The series name (logging only).</param>
    /// <param name="limit">How many next-up candidates to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The next-up episodes in Jellyfin's order (possibly empty, never null).</returns>
    public async Task<IReadOnlyList<BaseItem>> GetNextUpEpisodesAsync(
        ITVSeriesManager tvSeriesManager,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Guid seriesId,
        string? seriesName,
        int limit,
        CancellationToken cancellationToken)
    {
        var nextUpQuery = new NextUpQuery
        {
            User = jellyfinUser,
            SeriesId = seriesId,
            Limit = limit,
            EnableResumable = true
        };
        _logger.LogDebug("NextUp: querying NextUp for seriesId={SeriesId}, enableResumable=true, limit={Limit}", seriesId, limit);
        var nextUpSw = System.Diagnostics.Stopwatch.StartNew();
        QueryResult<BaseItem> nextUp = await RetryAsync(
            () => tvSeriesManager.GetNextUp(nextUpQuery, new DtoOptions(true)),
            "GetNextUp",
            cancellationToken).ConfigureAwait(false);
        nextUpSw.Stop();
        // Stage timing (device session 2026-09-06: four requests spent 6-26s between
        // these two lines while a remux and the startup catalog sync ran; controlled
        // re-runs under the same encode load measured 91-131ms, so the spikes were a
        // transient that left no trace. This line makes the next occurrence readable
        // from the logs instead of inferred).
        _logger.LogInformation(
            "NextUp: GetNextUp took {ElapsedMs}ms for series '{SeriesName}' ({ResultCount} results)",
            nextUpSw.ElapsedMilliseconds, seriesName, nextUp?.Items?.Count ?? 0);
        return nextUp?.Items ?? Array.Empty<BaseItem>();
    }

    /// <summary>
    /// JF-324 shared next-up episode launch: resolves the next unwatched episode of a
    /// series via Jellyfin's NextUp (per-user watched state; EnableResumable so an
    /// in-progress episode counts as the next one, which is what both "next episode"
    /// and "continue watching" mean to a viewer who stopped mid-episode), falls back to
    /// the most recently created episode when NextUp is empty (nothing unwatched left),
    /// and launches the winner via VideoApp with the resume-aware announce. Used by
    /// PlayNextEpisodeIntentHandler and PlayEpisodeIntentHandler's series-only
    /// fallback. Library and content gating happen in the caller's series resolution
    /// (<see cref="ResolveSeriesForPlaybackAsync"/>); this core only needs the
    /// already-scoped series.
    /// </summary>
    /// <param name="tvSeriesManager">The Jellyfin TV series manager (NextUp source).</param>
    /// <param name="libraryManager">The library manager (latest-episode fallback query).</param>
    /// <param name="userDataManager">The user data manager (resume-aware announce).</param>
    /// <param name="jellyfinUser">The Jellyfin user (per-user watched state).</param>
    /// <param name="user">The plugin user (stream URL + announce toggle).</param>
    /// <param name="session">The Jellyfin session (now-playing queue).</param>
    /// <param name="series">The already-resolved series item.</param>
    /// <param name="locale">The request locale for response strings.</param>
    /// <param name="context">The Alexa context (JF-505 screenless-device launch gate).</param>
    /// <param name="request">The skill request (JF-501 progressive announce vehicle).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The VideoApp launch response, or the localized NoNextEpisode Tell when the series has no playable episode.</returns>
    public async Task<SkillResponse> PlayNextUpEpisodeAsync(
        ITVSeriesManager tvSeriesManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        BaseItem series,
        string locale,
        Context context,
        Request request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> nextUpEpisodes = await GetNextUpEpisodesAsync(
            tvSeriesManager, jellyfinUser, series.Id, series.Name, 1, cancellationToken).ConfigureAwait(false);
        BaseItem? episode = nextUpEpisodes.FirstOrDefault();
        bool latestFallback = episode == null;

        if (episode == null)
        {
            // Latest fallback (JF-324): with nothing unwatched left, serve the most
            // recently created episode (DateCreated, the same ordering PlayPodcast
            // uses for "newest episode") and announce it with the latest-episode
            // wording instead of refusing.
            var latestQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
                Limit = 1,
                DtoOptions = new DtoOptions(true)
            };
            IReadOnlyList<BaseItem> latest = await RetryAsync(
                () => libraryManager.GetItemList(latestQuery),
                "GetLatestEpisode",
                cancellationToken).ConfigureAwait(false);
            episode = latest?.FirstOrDefault();
        }

        if (episode == null)
        {
            _logger.LogDebug("NextUp: no next-up and no episodes for series '{SeriesName}' ({SeriesId})", series.Name, series.Id);
            return ResponseBuilder.Tell(ResponseStrings.Get("NoNextEpisode", locale, series.Name));
        }

        _logger.LogDebug(
            "NextUp: resolved episode '{EpisodeName}' ({EpisodeId}) for series '{SeriesName}', latestFallback={LatestFallback}",
            episode.Name, episode.Id, series.Name, latestFallback);

        session.NowPlayingQueue = new List<QueueItem> { new QueueItem { Id = episode.Id } };
        session.FullNowPlayingItem = episode;

        // A next-up episode with playback progress is a resume: the announce says so
        // (VideoApp.Launch cannot honor the offset; the position info is spoken only).
        long resumeTicks = userDataManager.GetUserData(jellyfinUser, episode)?.PlaybackPositionTicks ?? 0;
        IOutputSpeech? speech;
        if (resumeTicks > 0)
        {
            speech = _launch.BuildVideoLaunchSpeech(episode, locale, resumeTicks, _launch.GetAnnounceNowPlaying(user));
        }
        else
        {
            speech = _launch.GetAnnounceNowPlaying(user)
                ? SpeechBuilder.BuildOutputSpeech(
                    latestFallback ? "PlayingLatestEpisodeSsml" : "PlayingNextEpisodeSsml",
                    latestFallback ? "PlayingLatestEpisode" : "PlayingNextEpisode",
                    locale,
                    episode.Name)
                : null;
        }

        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        // JF-501: the announce is spoken progressively AFTER the source URL has resolved
        // (it is a call argument, so it evaluates first) and BEFORE the final launch
        // response returns, so the fast-start HLS player cannot cut it mid-sentence
        // (observed case).
        return await _launch.BuildVideoAppLaunchResponseAsync(
            context,
            request,
            locale,
            _launch.GetVideoAppLaunchUrl(episode, user),
            episode.Name,
            speech).ConfigureAwait(false);
    }

    /// <summary>Delegates to RetryHelper.ExecuteWithRequestBudgetAsync with
    /// the composition-injected request budget and this class's logger; kept as a thin alias so the moved
    /// members keep calling <c>RetryAsync</c> by name (see the entry point doc).</summary>
    private Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
        => RetryHelper.ExecuteWithRequestBudgetAsync(operation, _logger, operationName, timeoutMs: _requestTimeoutMs, cancellationToken: cancellationToken);
}
