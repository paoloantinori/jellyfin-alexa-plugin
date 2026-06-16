using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayArtistSongsIntent requests.
/// </summary>
public class PlayArtistSongsIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager? _queueManager;
    private readonly IArtistIndex? _artistIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayArtistSongsIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlayArtistSongsIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayArtistSongs, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific artist.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? musician = intentRequest.Intent.Slots?.TryGetValue("musician", out var musicianSlot) == true ? musicianSlot.Value : null;

        Logger.LogDebug("PlayArtistSongs: entered, locale={Locale}", locale);

        if (string.IsNullOrWhiteSpace(musician))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchArtistName", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var totalSw = Stopwatch.StartNew();
        var tierSw = Stopwatch.StartNew();
        int tierReached = 0;
        string searchSource = "Database";
        SearchResponseMode mode = GetSearchResponseMode(user);

        // Pre-resolve library filter once for the entire request.
        // Used by both the in-memory and database paths, plus the final artist-songs query.
        Guid[]? allowedLibraryIds = null;
        Guid[]? topParentIds = null;

        IReadOnlyList<BaseItem> artists;

        if (_artistIndex?.IsReady == true)
        {
            // In-memory search: resolve library filter once, search the pre-loaded index
            searchSource = "InMemory";
            allowedLibraryIds = GetAllowedLibraryIds(user);
            topParentIds = allowedLibraryIds != null
                ? Util.LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager, Logger)
                : null;
            var allArtists = _artistIndex.GetArtists(topParentIds);

            // Tier 1: name contains query (in-memory equivalent of SearchTerm)
            tierSw.Restart();
            artists = allArtists
                .Where(a => a.Name.Contains(musician, StringComparison.OrdinalIgnoreCase))
                .ToList();
            tierSw.Stop();
            tierReached = 1;
            Logger.LogInformation(
                "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=InMemoryContains query='{Query}'",
                tierSw.ElapsedMilliseconds, artists.Count, musician);

            if (artists.Count == 0)
            {
                if (mode == SearchResponseMode.Fast)
                {
                    // Fast mode: skip prefix tiers, go straight to fuzzy-all
                    tierSw.Restart();
                    BaseItem? fuzzy = FuzzyMatch(musician, allArtists, a => a.Name, user);
                    tierSw.Stop();
                    tierReached = 4;
                    Logger.LogInformation(
                        "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}' mode=Fast",
                        tierSw.ElapsedMilliseconds, fuzzy != null, musician);
                    if (fuzzy != null)
                    {
                        artists = new List<BaseItem> { fuzzy };
                    }
                }
                else
                {
                    // Thorough mode: run tiers 2-4 as before (in-memory tiers are sub-ms, no need to parallelize)
                    string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;

                    // Tier 2: prefix first word + fuzzy (catches ASR truncation, e.g. "soul coughin" → "Soul Coughing")
                    tierSw.Restart();
                    var prefixCandidates = allArtists
                        .Where(a => a.Name.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    BaseItem? tier2Match = FuzzyMatch(musician, prefixCandidates, a => a.Name, user);
                    tierSw.Stop();
                    tierReached = 2;
                    Logger.LogInformation(
                        "ArtistSearch: tier=2 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFirstWord query='{Query}' prefix='{Prefix}'",
                        tierSw.ElapsedMilliseconds, tier2Match != null, musician, firstWord);
                    if (tier2Match != null)
                    {
                        artists = new List<BaseItem> { tier2Match };
                    }

                    // Tier 3: prefix full query + fuzzy (e.g. "Kidz Bop" → "Kidz Bop Kids")
                    if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
                    {
                        tierSw.Restart();
                        var fullPrefixCandidates = allArtists
                            .Where(a => a.Name.StartsWith(musician, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        BaseItem? tier3Match = FuzzyMatch(musician, fullPrefixCandidates, a => a.Name, user);
                        tierSw.Stop();
                        tierReached = 3;
                        Logger.LogInformation(
                            "ArtistSearch: tier=3 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFull query='{Query}'",
                            tierSw.ElapsedMilliseconds, tier3Match != null, musician);
                        if (tier3Match != null)
                        {
                            artists = new List<BaseItem> { tier3Match };
                        }
                    }

                    // Tier 4: fuzzy match against ALL artists (catches misspellings)
                    if (artists.Count == 0)
                    {
                        tierSw.Restart();
                        BaseItem? tier4Match = FuzzyMatch(musician, allArtists, a => a.Name, user);
                        tierSw.Stop();
                        tierReached = 4;
                        Logger.LogInformation(
                            "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}'",
                            tierSw.ElapsedMilliseconds, tier4Match != null, musician);
                        if (tier4Match != null)
                        {
                            artists = new List<BaseItem> { tier4Match };
                        }
                    }
                }
            }
        }
        else
        {
            // Fallback: database queries when in-memory index is not yet loaded
            // Resolve library filter once and reuse across all fallback tiers.
            allowedLibraryIds = GetAllowedLibraryIds(user);
            topParentIds = allowedLibraryIds != null
                ? Util.LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager, Logger)
                : null;

            if (mode == SearchResponseMode.Fast)
            {
                // Fast mode: single SearchTerm query, no fallback tiers, no ASR variants
                artists = await SearchWithAsrFallbackAsync(musician,
                    searchTerm =>
                    {
                        var q = new InternalItemsQuery()
                        {
                            Recursive = true,
                            SearchTerm = searchTerm,
                            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                            TopParentIds = topParentIds!,
                            DtoOptions = new DtoOptions(true)
                        };
                        return RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", cancellationToken);
                    }, mode).ConfigureAwait(false);

                tierSw.Stop();
                tierReached = 1;
                Logger.LogInformation(
                    "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}' mode=Fast",
                    tierSw.ElapsedMilliseconds, artists.Count, musician);
            }
            else
            {
                // Thorough mode: 4-tier fallback with ASR variants on tier 1
                artists = await SearchWithAsrFallbackAsync(musician,
                    searchTerm =>
                    {
                        var q = new InternalItemsQuery()
                        {
                            Recursive = true,
                            SearchTerm = searchTerm,
                            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                            TopParentIds = topParentIds!,
                            DtoOptions = new DtoOptions(true)
                        };
                        return RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", cancellationToken);
                    }).ConfigureAwait(false);

                tierSw.Stop();
                tierReached = 1;
                Logger.LogInformation(
                    "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}'",
                    tierSw.ElapsedMilliseconds, artists.Count, musician);

                // Early-exit on tier 1 hit
                if (artists.Count == 0)
                {
                    // Parallelize tiers 2-4: all independent, pick by priority order
                    string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;

                    var tier2 = TryPrefixFallbackAsync(firstWord, musician, topParentIds, user, "GetArtistsFuzzy", cancellationToken);
                    var tier3 = !string.Equals(firstWord, musician, StringComparison.Ordinal)
                        ? TryPrefixFallbackAsync(musician, musician, topParentIds, user, "GetArtistsFullPrefix", cancellationToken)
                        : Task.FromResult<BaseItem?>(null);
                    var tier4 = TryContainsFallbackAsync(musician, musician, topParentIds, user, "GetArtistsContains", cancellationToken);

                    BaseItem?[] parallelResults = await Task.WhenAll(tier2, tier3, tier4).ConfigureAwait(false);

                    // Preserve priority: tier 2 > tier 3 > tier 4
                    BaseItem? match = parallelResults[0] ?? parallelResults[1] ?? parallelResults[2];
                    tierReached = match != null ? (parallelResults[0] != null ? 2 : parallelResults[1] != null ? 3 : 4) : 4;

                    Logger.LogInformation(
                        "ArtistSearch: tiers=2-4 (parallel) matched={Matched} tierHit={Tier} method=ParallelFallback query='{Query}'",
                        match != null, tierReached, musician);
                    if (match != null)
                    {
                        artists = new List<BaseItem> { match };
                    }
                }
            }
        }

        totalSw.Stop();
        Logger.LogInformation(
            "ArtistSearch: total duration={TotalMs}ms tier_reached={Tier} results={Count} query='{Query}' source={Source} mode={Mode}",
            totalSw.ElapsedMilliseconds,
            tierReached,
            artists.Count,
            musician,
            searchSource,
            mode);

        if (artists.Count == 0)
        {
            Logger.LogDebug("PlayArtistSongs: no artist found for query='{Query}'", musician);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        // Disambiguation: in Fast mode, auto-play the best match
        bool fastAutoPlay = mode == SearchResponseMode.Fast && artists.Count > 1;

        if (artists.Count > 1 && !fastAutoPlay)
        {
            Logger.LogDebug("PlayArtistSongs: {Count} artists matched, running disambiguation", artists.Count);
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                musician,
                artists,
                a => a.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeArtist,
                locale,
                best =>
                {
                    artists = new List<BaseItem> { best };
                    return null!;
                },
                user: user);

            if (missOutcome == FuzzyMissOutcome.NotFound)
            {
                Logger.LogDebug("PlayArtistSongs: fuzzy miss outcome=NotFound, asking user to disambiguate");
                var matches = artists.Take(3).Select(a => (a.Id, a.Name, (string?)GetImageUrl(a.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeArtist, locale, context);
            }

            if (missResponse != null)
            {
                Logger.LogDebug("PlayArtistSongs: fuzzy miss outcome={Outcome}, returning response", missOutcome);
                return missResponse;
            }
        }
        else if (fastAutoPlay)
        {
            // Fast mode: pick the best fuzzy match and auto-play
            var best = FuzzyMatch(musician, artists, a => a.Name, user);
            if (best != null)
            {
                artists = new List<BaseItem> { best };
            }
            else
            {
                artists = new List<BaseItem> { artists[0] };
            }

            Logger.LogDebug("PlayArtistSongs: fast auto-play picked '{Name}'", artists[0].Name);
        }

        string matchedArtistName = artists[0].Name;
        Logger.LogDebug("PlayArtistSongs: matched artist='{ArtistName}' (id={ArtistId})", matchedArtistName, artists[0].Id);

        // Fetch the first page of artist songs for fast time-to-audio.
        // Remaining songs will be fetched on demand by PlaybackNearlyFinished.
        var artistSongsQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            OrderBy = PopularitySort,
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artists[0].Id },
            Limit = ProgressiveQueueConstants.GetInitialFetchSize()
        };

        // Reuse pre-resolved library filter. Both paths set topParentIds above.
        if (topParentIds != null)
        {
            artistSongsQuery.TopParentIds = topParentIds;
        }

        // Use GetItemList instead of GetItemsResult. Jellyfin's GetItemsResult evaluates
        // dbQuery.Count() after applying the ArtistIds cross-reference + PopularitySort
        // (which references User data), and EF Core's Count() translation NREs on this
        // combination. GetItemList skips the Count() step entirely.
        IReadOnlyList<BaseItem> artistItems = await RetryAsync(
            () => _libraryManager.GetItemList(artistSongsQuery),
            "GetArtistSongs",
            cancellationToken).ConfigureAwait(false);

        Logger.LogDebug("PlayArtistSongs: Jellyfin returned {SongCount} songs for artist='{ArtistName}'", artistItems.Count, matchedArtistName);

        if (artistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, matchedArtistName));
        }

        // Single-pass sort + resume detection (avoids duplicate GetUserData calls)
        var (artistsItems, startIndex, _) = SortAndFindResumeIndex(
            artistItems, jellyfinUser!, _userDataManager, resumePosition: false);

        if (startIndex > 0)
        {
            Logger.LogInformation(
                "PlayArtistSongs: resuming queue from track {Index} ({Name})",
                startIndex, artistsItems[startIndex].Name);
        }

        if (_config.ShuffleArtistSongs)
        {
            artistsItems = ShuffleCopy(artistsItems);
            startIndex = 0;
            Logger.LogDebug("PlayArtistSongs: shuffled {Count} tracks", artistsItems.Count);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < artistsItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = artistsItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = artistsItems[startIndex];

        // Persist queue to device storage for crash recovery
        _queueManager?.SetQueue(
            context.System.Device.DeviceID,
            artistsItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        // Store continuation info so PlaybackNearlyFinished can fetch the rest.
        // Without TotalRecordCount, assume more items exist if we filled the page.
        if (artistItems.Count >= ProgressiveQueueConstants.GetInitialFetchSize())
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Artist",
                    ArtistId = artists[0].Id,
                    StartIndex = artistItems.Count,
                    TotalCount = int.MaxValue,
                    UserId = jellyfinUser!.Id,
                    SortOrder = PopularitySort,
                    Shuffle = _config.ShuffleArtistSongs
                });
        }

        string itemId = artistsItems[startIndex].Id.ToString();

        Logger.LogDebug(
            "PlayArtistSongs: returning AudioPlayer, itemId={ItemId}, startIndex={StartIndex}, queueSize={QueueSize}, offset=0",
            itemId, startIndex, queueItems.Count);
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, artistsItems[0], user, context);
    }

    /// <summary>
    /// Tries a NameStartsWith prefix search followed by fuzzy matching against the results.
    /// Used as a fallback when the primary SearchTerm query returns no artists.
    /// </summary>
    private async Task<BaseItem?> TryPrefixFallbackAsync(
        string prefix, string musician, Guid[]? topParentIds, Entities.User? user,
        string retryLabel, CancellationToken cancellationToken)
    {
        return await TrySearchFallbackAsync(
            q => q.NameStartsWith = prefix, musician, topParentIds, user, retryLabel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries a NameContains substring search followed by fuzzy matching against the results.
    /// Catches cases where the query appears anywhere in the artist name (e.g. "Kidz Bop" → "The Kidz Bop Kids").
    /// </summary>
    private async Task<BaseItem?> TryContainsFallbackAsync(
        string searchTerm, string musician, Guid[]? topParentIds, Entities.User? user,
        string retryLabel, CancellationToken cancellationToken)
    {
        return await TrySearchFallbackAsync(
            q => q.NameContains = searchTerm, musician, topParentIds, user, retryLabel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a configured InternalItemsQuery and fuzzy-matches the results against the artist name.
    /// Uses pre-resolved topParentIds to avoid repeated library filter resolution.
    /// </summary>
    private async Task<BaseItem?> TrySearchFallbackAsync(
        Action<InternalItemsQuery> configure,
        string musician,
        Guid[]? topParentIds,
        Entities.User? user,
        string retryLabel,
        CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery()
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            TopParentIds = topParentIds!,
            DtoOptions = new DtoOptions(true)
        };
        configure(query);

        IReadOnlyList<BaseItem> results = await RetryAsync(
            () => _libraryManager.GetItemList(query),
            retryLabel,
            cancellationToken).ConfigureAwait(false);

        if (results == null || results.Count == 0)
        {
            return null;
        }

        return FuzzyMatch(musician, results, a => a.Name, user);
    }
}
