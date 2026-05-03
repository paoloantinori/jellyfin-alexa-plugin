using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using AlexaSession = Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Base handler class to handle skill requests.
/// </summary>
public abstract class BaseHandler
{
    private PluginConfiguration _config;

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
        if (!Guid.TryParse(context.System.User.AccessToken, out Guid userId))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", GetLocale(request)));
        }

        Entities.User? user = _config.GetUserById(userId);
        if (user == null)
        {
            Logger.LogError("User not found for access token: {UserId}", userId);

            return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", GetLocale(request)));
        }

        SessionInfo session = await RetryHelper.ExecuteWithRetryAsync(
            () => SessionManager.GetSessionByAuthenticationToken(user.JellyfinToken, context.System.Device.DeviceID, Plugin.Instance!.Configuration.ServerAddress),
            Logger,
            "GetSessionByAuthToken",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return await HandleAsync(request, context, user, session, alexaSession?.Attributes, cancellationToken).ConfigureAwait(false);
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
    {
        // TODO: add possible transcoding
        return new Uri(new Uri(_config.ServerAddress), "Items/" + itemId + "/Download?api_key=" + user.JellyfinToken).ToString();
    }

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
        string imageUrl = item != null ? GetImageUrl(itemId, user) : string.Empty;
        var imageSources = new AudioItemSources
        {
            Sources = new List<AudioItemSource> { new() { Url = imageUrl } }
        };

        var directive = new AudioPlayerPlayDirective
        {
            PlayBehavior = playBehavior,
            AudioItem = new AudioItem
            {
                Stream = new AudioItemStream
                {
                    Url = streamUrl,
                    Token = itemId,
                    OffsetInMilliseconds = offsetInMilliseconds
                },
                Metadata = new AudioItemMetadata
                {
                    Title = item?.Name ?? string.Empty,
                    Subtitle = GetSubtitle(item),
                    Art = imageSources,
                    BackgroundImage = imageSources
                }
            }
        };

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                Directives = new List<IDirective> { directive }
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
            return audio.Album ?? string.Empty;
        }

        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            return episode.SeriesName ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>
    /// Extract the locale from the request, defaulting to en-US if not available.
    /// </summary>
    /// <param name="request">The incoming request.</param>
    /// <returns>The locale string (e.g. "en-US", "it-IT").</returns>
    protected static string GetLocale(Request request)
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
    protected async Task SendProgressiveResponse(Context context, Request request, string message)
    {
        try
        {
            using var httpClient = new HttpClient();
            var progressiveResponse = new ProgressiveResponse(
                context.System.ApiAccessToken,
                request.RequestId,
                context.System?.ApiEndpoint ?? "https://api.amazonalexa.com",
                httpClient);
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
        return RetryHelper.ExecuteWithRetryAsync(operation, Logger, operationName, cancellationToken: cancellationToken);
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

        try
        {
            IReadOnlyList<BaseItem> results = await RetryAsync(operation, operationName, cancellationToken).ConfigureAwait(false);
            cache.Put(userId, queryKey, results);
            return (results, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (cache.TryGet(userId, queryKey, out IReadOnlyList<BaseItem>? cached))
        {
            Logger.LogWarning(ex, "Library search failed for {Operation}, serving cached results", operationName);
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
    protected static T? FuzzyMatch<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, int threshold = FuzzyMatcher.DefaultThreshold) where T : class
    {
        return FuzzyMatcher.FindBestMatch(query, candidates, selector, threshold);
    }
}
