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
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
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
        (ItemSortBy.IsFavoriteOrLiked, SortOrder.Descending),
        (ItemSortBy.PlayCount, SortOrder.Descending),
        (ItemSortBy.CommunityRating, SortOrder.Descending),
        (ItemSortBy.SortName, SortOrder.Ascending)
    };

    /// <summary>
    /// Reorder items so favorites appear first, then by personal rating descending
    /// within each group (favorites, non-favorites). Items without a rating keep
    /// their original relative order (stable sort).
    /// </summary>
    /// <param name="items">Items to reorder.</param>
    /// <param name="user">Jellyfin user for favorite and rating lookup.</param>
    /// <param name="userDataManager">User data manager for favorite/rating status.</param>
    /// <returns>Items sorted with favorites first and highest-rated within each group.</returns>
    protected static IReadOnlyList<BaseItem> FavoritesAndRatingsFirst(
        IReadOnlyList<BaseItem> items,
        Jellyfin.Database.Implementations.Entities.User user,
        IUserDataManager userDataManager)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var favorites = new List<(int Index, BaseItem Item, double? Rating)>();
        var rest = new List<(int Index, BaseItem Item, double? Rating)>(items.Count);
        bool anyRating = false;

        for (int i = 0; i < items.Count; i++)
        {
            BaseItem item = items[i];
            UserItemData? data = userDataManager.GetUserData(user, item);
            double? rating = data?.Rating;
            if (rating.HasValue)
            {
                anyRating = true;
            }

            bool isFavorite = data?.IsFavorite == true;

            var entry = (i, item, rating);
            if (isFavorite)
            {
                favorites.Add(entry);
            }
            else
            {
                rest.Add(entry);
            }
        }

        if (!anyRating)
        {
            return items;
        }

        List<BaseItem> result = new List<BaseItem>(items.Count);
        result.AddRange(SortByRating(favorites));
        result.AddRange(SortByRating(rest));
        return result;
    }

    /// <summary>
    /// Combined sort-by-rating + resume-index detection in a single pass over user data.
    /// Eliminates duplicate GetUserData calls when both operations are needed.
    /// </summary>
    protected static (IReadOnlyList<BaseItem> SortedItems, int ResumeIndex, long ResumeTicks) SortAndFindResumeIndex(
        IReadOnlyList<BaseItem> items,
        Jellyfin.Database.Implementations.Entities.User user,
        IUserDataManager userDataManager,
        bool resumePosition)
    {
        if (items.Count <= 1)
        {
            if (items.Count == 1)
            {
                UserItemData? data = userDataManager.GetUserData(user, items[0]);
                long ticks = resumePosition && data?.PlaybackPositionTicks > 0 && data.Played == false
                    ? data.PlaybackPositionTicks : 0;
                return (items, 0, ticks);
            }

            return (items, 0, 0);
        }

        var favorites = new List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)>();
        var rest = new List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)>(items.Count);
        bool anyRating = false;
        int lastPlayedIndex = -1;
        int inProgressIndex = -1;
        long inProgressTicks = 0;

        for (int i = 0; i < items.Count; i++)
        {
            BaseItem item = items[i];
            UserItemData? data = userDataManager.GetUserData(user, item);
            double? rating = data?.Rating;
            if (rating.HasValue)
            {
                anyRating = true;
            }

            bool isFavorite = data?.IsFavorite == true;

            // Track resume position (first in-progress track wins)
            if (inProgressIndex < 0 && data?.PlaybackPositionTicks > 0 && data.Played == false)
            {
                inProgressIndex = i;
                inProgressTicks = resumePosition ? data.PlaybackPositionTicks : 0;
            }

            if (data?.Played == true && lastPlayedIndex < i)
            {
                lastPlayedIndex = i;
            }

            var entry = (i, item, rating, data);
            if (isFavorite)
            {
                favorites.Add(entry);
            }
            else
            {
                rest.Add(entry);
            }
        }

        // Sort
        IReadOnlyList<BaseItem> sorted;
        if (!anyRating)
        {
            sorted = items;
        }
        else
        {
            List<BaseItem> result = new List<BaseItem>(items.Count);
            result.AddRange(SortByRating(favorites));
            result.AddRange(SortByRating(rest));
            sorted = result;
        }

        // Determine resume index
        if (inProgressIndex >= 0)
        {
            // Map original index to sorted position
            BaseItem inProgressItem = items[inProgressIndex];
            int sortedIndex = FindItemIndex(sorted, inProgressItem);
            return (sorted, sortedIndex, inProgressTicks);
        }

        if (lastPlayedIndex >= 0 && lastPlayedIndex + 1 < items.Count)
        {
            BaseItem nextItem = items[lastPlayedIndex + 1];
            int sortedIndex = FindItemIndex(sorted, nextItem);
            return (sorted, sortedIndex, 0);
        }

        return (sorted, 0, 0);
    }

    private static int FindItemIndex(IReadOnlyList<BaseItem> items, BaseItem target)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target))
            {
                return i;
            }
        }

        return 0;
    }

    private static IEnumerable<BaseItem> SortByRating(List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)> items)
    {
        return items.OrderByDescending(i => i.Rating ?? double.MinValue)
                    .ThenBy(i => i.Index)
                    .Select(i => i.Item);
    }

    private static IEnumerable<BaseItem> SortByRating(List<(int Index, BaseItem Item, double? Rating)> items)
    {
        return items.OrderByDescending(i => i.Rating ?? double.MinValue)
                    .ThenBy(i => i.Index)
                    .Select(i => i.Item);
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

    /// <summary>
    /// Get a video-audio URL that combines album art with audio into an MP4 stream
    /// for Echo Show VideoApp playback with native progress bar controls.
    /// </summary>
    /// <param name="itemId">Id of the audio item.</param>
    /// <param name="user">The user (unused, kept for API consistency).</param>
    /// <returns>URL to the video-audio endpoint.</returns>
    public string GetVideoAudioUrl(string itemId, Entities.User user)
        => new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/{itemId}").ToString();

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
    /// Build a pause response: AudioPlayer.Stop with the session kept open for resume.
    /// </summary>
    public static SkillResponse BuildPauseResponse()
    {
        var response = ResponseBuilder.AudioPlayerStop();
        response.Response.ShouldEndSession = false;
        return response;
    }

    /// <summary>
    /// Build a keep-alive response that keeps the skill session alive without
    /// opening the mic. Used by AudioPlayer event handlers to allow subsequent
    /// events (e.g., PING from APL handleTick) to reach the backend.
    /// </summary>
    public static SkillResponse BuildKeepAliveResponse()
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = null
            }
        };
    }

    /// <summary>
    /// Build a response that ends the skill session, causing APL documents to dismiss.
    /// Used when playback finishes and the queue is exhausted, or when the user stops playback.
    /// </summary>
    public static SkillResponse BuildEndSessionResponse()
    {
        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true
            }
        };
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
    /// Build an AudioPlayer response with cover art metadata.
    /// </summary>
    /// <param name="playBehavior">The play behavior (ReplaceAll, Enqueue, ReplaceEnqueued).</param>
    /// <param name="streamUrl">The audio stream URL.</param>
    /// <param name="itemId">The item ID used as the stream token.</param>
    /// <param name="item">The media item for metadata (title, art), or null.</param>
    /// <param name="user">The user for building the image URL.</param>
    /// <param name="context">Optional Alexa context for enqueue previous-token tracking.</param>
    /// <param name="offsetInMilliseconds">Resume offset in milliseconds (default 0).</param>
    /// <returns>A SkillResponse containing the AudioPlayer directive.</returns>
    public SkillResponse BuildAudioPlayerResponse(PlayBehavior playBehavior, string streamUrl, string itemId, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User user, Context? context, int offsetInMilliseconds = 0)
    {
        // Route initial playback through VideoApp when native controls are enabled.
        // Enqueue/ReplaceEnqueued stay as AudioPlayer for queue building.
        // Resume (offset > 0) also stays as AudioPlayer since VideoApp has no offset support.
        if (Plugin.Instance?.Configuration?.NativeControlsForAudio == true
            && playBehavior == PlayBehavior.ReplaceAll
            && offsetInMilliseconds == 0)
        {
            return BuildVideoAppAudioResponse(itemId, item, user);
        }

        Logger.LogDebug("BuildAudioPlayerResponse: itemId={ItemId}, behavior={Behavior}, offsetMs={OffsetMs}, title={Title}",
            itemId, playBehavior, offsetInMilliseconds, item?.Name);
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

        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = true,
                Directives = new List<IDirective> { directive }
            }
        };

        if (Plugin.Instance?.Configuration?.SeekEnabled == true && item != null
            && playBehavior != PlayBehavior.Enqueue)
        {
            string title = item.Name ?? string.Empty;
            string cardContent = title;

            long runTimeTicks = item.RunTimeTicks ?? 0;
            if (runTimeTicks > 0)
            {
                string total = FormatPosition(runTimeTicks);
                if (offsetInMilliseconds > 0)
                {
                    long posTicks = (long)offsetInMilliseconds * TimeSpan.TicksPerMillisecond;
                    cardContent = $"{title}\n{FormatPosition(posTicks)} / {total}";
                }
                else
                {
                    cardContent = $"{title}\n0:00 / {total}";
                }
            }

            response.Response.Card = new StandardCard
            {
                Title = string.Empty,
                Content = cardContent
            };
        }

        return response;
    }

    /// <summary>
    /// Build a VideoApp.Launch response for audio playback using the video-audio
    /// endpoint, which combines album art with audio into a streamable MP4.
    /// Gives native progress bar / scrubber on Echo Show.
    /// </summary>
    public SkillResponse BuildVideoAppAudioResponse(string itemId, BaseItem? item, Entities.User user)
    {
        string videoAudioUrl = GetVideoAudioUrl(itemId, user);
        Logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, title={Title}, url={Url}", itemId, item?.Name, videoAudioUrl);

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                ShouldEndSession = null,
                Directives = new List<IDirective>
                {
                    new Directive.VideoAppLaunchDirective
                    {
                        VideoItem = new Directive.VideoItem
                        {
                            Source = videoAudioUrl,
                            Metadata = new Directive.VideoItemMetadata
                            {
                                Title = item?.Name ?? string.Empty,
                                Subtitle = GetSubtitle(item)
                            }
                        }
                    }
                }
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
    /// Build an OutputSpeech using SSML with plaintext fallback.
    /// Tries the SSML key first; falls back to the plain key if SSML is unavailable.
    /// </summary>
    protected static IOutputSpeech BuildOutputSpeech(string ssmlKey, string plainKey, string locale, params object[] args)
    {
        string? ssml = GetSsml(ssmlKey, locale, args);
        return ssml != null
            ? new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
            : new PlainTextOutputSpeech { Text = ResponseStrings.Get(plainKey, locale, args) };
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
    /// Returns a "feature disabled" response if the flag is off, or null if enabled.
    /// Reads from live configuration so config page changes take effect immediately.
    /// </summary>
    protected SkillResponse? IfFeatureDisabled(Func<PluginConfiguration, bool> isEnabled, Request request)
    {
        var config = Plugin.Instance?.Configuration;
        if (config != null && !isEnabled(config))
        {
            Logger.LogInformation("Feature is disabled via configuration");
            return ResponseBuilder.Tell(ResponseStrings.Get("FeatureDisabled", GetLocale(request)));
        }

        return null;
    }

    /// <summary>
    /// Filters an array of BaseItemKind values to only include types whose media type category
    /// is enabled in configuration.
    /// </summary>
    protected static BaseItemKind[] FilterByContentAccess(BaseItemKind[] types)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return types;
        }

        var allowed = new List<BaseItemKind>(types.Length);
        foreach (var type in types)
        {
            if (IsTypeAllowed(type, config))
            {
                allowed.Add(type);
            }
        }

        return allowed.Count == types.Length ? types : allowed.ToArray();
    }

    private static bool IsTypeAllowed(BaseItemKind type, PluginConfiguration config)
    {
        return type switch
        {
            BaseItemKind.Audio or BaseItemKind.MusicAlbum or BaseItemKind.MusicArtist => config.MusicEnabled,
            BaseItemKind.Movie or BaseItemKind.Episode or BaseItemKind.Series => config.VideosEnabled,
            BaseItemKind.AudioBook => config.BooksEnabled,
            BaseItemKind.Playlist => true, // playlists are cross-type, always allowed
            _ => true // unknown types pass through
        };
    }

    /// <summary>
    /// Checks if a media type category is disabled and returns a localized response.
    /// Use in handlers that target a specific media type.
    /// </summary>
    protected SkillResponse? IfMediaTypeDisabled(Func<PluginConfiguration, bool> isEnabled, Request request)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        if (!isEnabled(config))
        {
            Logger.LogInformation("Media type is disabled via configuration");
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", GetLocale(request)));
        }

        return null;
    }

    /// <summary>
    /// Gets the allowed library IDs for a user, or null if all libraries are accessible.
    /// Returns null when no restriction is configured (backward compatible default).
    /// </summary>
    protected static Guid[]? GetAllowedLibraryIds(Entities.User? user)
        => Util.LibraryFilter.GetAllowedLibraryIds(user);

    /// <summary>
    /// Applies per-user library filtering to a query by setting TopParentIds.
    /// Resolves CollectionFolder IDs to physical folder IDs for correct filtering.
    /// No-op when the user has no library restrictions configured.
    /// </summary>
    protected static void ApplyLibraryFilter(InternalItemsQuery query, Entities.User? user, ILibraryManager libraryManager, ILogger? logger = null)
        => Util.LibraryFilter.ApplyLibraryFilter(query, user, libraryManager, logger);

    /// <summary>
    /// Send a progressive response to keep the Alexa session alive during long operations.
    /// Resets the 8-second timeout. Only works with IntentRequest/LaunchRequest.
    /// A fresh HttpClient is created per call because ProgressiveResponse sets BaseAddress
    /// internally, which cannot be modified on an HttpClient that has already sent a request.
    /// </summary>
    /// <param name="context">The Alexa context containing API access token.</param>
    /// <param name="request">The request containing the request ID.</param>
    /// <param name="message">The message to speak to the user.</param>
    /// <returns>A task representing the async operation.</returns>
    protected async Task SendProgressiveResponse(Context context, Request request, string message)
    {
        Logger.LogDebug("SendProgressiveResponse: sending message={Message}", message);
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
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
        return RetryHelper.ExecuteWithRetryAsync(operation, Logger, operationName, cancellationToken: cancellationToken, timeoutMs: AlexaRequestTimeoutMs);
    }

    /// <summary>
    /// Executes GetItemsResult with a fallback to GetItemList on NullReferenceException.
    /// Jellyfin's GetItemsResult evaluates dbQuery.Count() after applying query filters
    /// and ordering. Certain combinations (e.g. ArtistIds + PopularitySort referencing
    /// User data) cause EF Core's Count() translation to NRE. GetItemList skips the
    /// Count() step entirely.
    /// </summary>
    protected QueryResult<BaseItem> SafeGetItemsResult(ILibraryManager libraryManager, InternalItemsQuery query)
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
            Logger.LogWarning("GetItemsResult NRE — falling back to GetItemList");
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
    protected async Task<IReadOnlyList<T>> SearchWithAsrFallbackAsync<T>(
        string query,
        Func<string, Task<IReadOnlyList<T>>> searchFunc)
    {
        IReadOnlyList<T> results = await searchFunc(query).ConfigureAwait(false) ?? Array.Empty<T>();

        if (results.Count > 0)
        {
            return results;
        }

        if (!_config.AsrCompoundWordFixEnabled)
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
    protected T? FuzzyMatch<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, Entities.User? user = null, int threshold = -1)
        where T : class
    {
        int effectiveThreshold = threshold >= 0 ? threshold : FuzzyMatcher.GetDefaultThreshold(user);
        var result = FuzzyMatcher.FindBestMatch(query, candidates, selector, effectiveThreshold);
        Logger.LogDebug("FuzzyMatch: query={Query}, best={BestMatch}, threshold={Threshold}, matched={Matched}",
            query, result != null ? selector(result) : "(null)", effectiveThreshold, result != null);
        return result;
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
        Func<T, SkillResponse>? autoPlayFunc = null,
        Entities.User? user = null)
        where T : class
    {
        if (candidates == null || candidates.Count == 0)
        {
            Logger.LogDebug("HandleFuzzyMiss: no candidates for query={Query}", query);
            return (FuzzyMissOutcome.NotFound, null);
        }

        var bestWithScore = FuzzyMatcher.FindBestMatchWithScore(query, candidates, selector);

        if (bestWithScore == null || bestWithScore.Value.Item == null || bestWithScore.Value.Score < FuzzyMatcher.GetSuggestionThreshold(user))
        {
            Logger.LogDebug("HandleFuzzyMiss: query={Query}, candidates={CandidateCount}, best={BestMatch}, score={Score}, below suggestion threshold — not-found",
                query, candidates.Count,
                bestWithScore?.Item != null ? selector(bestWithScore.Value.Item) : "(null)",
                bestWithScore?.Score ?? 0);
            return (FuzzyMissOutcome.NotFound, null);
        }

        T best = bestWithScore.Value.Item;
        int score = bestWithScore.Value.Score;

        // High-confidence matches auto-accept regardless of FuzzyMatchBehavior.
        // Only borderline matches (SuggestionThreshold..DefaultThreshold) consult the per-user config.
        FuzzyMatchBehavior behavior = user?.FuzzyMatchBehavior ?? FuzzyMatchBehavior.Confirm;
        bool autoAccept = score >= FuzzyMatcher.GetDefaultThreshold(user)
            || (behavior == FuzzyMatchBehavior.AutoPlay && autoPlayFunc != null);

        if (autoAccept && autoPlayFunc != null)
        {
            Logger.LogDebug("HandleFuzzyMiss: query={Query}, best={BestMatch}, score={Score}, auto-accept=true — auto-playing",
                query, selector(best), score);
            SkillResponse? playResponse = autoPlayFunc(best);

            // autoPlayFunc may return null when the caller only uses it as a side-effect
            // to narrow the candidate list (e.g. PlayArtistSongsIntentHandler).
            if (playResponse == null)
            {
                return (FuzzyMissOutcome.SuggestionHandled, null);
            }

            // Near-exact or exact matches (score >= ContainmentScore) play directly without
            // the "closest match" qualifier — it would sound redundant.
            if (score >= FuzzyMatcher.ContainmentScore)
            {
                return (FuzzyMissOutcome.SuggestionHandled, playResponse);
            }

            string? ssml = GetSsml("FuzzyAutoPlayAnnouncementSsml", locale, selector(best), query);
            playResponse.Response.OutputSpeech = ssml != null
                ? new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
                : new PlainTextOutputSpeech { Text = ResponseStrings.Get("FuzzyAutoPlayAnnouncement", locale, selector(best), query) };
            return (FuzzyMissOutcome.SuggestionHandled, playResponse);
        }

        // Confirm mode: "Did you mean X?"
        Logger.LogDebug("HandleFuzzyMiss: query={Query}, best={BestMatch}, score={Score}, candidates={CandidateCount} — disambiguating",
            query, selector(best), score, candidates.Count);
        var matches = matchExtractor(best) ?? new List<(Guid, string)>();
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

        var matchInfos = matches.Select(m => new DisambiguationHelper.MatchInfo { Id = m.Id.ToString(), Name = m.Name }).ToList();
        response.SessionAttributes = new Dictionary<string, object>
        {
            ["disambig_matches"] = Newtonsoft.Json.JsonConvert.SerializeObject(matchInfos),
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
    /// Create a shuffled copy of a read-only list.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    protected static List<T> ShuffleCopy<T>(IReadOnlyList<T> source)
    {
        var copy = source.ToList();
        Shuffle(copy);
        return copy;
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
        Entities.User user,
        ILibraryManager libraryManager,
        CancellationToken cancellationToken)
    {
        var allResults = new List<BaseItem>();
        var seen = new HashSet<Guid> { current.Id };

        if (current.Genres != null && current.Genres.Length > 0)
        {
            var genreQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                Genres = current.Genres,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Limit = 50,
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(genreQuery, user, libraryManager, Logger);

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
    /// Query recently played items from Jellyfin and return them as display items
    /// suitable for an APL carousel. Deduplicates by name (keeps first = most recent),
    /// applies per-user library filtering, and respects feature flags for media types.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library access and image URL generation.</param>
    /// <param name="libraryManager">The library manager for querying items.</param>
    /// <param name="config">Plugin configuration for feature flags and server address.</param>
    /// <returns>A list of display items (empty, never null).</returns>
    private protected static List<Apl.ListDisplayItem> GetRecentlyPlayedItems(
        JellyfinUser jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        PluginConfiguration config)
    {
        var itemTypes = new List<BaseItemKind>();
        if (config.MusicEnabled)
        {
            itemTypes.Add(BaseItemKind.Audio);
        }

        if (config.VideosEnabled)
        {
            itemTypes.Add(BaseItemKind.Movie);
            itemTypes.Add(BaseItemKind.Episode);
        }

        if (config.BooksEnabled)
        {
            itemTypes.Add(BaseItemKind.AudioBook);
        }

        if (itemTypes.Count == 0)
        {
            return new List<Apl.ListDisplayItem>();
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = itemTypes.ToArray(),
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 20,
            DtoOptions = new DtoOptions(true)
        };

        ApplyLibraryFilter(query, user, libraryManager);

        IReadOnlyList<BaseItem> recentItems = libraryManager.GetItemList(query) ?? Array.Empty<BaseItem>();

        var results = new List<Apl.ListDisplayItem>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BaseItem item in recentItems)
        {
            if (results.Count >= 10)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                continue;
            }

            // Deduplicate by name to avoid "Song X" appearing twice
            if (!seenNames.Add(item.Name))
            {
                continue;
            }

            string subtitle = Apl.AplHelper.GetSubtitle(item);
            string artUrl = new Uri(new Uri(config.ServerAddress), "Items/" + item.Id + "/Images/Primary?api_key=" + user.JellyfinToken).ToString();

            results.Add(new Apl.ListDisplayItem(
                item.Name,
                item.Id.ToString(),
                subtitle,
                artUrl));
        }

        return results;
    }

    /// <summary>
    /// Find the most recently played item that has non-zero server-side progress
    /// (PlaybackPositionTicks > 0 and not marked as Played). Queries across the
    /// specified content types ordered by DatePlayed descending.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for query context.</param>
    /// <param name="libraryManager">The library manager for querying items.</param>
    /// <param name="userDataManager">The user data manager for progress lookup.</param>
    /// <param name="pluginUser">The plugin user for library access filtering.</param>
    /// <param name="contentTypes">The content types to search (e.g. Audio, Movie, Episode).</param>
    /// <param name="maxCandidates">Maximum items to scan (default 50).</param>
    /// <returns>The best resume candidate and its position ticks, or (null, 0) if none found.</returns>
    protected static (BaseItem? Item, long PositionTicks) FindLastPlayedItemWithProgress(
        JellyfinUser jellyfinUser,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Entities.User pluginUser,
        BaseItemKind[] contentTypes,
        ILogger? logger = null,
        int maxCandidates = 50)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = contentTypes,
            IsPlayed = false,
            MinDateLastSavedForUser = DateTime.UtcNow.AddDays(-30),
            Limit = maxCandidates,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, pluginUser, libraryManager, logger);

        IReadOnlyList<BaseItem> recentItems = libraryManager.GetItemList(query);

        logger?.LogDebug(
            "FindLastPlayedItemWithProgress: found {Count} recently-played items for user {UserId}",
            recentItems.Count, jellyfinUser.Id);

        foreach (BaseItem item in recentItems)
        {
            UserItemData? userData = userDataManager.GetUserData(jellyfinUser, item);
            if (userData == null || userData.PlaybackPositionTicks <= 0)
            {
                continue;
            }

            logger?.LogDebug(
                "FindLastPlayedItemWithProgress: found item '{Name}' ({Id}) with positionTicks={Ticks}",
                item.Name, item.Id, userData.PlaybackPositionTicks);

            return (item, userData.PlaybackPositionTicks);
        }

        logger?.LogDebug("FindLastPlayedItemWithProgress: no item with progress found");
        return (null, 0);
    }

    /// <summary>
    /// Find the resume track index with only UserData (no ItemPositionState).
    /// Delegates to the full overload with null queueManager.
    /// </summary>
    protected static (int Index, long PositionTicks) FindResumeTrackIndex(
        IReadOnlyList<BaseItem> tracks,
        JellyfinUser jellyfinUser,
        IUserDataManager userDataManager,
        bool resumePosition,
        ILogger? logger = null)
        => FindResumeTrackIndex(tracks, jellyfinUser, userDataManager, null, null, resumePosition, logger);

    /// <summary>
    /// Find the resume track index, checking ItemPositionState first
    /// (bypasses Jellyfin's MinAudiobookResume threshold), then UserData.
    /// When queueManager/deviceId are null, skips the ItemPositionState check.
    /// </summary>
    protected static (int Index, long PositionTicks) FindResumeTrackIndex(
        IReadOnlyList<BaseItem> tracks,
        JellyfinUser jellyfinUser,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        string? deviceId,
        bool resumePosition,
        ILogger? logger = null)
    {
        Playback.DeviceQueue? queue = queueManager != null && deviceId != null
            ? queueManager.GetOrCreateQueue(deviceId)
            : null;
        int lastPlayedIndex = -1;

        for (int i = 0; i < tracks.Count; i++)
        {
            // Check ItemPositionState first (bypasses MinAudiobookResume threshold)
            if (queue != null)
            {
                string itemIdStr = tracks[i].Id.ToString("N");
                if (queue.ItemPositionState.TryGetValue(itemIdStr, out long cachedTicks) && cachedTicks > 0)
                {
                    logger?.LogDebug(
                        "FindResumeTrackIndex: found ItemPositionState for track[{Idx}] '{Name}' — ticks={Ticks}",
                        i, tracks[i].Name, cachedTicks);
                    return (i, resumePosition ? cachedTicks : 0);
                }
            }

            // Fall back to Jellyfin UserData
            UserItemData? data = userDataManager.GetUserData(jellyfinUser, tracks[i]);
            if (data == null)
            {
                continue;
            }

            if (data.PlaybackPositionTicks > 0 && !data.Played)
            {
                logger?.LogDebug(
                    "FindResumeTrackIndex: found UserData for track[{Idx}] '{Name}' — ticks={Ticks}",
                    i, tracks[i].Name, data.PlaybackPositionTicks);
                return (i, resumePosition ? data.PlaybackPositionTicks : 0);
            }

            if (data.Played && lastPlayedIndex < i)
            {
                lastPlayedIndex = i;
            }
        }

        if (lastPlayedIndex >= 0 && lastPlayedIndex + 1 < tracks.Count)
        {
            logger?.LogDebug(
                "FindResumeTrackIndex: no in-progress track, resuming after last played[{Idx}] '{Name}'",
                lastPlayedIndex, tracks[lastPlayedIndex].Name);
            return (lastPlayedIndex + 1, 0);
        }

        logger?.LogDebug("FindResumeTrackIndex: no resume position found, starting from beginning");
        return (0, 0);
    }

    /// <summary>
    /// Escapes special XML characters in text for safe inclusion in SSML.
    /// </summary>
    /// <param name="text">The text to escape.</param>
    /// <returns>The XML-escaped text.</returns>
    internal static string EscapeXml(string text)
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
    private protected void TryAttachListDirective(
        SkillResponse response,
        Context? context,
        string title,
        List<Apl.ListDisplayItem> items,
        string token,
        string action = "selectItem",
        bool hasMore = false)
    {
        if (!Apl.AplHelper.VisualsEnabled)
        {
            Logger.LogDebug("APL list skipped for '{Token}': visuals disabled in config", token);
            return;
        }

        if (!Apl.AplHelper.DeviceSupportsApl(context))
        {
            var keys = context?.System?.Device?.SupportedInterfaces?.Keys;
            Logger.LogDebug("APL list skipped for '{Token}': device does not support APL. Interfaces: {Interfaces}", token, keys != null ? string.Join(", ", keys) : "null");
            return;
        }

        var directive = Apl.AplHelper.BuildListDirective(title, items, token, action, context, hasMore);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);
        }
        else
        {
            Logger.LogWarning("APL BuildListDirective returned null for '{Token}' with {Count} items", token, items.Count);
        }
    }

    /// <summary>
    /// Attach an APL image carousel directive to a response when the device supports APL.
    /// No-op on non-APL devices or when visuals are disabled.
    /// </summary>
    private protected void TryAttachCarouselDirective(
        SkillResponse response,
        Context? context,
        string title,
        List<Apl.ListDisplayItem> items,
        string token = "carousel",
        string locale = "en-US")
    {
        if (!Apl.AplHelper.VisualsEnabled)
        {
            Logger.LogDebug("APL carousel skipped for '{Token}': visuals disabled in config", token);
            return;
        }

        if (!Apl.AplHelper.DeviceSupportsApl(context))
        {
            var keys = context?.System?.Device?.SupportedInterfaces?.Keys;
            Logger.LogDebug("APL carousel skipped for '{Token}': device does not support APL. Interfaces: {Interfaces}", token, keys != null ? string.Join(", ", keys) : "null");
            return;
        }

        var directive = Apl.AplHelper.BuildCarouselDirective(title, items, token, context);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);

            // Interactive APL directives require an open session to receive SendEvent callbacks.
            if (response.Response.ShouldEndSession == true)
            {
                response.Response.ShouldEndSession = false;
                string repromptText = ResponseStrings.Get("CarouselReprompt", locale);
                if (response.Response.Reprompt == null && !string.IsNullOrEmpty(repromptText))
                {
                    response.Response.Reprompt = new Reprompt(repromptText);
                }
            }
        }
        else
        {
            Logger.LogWarning("APL BuildCarouselDirective returned null for '{Token}' with {Count} items", token, items.Count);
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

    /// <summary>
    /// Format a TimeSpan into a locale-aware voice-friendly string (e.g. "1 hours and 30 minutes").
    /// Uses ResponseStrings for localized templates.
    /// </summary>
    protected static string FormatTimeSpan(TimeSpan span, string locale)
    {
        if (span.TotalHours >= 1)
        {
            return ResponseStrings.Get("HoursAndMinutes", locale, (int)span.TotalHours, span.Minutes);
        }

        if (span.TotalMinutes >= 1)
        {
            return ResponseStrings.Get("MinutesAndSeconds", locale, (int)span.TotalMinutes, span.Seconds);
        }

        return ResponseStrings.Get("SecondsOnly", locale, span.Seconds);
    }

    /// <summary>
    /// Build a locale-aware position string from session state.
    /// Returns "X of Y" when runtime is known, just the position otherwise, or empty when position is 0/unavailable.
    /// </summary>
    protected static string BuildPositionDisplay(SessionInfo session, string locale)
    {
        if (session.PlayState?.PositionTicks == null || session.PlayState.PositionTicks.Value <= 0)
        {
            return string.Empty;
        }

        var position = TimeSpan.FromTicks(session.PlayState.PositionTicks.Value);
        string positionStr = FormatTimeSpan(position, locale);

        long? runtimeTicks = session.NowPlayingItem?.RunTimeTicks;
        if (runtimeTicks.HasValue && runtimeTicks.Value > 0)
        {
            var runtime = TimeSpan.FromTicks(runtimeTicks.Value);
            return ResponseStrings.Get("PositionOfTotal", locale, positionStr, FormatTimeSpan(runtime, locale));
        }

        return positionStr;
    }
}
