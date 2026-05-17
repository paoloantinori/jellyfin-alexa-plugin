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

        if (string.IsNullOrWhiteSpace(musician))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchArtistName", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var totalSw = Stopwatch.StartNew();
        var tierSw = Stopwatch.StartNew();
        int tierReached = 0;
        string searchSource = "Database";

        IReadOnlyList<BaseItem> artists;

        if (_artistIndex?.IsReady == true)
        {
            // In-memory search: resolve library filter once, search the pre-loaded index
            searchSource = "InMemory";
            var allowedLibraryIds = GetAllowedLibraryIds(user);
            Guid[]? topParentIds = allowedLibraryIds != null
                ? Util.LibraryFilter.ResolveTopParentIds(allowedLibraryIds, _libraryManager)
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

            // Tier 2: prefix first word + fuzzy (catches ASR truncation, e.g. "soul coughin" → "Soul Coughing")
            string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;
            if (artists.Count == 0)
            {
                tierSw.Restart();
                var prefixCandidates = allArtists
                    .Where(a => a.Name.StartsWith(firstWord, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                BaseItem? fuzzy = FuzzyMatch(musician, prefixCandidates, a => a.Name, user);
                tierSw.Stop();
                tierReached = 2;
                Logger.LogInformation(
                    "ArtistSearch: tier=2 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFirstWord query='{Query}' prefix='{Prefix}'",
                    tierSw.ElapsedMilliseconds, fuzzy != null, musician, firstWord);
                if (fuzzy != null)
                {
                    artists = new List<BaseItem> { fuzzy };
                }
            }

            // Tier 3: prefix full query + fuzzy (e.g. "Kidz Bop" → "Kidz Bop Kids")
            // Only applies when the query has multiple words (tier 2 already covered single-word prefix).
            if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
            {
                tierSw.Restart();
                var prefixCandidates = allArtists
                    .Where(a => a.Name.StartsWith(musician, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                BaseItem? fuzzy = FuzzyMatch(musician, prefixCandidates, a => a.Name, user);
                tierSw.Stop();
                tierReached = 3;
                Logger.LogInformation(
                    "ArtistSearch: tier=3 duration={TierMs}ms matched={Matched} method=InMemoryPrefixFull query='{Query}'",
                    tierSw.ElapsedMilliseconds, fuzzy != null, musician);
                if (fuzzy != null)
                {
                    artists = new List<BaseItem> { fuzzy };
                }
            }

            // Tier 4: fuzzy match against ALL artists (catches misspellings)
            if (artists.Count == 0)
            {
                tierSw.Restart();
                BaseItem? fuzzy = FuzzyMatch(musician, allArtists, a => a.Name, user);
                tierSw.Stop();
                tierReached = 4;
                Logger.LogInformation(
                    "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=InMemoryFuzzyAll query='{Query}'",
                    tierSw.ElapsedMilliseconds, fuzzy != null, musician);
                if (fuzzy != null)
                {
                    artists = new List<BaseItem> { fuzzy };
                }
            }
        }
        else
        {
            // Fallback: database queries when in-memory index is not yet loaded
            var artistSearchQuery = new InternalItemsQuery()
            {
                Recursive = true,
                SearchTerm = musician,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(artistSearchQuery, user, _libraryManager);

            artists = await RetryAsync(
                () => _libraryManager.GetItemList(artistSearchQuery),
                "GetArtists",
                cancellationToken).ConfigureAwait(false);

            tierSw.Stop();
            tierReached = 1;
            Logger.LogInformation(
                "ArtistSearch: tier=1 duration={TierMs}ms results={Count} method=SearchTerm query='{Query}'",
                tierSw.ElapsedMilliseconds, artists.Count, musician);

            string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;
            if (artists.Count == 0)
            {
                tierSw.Restart();
                BaseItem? fuzzy = await TryPrefixFallbackAsync(
                    firstWord, musician, user, "GetArtistsFuzzy", cancellationToken).ConfigureAwait(false);
                tierSw.Stop();
                tierReached = 2;
                Logger.LogInformation(
                    "ArtistSearch: tier=2 duration={TierMs}ms matched={Matched} method=PrefixFirstWord query='{Query}' prefix='{Prefix}'",
                    tierSw.ElapsedMilliseconds, fuzzy != null, musician, firstWord);
                if (fuzzy != null)
                {
                    artists = new List<BaseItem> { fuzzy };
                }
            }

            if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
            {
                tierSw.Restart();
                BaseItem? fullPrefixFuzzy = await TryPrefixFallbackAsync(
                    musician, musician, user, "GetArtistsFullPrefix", cancellationToken).ConfigureAwait(false);
                tierSw.Stop();
                tierReached = 3;
                Logger.LogInformation(
                    "ArtistSearch: tier=3 duration={TierMs}ms matched={Matched} method=PrefixFullQuery query='{Query}'",
                    tierSw.ElapsedMilliseconds, fullPrefixFuzzy != null, musician);
                if (fullPrefixFuzzy != null)
                {
                    artists = new List<BaseItem> { fullPrefixFuzzy };
                }
            }

            if (artists.Count == 0)
            {
                tierSw.Restart();
                BaseItem? containsFuzzy = await TryContainsFallbackAsync(
                    musician, musician, user, "GetArtistsContains", cancellationToken).ConfigureAwait(false);
                tierSw.Stop();
                tierReached = 4;
                Logger.LogInformation(
                    "ArtistSearch: tier=4 duration={TierMs}ms matched={Matched} method=Contains query='{Query}'",
                    tierSw.ElapsedMilliseconds, containsFuzzy != null, musician);
                if (containsFuzzy != null)
                {
                    artists = new List<BaseItem> { containsFuzzy };
                }
            }
        }

        totalSw.Stop();
        Logger.LogInformation(
            "ArtistSearch: total duration={TotalMs}ms tier_reached={Tier} results={Count} query='{Query}' source={Source}",
            totalSw.ElapsedMilliseconds, tierReached, artists.Count, musician, searchSource);

        if (artists.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        if (artists.Count > 1)
        {
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
                var matches = artists.Take(3).Select(a => (a.Id, a.Name)).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeArtist, locale);
            }

            if (missResponse != null)
            {
                return missResponse;
            }
        }

        string matchedArtistName = artists[0].Name;

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
        ApplyLibraryFilter(artistSongsQuery, user, _libraryManager);

        QueryResult<BaseItem> artistResult = await RetryAsync(
            () => _libraryManager.GetItemsResult(artistSongsQuery),
            "GetArtistSongs",
            cancellationToken).ConfigureAwait(false);

        if (artistResult.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, matchedArtistName));
        }

        IReadOnlyList<BaseItem> artistsItems = FavoritesAndRatingsFirst(artistResult.Items, jellyfinUser, _userDataManager);

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < artistsItems.Count; i++)
        {
            BaseItem item = artistsItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
            });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = artistsItems[0];

        // Persist queue to device storage for crash recovery
        _queueManager?.SetQueue(
            context.System.Device.DeviceID,
            artistsItems.Select(i => i.Id.ToString()).ToList(),
            0);

        // Store continuation info so PlaybackNearlyFinished can fetch the rest
        if (artistResult.TotalRecordCount > artistResult.Items.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Artist",
                    ArtistId = artists[0].Id,
                    StartIndex = artistResult.Items.Count,
                    TotalCount = artistResult.TotalRecordCount,
                    UserId = jellyfinUser.Id,
                    SortOrder = PopularitySort
                });
        }

        string itemId = artistsItems[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, artistsItems[0], user, context);
    }

    /// <summary>
    /// Tries a NameStartsWith prefix search followed by fuzzy matching against the results.
    /// Used as a fallback when the primary SearchTerm query returns no artists.
    /// </summary>
    private async Task<BaseItem?> TryPrefixFallbackAsync(
        string prefix, string musician, Entities.User? user,
        string retryLabel, CancellationToken cancellationToken)
    {
        return await TrySearchFallbackAsync(
            q => q.NameStartsWith = prefix, musician, user, retryLabel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tries a NameContains substring search followed by fuzzy matching against the results.
    /// Catches cases where the query appears anywhere in the artist name (e.g. "Kidz Bop" → "The Kidz Bop Kids").
    /// </summary>
    private async Task<BaseItem?> TryContainsFallbackAsync(
        string searchTerm, string musician, Entities.User? user,
        string retryLabel, CancellationToken cancellationToken)
    {
        return await TrySearchFallbackAsync(
            q => q.NameContains = searchTerm, musician, user, retryLabel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a configured InternalItemsQuery and fuzzy-matches the results against the artist name.
    /// </summary>
    private async Task<BaseItem?> TrySearchFallbackAsync(
        Action<InternalItemsQuery> configure, string musician, Entities.User? user,
        string retryLabel, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery()
        {
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            DtoOptions = new DtoOptions(true)
        };
        configure(query);
        ApplyLibraryFilter(query, user, _libraryManager);

        IReadOnlyList<BaseItem> results = await RetryAsync(
            () => _libraryManager.GetItemList(query),
            retryLabel,
            cancellationToken).ConfigureAwait(false);

        return FuzzyMatch(musician, results, a => a.Name, user);
    }
}