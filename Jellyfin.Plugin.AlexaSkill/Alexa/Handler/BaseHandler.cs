using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using AlexaSession = Alexa.NET.Request.Session;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Base handler class to handle skill requests.
/// </summary>
public abstract class BaseHandler
{
    /// <summary>
    /// Alexa request timeout budget in milliseconds.
    /// Matches the CancellationTokenSource(TimeSpan.FromSeconds(6)) in AlexaSkillController.
    /// </summary>
    private const int AlexaRequestTimeoutMs = 6000;

    protected static readonly (ItemSortBy SortBy, SortOrder Order)[] PopularitySort =
    {
        (ItemSortBy.PlayCount, SortOrder.Descending),
        (ItemSortBy.CommunityRating, SortOrder.Descending),
        (ItemSortBy.SortName, SortOrder.Ascending)
    };

    /// <summary>
    /// Reorder items so favorites appear first while preserving the original
    /// relative order within each group (stable partition).
    /// </summary>
    /// <param name="items">Items to reorder.</param>
    /// <param name="user">Jellyfin user for favorite lookup.</param>
    /// <param name="userDataManager">User data manager for favorite status.</param>
    /// <returns>Items with favorites first, or original list if no favorites found.</returns>
    protected static IReadOnlyList<BaseItem> FavoritesFirst(
        IReadOnlyList<BaseItem> items,
        Jellyfin.Database.Implementations.Entities.User user,
        IUserDataManager userDataManager)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var favorites = new List<BaseItem>();
        var rest = new List<BaseItem>(items.Count);

        foreach (BaseItem item in items)
        {
            UserItemData? data = userDataManager.GetUserData(user, item);
            if (data?.IsFavorite == true)
            {
                favorites.Add(item);
            }
            else
            {
                rest.Add(item);
            }
        }

        if (favorites.Count == 0)
        {
            return items;
        }

