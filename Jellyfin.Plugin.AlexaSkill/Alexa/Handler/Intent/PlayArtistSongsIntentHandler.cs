using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayArtistSongsIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlayArtistSongsIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
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

        var artistSearchQuery = new InternalItemsQuery()
        {
            Recursive = true,
            SearchTerm = musician,
            IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(artistSearchQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> artists = await RetryAsync(
            () => _libraryManager.GetItemList(artistSearchQuery),
            "GetArtists",
            cancellationToken).ConfigureAwait(false);

        // Fallback: when SearchTerm fails (e.g. Alexa ASR truncation "soul coughin" vs "Soul Coughing"),
        // try a broader prefix search and fuzzy match the results.
        string firstWord = musician.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? musician;
        if (artists.Count == 0)
        {
            BaseItem? fuzzy = await TryPrefixFallbackAsync(
                firstWord, musician, user, "GetArtistsFuzzy", cancellationToken).ConfigureAwait(false);
            if (fuzzy != null)
            {
                Logger.LogInformation(
                    "First-word prefix fallback matched '{Name}' for query '{Query}' (prefix '{Prefix}')",
                    fuzzy.Name, musician, firstWord);
                artists = new List<BaseItem> { fuzzy };
            }
        }

        // Second fallback: try full query as prefix (e.g. "Kidz Bop" → NameStartsWith "Kidz Bop").
        // Helps when the first-word prefix returns too many results or the fuzzy match
        // falls below threshold (e.g. "Kidz Bop" → "Kidz Bop Kids").
        if (artists.Count == 0 && !string.Equals(firstWord, musician, StringComparison.Ordinal))
        {
            BaseItem? fullPrefixFuzzy = await TryPrefixFallbackAsync(
                musician, musician, user, "GetArtistsFullPrefix", cancellationToken).ConfigureAwait(false);
            if (fullPrefixFuzzy != null)
            {
                Logger.LogInformation(
                    "Full-prefix fallback matched '{Name}' for query '{Query}'",
                    fullPrefixFuzzy.Name, musician);
                artists = new List<BaseItem> { fullPrefixFuzzy };
            }
        }

        // Third fallback: try NameContains substring search (e.g. "Kidz Bop" → "The Kidz Bop Kids").
        // Catches cases where the query appears anywhere in the artist name, not just at the start.
        if (artists.Count == 0)
        {
            BaseItem? containsFuzzy = await TryContainsFallbackAsync(
                musician, musician, user, "GetArtistsContains", cancellationToken).ConfigureAwait(false);
            if (containsFuzzy != null)
            {
                Logger.LogInformation(
                    "Contains fallback matched '{Name}' for query '{Query}'",
                    containsFuzzy.Name, musician);
                artists = new List<BaseItem> { containsFuzzy };
            }
        }

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

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }
            }
            else
            {
                var matches = artists.Take(3).Select(a => (a.Id, a.Name)).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeArtist, locale);
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

        string item_id = artistsItems[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, artistsItems[0], user, context);
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