        favorites.AddRange(rest);
        return favorites;
    }

    private protected PluginConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="BaseHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">The logger factory instance.</param>
    protected BaseHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
    {
        SessionManager = sessionManager;
        _config = config;
        Logger = loggerFactory.CreateLogger<BaseHandler>();
    }

    /// <summary>
    /// Gets or sets the session manager instance.
    /// </summary>
    protected ISessionManager SessionManager { get; set; }

    /// <summary>
    /// Gets or sets logger instance.
    /// </summary>
    protected ILogger Logger { get; set; }

    /// <summary>
    /// Handle a skill request by calling the class HandleAsync method and return a skill response.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public Task<SkillResponse> HandleRequestAsync(Request request, Context context, CancellationToken cancellationToken = default)
    {
        return HandleRequestAsync(request, context, (AlexaSession?)null, cancellationToken);
    }

    /// <summary>
    /// Handle a skill request with Alexa session attributes for disambiguation state.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="alexaSession">The Alexa session containing session attributes.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public async Task<SkillResponse> HandleRequestAsync(Request request, Context context, AlexaSession? alexaSession, CancellationToken cancellationToken = default)
    {
        // Voice-based identification takes priority over account linking so multi-user
        // households get the right library automatically when speaker recognition is active.
        string? personId = context.System?.Person?.PersonId;
        Entities.User? user = !string.IsNullOrEmpty(personId)
            ? _config.GetUserByPersonId(personId)
            : null;

        // Account linking via access token serves as the fallback for devices without speaker recognition.
        if (user == null)
        {
            if (!Guid.TryParse(context.System!.User!.AccessToken, out Guid userId))
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", GetLocale(request)));
            }

            user = _config.GetUserById(userId);
        }

        if (user == null)
        {
            Logger.LogError("User not found for access token or person ID");

            return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", GetLocale(request)));
        }

        SessionInfo? session = await RetryHelper.ExecuteWithRetryAsync(
            () => SessionManager.GetSessionByAuthenticationToken(user.JellyfinToken, context.System!.Device!.DeviceID, Plugin.Instance!.Configuration.ServerAddress),
            Logger,
            "GetSessionByAuthToken",
            cancellationToken: cancellationToken,
            timeoutMs: AlexaRequestTimeoutMs).ConfigureAwait(false);

        string serverUrl = _config.ServerAddress;

        if (session == null)
        {
            Logger.LogError("Session not found for user {UserId}", user.Id);
            return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", GetLocale(request)));
        }

        try
        {
            SkillResponse response = await HandleAsync(request, context, user, session, alexaSession?.Attributes, cancellationToken).ConfigureAwait(false);
            Plugin.Instance?.CircuitBreaker.RecordSuccess(serverUrl);
            return response;
        }
        catch (Exception ex) when (RetryHelper.IsTransient(ex, cancellationToken))
        {
            Plugin.Instance?.CircuitBreaker.RecordFailure(serverUrl, Logger);
            throw;
        }
    }

    /// <summary>
    /// Determines whether this instance can handle the skill request.
    /// </summary>
    /// <param name="request">The Request type what this handler can process.</param>
    /// <returns>True if this handle can handle the given request type, false otherwise.</returns>
    public abstract bool CanHandle(Request request);

    /// <summary>
    /// Handle a skill request and return a skill response.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public abstract Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken);

    /// <summary>
    /// Handle a skill request with session attributes for disambiguation state.
    /// By default delegates to the session-unaware overload. Handlers that need
    /// session attributes (e.g. Yes/No during disambiguation) should override this.
    /// </summary>
    /// <param name="request">The skill request to handle.</param>
    /// <param name="context">The lambda context.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="sessionAttributes">Session attributes from the Alexa request, or null.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The skill response to the request.</returns>
    public virtual Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        return HandleAsync(request, context, user, session, cancellationToken);
    }

    /// <summary>
    /// Get a stream url for the given item.
    /// </summary>
    /// <param name="itemId">Id of the item to stream.</param>
    /// <param name="user">The user for which the item should be played.</param>
    /// <returns>Streamable url of the requested item.</returns>
    public string GetStreamUrl(string itemId, Entities.User user)
        => BuildStreamUrl("Audio/", itemId, user);

    /// <summary>
    /// Get a video stream URL for the given item.
    /// </summary>
    /// <param name="itemId">Id of the item to stream.</param>
    /// <param name="user">The user for which the item should be played.</param>
    /// <returns>Streamable url of the requested item.</returns>
    public string GetVideoStreamUrl(string itemId, Entities.User user)
        => BuildStreamUrl("Videos/", itemId, user);

    private string BuildStreamUrl(string pathSegment, string itemId, Entities.User user)
        => new Uri(new Uri(_config.ServerAddress), $"{pathSegment}{itemId}/stream?static=true&api_key={user.JellyfinToken}").ToString();

    /// <summary>
    /// Get a cover art image URL for the given item.
    /// </summary>
    /// <param name="itemId">Id of the item.</param>
    /// <param name="user">The user for authentication.</param>
    /// <returns>URL of the item's primary image.</returns>
    public string GetImageUrl(string itemId, Entities.User user)
    {
        return new Uri(new Uri(_config.ServerAddress), "Items/" + itemId + "/Images/Primary?api_key=" + user.JellyfinToken).ToString();
    }

    /// <summary>
    /// Build an AudioPlayer response with cover art metadata.
    /// </summary>
    /// <param name="playBehavior">The play behavior (ReplaceAll, Enqueue, ReplaceEnqueued).</param>
    /// <param name="streamUrl">The audio stream URL.</param>
    /// <param name="itemId">The item ID used as the stream token.</param>
    /// <param name="item">The media item for metadata (title, art), or null.</param>
    /// <param name="user">The user for building the image URL.</param>
    /// <param name="offsetInMilliseconds">Resume offset in milliseconds (default 0).</param>
    /// <returns>A SkillResponse containing the AudioPlayer directive with metadata.</returns>
    public SkillResponse BuildAudioPlayerResponse(PlayBehavior playBehavior, string streamUrl, string itemId, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User user, int offsetInMilliseconds = 0)
    {
        return BuildAudioPlayerResponse(playBehavior, streamUrl, itemId, item, user, null, offsetInMilliseconds);
    }

    /// <summary>
    /// Build an AudioPlayer response with cover art metadata and optional APL visual.
    /// </summary>
    /// <param name="playBehavior">The play behavior (ReplaceAll, Enqueue, ReplaceEnqueued).</param>
    /// <param name="streamUrl">The audio stream URL.</param>
    /// <param name="itemId">The item ID used as the stream token.</param>
    /// <param name="item">The media item for metadata (title, art), or null.</param>
    /// <param name="user">The user for building the image URL.</param>
    /// <param name="context">Optional Alexa context for APL device detection.</param>
    /// <param name="offsetInMilliseconds">Resume offset in milliseconds (default 0).</param>
    /// <returns>A SkillResponse containing the AudioPlayer directive with optional APL visual.</returns>
    public SkillResponse BuildAudioPlayerResponse(PlayBehavior playBehavior, string streamUrl, string itemId, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User user, Context? context, int offsetInMilliseconds = 0)
    {
        string imageUrl = item != null ? GetImageUrl(itemId, user) : string.Empty;
        var imageSources = new AudioItemSources
        {
            Sources = new List<AudioItemSource> { new() { Url = imageUrl } }
        };

        var stream = new AudioItemStream
        {
            Url = streamUrl,
            Token = itemId,
            OffsetInMilliseconds = offsetInMilliseconds
        };

        if (playBehavior == PlayBehavior.Enqueue && context?.AudioPlayer?.Token != null)
        {
            stream.ExpectedPreviousToken = context.AudioPlayer.Token;
        }

        var directive = new AudioPlayerPlayDirective
        {
            PlayBehavior = playBehavior,
            AudioItem = new AudioItem
            {
                Stream = stream,
                Metadata = new AudioItemMetadata
                {
                    Title = item?.Name ?? string.Empty,
                    Subtitle = GetSubtitle(item),
                    Art = imageSources,
                    BackgroundImage = imageSources
                }
            }
        };

        var directives = new List<IDirective> { directive };

        if (item != null && context != null && Apl.AplHelper.DeviceSupportsApl(context))
        {
            var aplDirective = Apl.AplHelper.BuildNowPlayingDirective(item, imageUrl, imageUrl);
            if (aplDirective != null)
            {
                directives.Add(aplDirective);
            }
        }

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                Directives = directives
            }
        };
    }

    /// <summary>
    /// Build a subtitle string from item metadata for display on Echo Show/Fire TV.
    /// </summary>
    private static string GetSubtitle(BaseItem? item)
    {
        if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
        {
            return audio.Artists?.Count > 0
                ? audio.Artists[0]
                : audio.Album ?? string.Empty;
        }

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            return episode.SeriesName ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Build a Tell response using SSML for more natural speech.
    /// </summary>
    /// <param name="ssml">SSML content (without the outer speak tags).</param>
    /// <returns>A SkillResponse with SSML output speech.</returns>
    public static SkillResponse TellSsml(string ssml)
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
            }
        };
    }

    /// <summary>
    /// Build an Ask response using SSML for more natural speech, with an SSML reprompt.
    /// </summary>
    /// <param name="ssml">SSML content for the main speech (without speak tags).</param>
    /// <param name="repromptSsml">SSML content for the reprompt (without speak tags).</param>
    /// <returns>A SkillResponse with SSML output speech and reprompt.</returns>
    public static SkillResponse AskSsml(string ssml, string repromptSsml)
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = false,
                OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" },
                Reprompt = new Reprompt { OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{repromptSsml}</speak>" } }
            }
        };
    }

    /// <summary>
    /// Build an Ask response using SSML for speech and plain text for reprompt.
    /// </summary>
    /// <param name="ssml">SSML content for the main speech (without speak tags).</param>
    /// <param name="reprompt">Plain text reprompt.</param>
    /// <returns>A SkillResponse with SSML output speech and plain text reprompt.</returns>
    public static SkillResponse AskSsml(string ssml, Reprompt reprompt)
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = false,
                OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" },
                Reprompt = reprompt
            }
        };
    }

    /// <summary>
    /// Try to get an SSML-enhanced string from locale files.
    /// Returns null if no SSML key exists, allowing fallback to plain text.
    /// </summary>
    /// <param name="key">The SSML key (e.g. "NowPlayingSsml").</param>
    /// <param name="locale">The locale identifier.</param>
    /// <param name="args">Optional format arguments.</param>
    /// <returns>The formatted SSML string, or null if the key doesn't exist.</returns>
    public static string? GetSsml(string key, string locale, params object[] args)
    {
        string template = ResponseStrings.Get(key, locale);
        if (template == key)
        {
            return null;
        }

        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>
    /// Extract the locale from the request, defaulting to en-US if not available.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The locale string (e.g. "en-US", "it-IT").</returns>
    protected static string GetLocale(Request request)
    {
        return GetLocalePublic(request);
    }

    /// <summary>
    /// Extract the locale from the request, defaulting to en-US if not available.
    /// Public version accessible from pipeline interceptors.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The locale string (e.g. "en-US", "it-IT").</returns>
    public static string GetLocalePublic(Request request)
    {
        return string.IsNullOrEmpty(request.Locale) ? "en-US" : request.Locale;
    }

    /// <summary>
    /// Send a progressive response to keep the Alexa session alive during long operations.
    /// Resets the 8-second timeout. Only works with IntentRequest/LaunchRequest.
    /// </summary>
    /// <param name="context">The Alexa context containing API access token.</param>
    /// <param name="request">The request containing the request ID.</param>
    /// <param name="message">The message to speak to the user.</param>
    /// <returns>A task representing the async operation.</returns>
    private static readonly HttpClient ProgressiveResponseHttp = new() { Timeout = TimeSpan.FromSeconds(2) };

    protected async Task SendProgressiveResponse(Context context, Request request, string message)
    {
        try
        {
            var progressiveResponse = new ProgressiveResponse(
                context.System.ApiAccessToken,
                request.RequestId,
                context.System?.ApiEndpoint ?? "https://api.amazonalexa.com",
                ProgressiveResponseHttp);
            await progressiveResponse.SendSpeech(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to send progressive response");
        }
    }

    /// <summary>
    /// Execute a synchronous Jellyfin API call with retry logic and exponential backoff.
    /// </summary>
    /// <typeparam name="T">The return type.</typeparam>
    /// <param name="operation">The synchronous operation to execute.</param>
    /// <param name="operationName">Name for logging (e.g. "GetItemsList").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    protected Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
    {
        return RetryHelper.ExecuteWithRetryAsync(operation, Logger, operationName, cancellationToken: cancellationToken, timeoutMs: AlexaRequestTimeoutMs);
    }

    /// <summary>
    /// Execute a library search with caching. On success, results are cached.
    /// On failure, returns cached results if available.
    /// </summary>
    /// <param name="userId">The user ID for cache partitioning.</param>
    /// <param name="queryKey">Normalized cache key (search term + filters).</param>
    /// <param name="operation">The library query to execute.</param>
    /// <param name="operationName">Name for logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple of search results and whether they came from cache.</returns>
    protected async Task<(IReadOnlyList<BaseItem> Results, bool FromCache)> CachedSearchAsync(
        Guid userId,
        string queryKey,
        Func<IReadOnlyList<BaseItem>> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        SearchResultCache cache = Plugin.Instance?.SearchCache ?? SearchResultCache.Noop;
        var counters = Plugin.Instance?.RequestCounters;

        try
        {
            IReadOnlyList<BaseItem> results = await RetryAsync(operation, operationName, cancellationToken).ConfigureAwait(false);
            cache.Put(userId, queryKey, results);
            counters?.IncrementCacheMiss();
            return (results, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cache.TryGet(userId, queryKey, out IReadOnlyList<BaseItem>? cached))
        {
            Logger.LogWarning(ex, "Library search failed for {Operation}, serving cached results", operationName);
            counters?.IncrementCacheHit();
            return (cached!, true);
        }
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
    protected static T? FuzzyMatch<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, int threshold = FuzzyMatcher.DefaultThreshold)
        where T : class
    {
        return FuzzyMatcher.FindBestMatch(query, candidates, selector, threshold);
    }

    /// <summary>
    /// Result of a fuzzy match attempt with suggestion support.
    /// </summary>
    protected enum FuzzyMissOutcome
    {
        /// <summary>A close candidate was found and handled (returned as response).</summary>
        SuggestionHandled,
        /// <summary>No close candidate found; caller should handle "not found".</summary>
        NotFound
    }

    /// <summary>
    /// Handle the case when FuzzyMatch returns null. Checks config for behavior:
    /// - Confirm: returns "Did you mean X?" prompt via disambiguation session
    /// - AutoPlay: invokes playFunc with the closest match and returns an announcement response
    /// Returns (SuggestionHandled, response) when a suggestion was made, or (NotFound, null) when no close candidate exists.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="query">The original search query.</param>
    /// <param name="candidates">The full list of candidate items.</param>
    /// <param name="selector">Function to extract the display name from an item.</param>
    /// <param name="matchExtractor">Function to create disambiguation match list from the best candidate.</param>
    /// <param name="mediaType">The media type for disambiguation state.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <param name="autoPlayFunc">Optional function to play the suggested item in AutoPlay mode.</param>
    /// <returns>A tuple indicating the outcome and optional response.</returns>
    protected (FuzzyMissOutcome Outcome, SkillResponse? Response) HandleFuzzyMiss<T>(
        string query,
        IReadOnlyList<T> candidates,
        Func<T, string> selector,
        Func<T, List<(Guid Id, string Name)>> matchExtractor,
        string mediaType,
        string locale,
        Func<T, SkillResponse>? autoPlayFunc = null)
        where T : class
    {
        var bestWithScore = FuzzyMatcher.FindBestMatchWithScore(query, candidates, selector);

        if (bestWithScore == null || bestWithScore.Value.Score < FuzzyMatcher.SuggestionThreshold)
        {
            return (FuzzyMissOutcome.NotFound, null);
        }

        T best = bestWithScore.Value.Item;

        if (_config.FuzzyMatchBehavior == FuzzyMatchBehavior.AutoPlay && autoPlayFunc != null)
        {
            SkillResponse playResponse = autoPlayFunc(best);
            string? ssml = GetSsml("FuzzyAutoPlayAnnouncementSsml", locale, selector(best), query);
            playResponse.Response.OutputSpeech = ssml != null
                ? new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
                : new PlainTextOutputSpeech { Text = ResponseStrings.Get("FuzzyAutoPlayAnnouncement", locale, selector(best), query) };
            return (FuzzyMissOutcome.SuggestionHandled, playResponse);
        }

        // Confirm mode: "Did you mean X?"
        var matches = matchExtractor(best);
        string? promptSsml = GetSsml("FuzzySuggestionPromptSsml", locale, query, selector(best));

        SkillResponse response;
        if (promptSsml != null)
        {
            string reprompt = ResponseStrings.Get("FuzzySuggestionReprompt", locale);
            response = AskSsml(promptSsml, new Reprompt(reprompt));
        }
        else
        {
            string prompt = ResponseStrings.Get("FuzzySuggestionPrompt", locale, query, selector(best));
            string reprompt = ResponseStrings.Get("FuzzySuggestionReprompt", locale);
            response = ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
        }

        response.SessionAttributes = new Dictionary<string, object>
        {
            ["disambig_matches"] = Newtonsoft.Json.JsonConvert.SerializeObject(matches),
            ["disambig_index"] = 0,
            ["disambig_type"] = mediaType
        };

        return (FuzzyMissOutcome.SuggestionHandled, response);
    }

    /// <summary>
    /// Shuffle a list in place using Fisher-Yates algorithm.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    /// <param name="list">The list to shuffle.</param>
    protected static void Shuffle<T>(IList<T> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
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
    protected async Task<IReadOnlyList<BaseItem>> FindRadioTracksAsync(
        MediaBrowser.Controller.Entities.Audio.Audio current,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
    {
        var allResults = new List<BaseItem>();
        var seen = new HashSet<Guid> { current.Id };

        if (current.Genres != null && current.Genres.Length > 0)
        {
            IReadOnlyList<BaseItem> byGenre = await RetryAsync(
                () => libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = jellyfinUser,
                    Recursive = true,
                    Genres = current.Genres,
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    Limit = 50,
                    OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                    DtoOptions = new DtoOptions(true)
                }),
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
    /// Escapes special XML characters in text for safe inclusion in SSML.
    /// </summary>
    /// <param name="text">The text to escape.</param>
    /// <returns>The XML-escaped text.</returns>
    protected static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a Jellyfin user by ID and returns either the user or an error response.
    /// </summary>
    /// <param name="userManager">The user manager to look up the user from.</param>
    /// <param name="userId">The Jellyfin user ID to resolve.</param>
    /// <param name="locale">The locale for the error response string.</param>
    /// <returns>A tuple: use <see cref="JellyfinUser"/> when not null, otherwise return <see cref="SkillResponse"/>.</returns>
    protected static (JellyfinUser? User, SkillResponse? Error) ResolveJellyfinUser(
        IUserManager userManager,
        Guid userId,
        string locale)
    {
        JellyfinUser? user = userManager.GetUserById(userId);
        if (user == null)
        {
            return (null, ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", locale)));
        }

        return (user, null);
    }

    /// <summary>
    /// Conditionally attach an APL list directive to a response if the device supports APL.
    /// </summary>
    /// <param name="response">The skill response to attach the directive to.</param>
    /// <param name="context">The Alexa context for APL device detection.</param>
    /// <param name="title">The title for the APL list.</param>
    /// <param name="items">The items to display in the list.</param>
    /// <param name="token">A token identifying the APL directive.</param>
    /// <param name="action">The action for the APL list items.</param>
    private protected static void TryAttachListDirective(
        SkillResponse response,
        Context? context,
        string title,
        List<Apl.ListDisplayItem> items,
        string token,
        string action = "selectItem")
    {
        if (Apl.AplHelper.DeviceSupportsApl(context))
        {
            var directive = Apl.AplHelper.BuildListDirective(title, items, token, action);
            if (directive != null)
            {
                response.Response.Directives.Add(directive);
            }
        }
    }

    /// <summary>
    /// Extract the first artist name from an audio item, or null for non-audio items.
    /// </summary>
    /// <param name="item">The media item.</param>
    /// <returns>The first artist name, or null.</returns>
    protected static string? GetArtistSubtitle(MediaBrowser.Controller.Entities.BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.Audio.Audio a && a.Artists is { Count: > 0 })
        {
            return a.Artists[0];
        }

        return null;
    }

    /// <summary>
    /// Formats a tick-based playback position into a human-readable string.
    /// </summary>
    /// <param name="ticks">The playback position in ticks.</param>
    /// <returns>A formatted position string (e.g. "1h 30m", "45m 12s", "30s").</returns>
    protected static string FormatPosition(long ticks)
    {
        var ts = TimeSpan.FromTicks(ticks);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s" : $"{ts.Seconds}s";
    }
}
