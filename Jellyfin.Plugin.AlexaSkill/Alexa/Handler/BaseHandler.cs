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
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using AlexaSession = Alexa.NET.Request.Session;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Base handler class to handle skill requests.
/// </summary>
/// <remarks>
/// Handlers are registered as DI singletons (Registrator registers each handler type as
/// BaseHandler), so they MUST be stateless: no per-request mutable instance fields, or
/// concurrent Alexa requests will race on them. Injected dependencies should be readonly.
/// Per-request state belongs in the session/request parameters, not handler fields.
/// </remarks>
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
    /// Minimum fuzzy-match score required for a cross-media-type artist fallback
    /// (album/song not found → play an artist instead). Higher than the normal
    /// default threshold because a wrong-artist false positive is worse than a
    /// clean "not found" — the observed false positives ("la ballata del genesio"
    /// → "Lamb", "disco jazz caffè" → "Uazz") both scored 75. Apply via
    /// <c>Math.Max(FuzzyMatcher.GetDefaultThreshold(user), CrossMediaArtistThreshold)</c>
    /// so a user who raised FuzzyMatchThreshold is still respected. Shared by the
    /// PlayAlbum and PlaySong cross-media fallbacks (JF-339).
    /// </summary>
    protected const int CrossMediaArtistThreshold = 85;

    /// <summary>
    /// Maximum word count for a cross-media artist fallback query. A long query is a
    /// poor artist query and a wrong-artist false positive is worse than a clean
    /// "not found" (observed: "la ballata del genesio" → "Lamb"). Shared by the PlaySong
    /// cross-media fallback and the greedy-intent TryEntityFallbackAsync.
    /// </summary>
    protected const int CrossMediaArtistMaxWords = 2;

    /// <summary>
    /// JF-345: minimum fuzzy-match score for the song-to-album cascade (a bare
    /// "play abbey road" in the free-text locales routes to PlaySong, misses,
    /// and used to dead-end in a song not-found). Containment-grade (equal to
    /// <see cref="FuzzyMatcher.ContainmentScore"/>), deliberately STRICTER than the
    /// artist cascade's <see cref="CrossMediaArtistThreshold"/> because song/album
    /// name overlap is far more common than artist/mood overlap: only a near-exact
    /// album name substitutes. Apply via
    /// <c>Math.Max(FuzzyMatcher.GetDefaultThreshold(user), CrossMediaAlbumThreshold)</c>
    /// so a user who raised FuzzyMatchThreshold is still respected.
    /// </summary>
    protected const int CrossMediaAlbumThreshold = 90;

    /// <summary>
    /// Shared word-count guard for BOTH cross-media fallback gates (artist and album):
    /// tokenizes the slot text with the locale stop-word set and rejects queries whose
    /// content words exceed <see cref="CrossMediaArtistMaxWords"/> (a long query is a
    /// poor artist AND a poor album guess; JF-295's original rationale, JF-446 consolidation,
    /// JF-345 extension to the album gate).
    /// </summary>
    /// <param name="slotText">The raw slot text.</param>
    /// <param name="locale">The request locale (stop-word set selection).</param>
    /// <param name="fallbackNoun">The fallback noun for the skip log ("artist"/"album").</param>
    /// <param name="logLabel">Log label.</param>
    /// <param name="tokens">The content-word tokens when the guard passes.</param>
    /// <returns>True when the query is short enough to attempt the fallback.</returns>
    protected bool PassesCrossMediaWordGuard(string slotText, string locale, string fallbackNoun, string logLabel, out string[] tokens)
    {
        tokens = Util.KeywordMatcher.Tokenize(slotText, locale);
        if (tokens.Length == 0 || tokens.Length > CrossMediaArtistMaxWords)
        {
            Logger.LogDebug(
                "{Label}: skipping {Noun} fallback, {Count} content words in '{Query}' (guard {Max})",
                logLabel, fallbackNoun, tokens.Length, slotText, CrossMediaArtistMaxWords);
            return false;
        }

        return true;
    }

    /// <summary>
    /// JF-465: the ONE Layer-1 warming-gate preamble (JF-419 family). While the index
    /// is present but still loading, this refuses the request at entry (throws
    /// <see cref="Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions.SkillWarmingUpException"/>,
    /// translated once by the request pipeline into the session-ending SkillWarmingUp
    /// Tell) instead of letting the handler fall to the cold database path that can
    /// exceed Alexa's ~8-second response window (live incident 2026-08-31 07:59).
    /// Call at handler entry, BEFORE the "searching" progressive response (no
    /// announcement-then-refusal) and AFTER any cancel-word escape hatch (an open
    /// Dialog flow must still cancel during warming). A null or disabled index
    /// degrades (no gate). Layer 2 (the ArtistSearch choke point) still covers every
    /// unguarded caller. The gated-handler roster is asserted by WarmingGateCoverageTests:
    /// gating a new handler requires adding it there.
    /// </summary>
    /// <param name="artistIndex">The artist index the handler's request path uses.</param>
    protected static void GuardIndexReady(IArtistIndex? artistIndex) => IndexWarmingGate.EnsureReady(artistIndex);

    /// <summary>
    /// The song n-gram overload of <see cref="GuardIndexReady(IArtistIndex)"/>: the
    /// per-path gate for handlers whose fast resource IS the song n-gram index.
    /// </summary>
    /// <param name="songIndex">The song n-gram index the handler's request path uses.</param>
    protected static void GuardIndexReady(ISongNgramIndex? songIndex) => IndexWarmingGate.EnsureReady(songIndex);

    /// <summary>
    /// JF-439: minimum KeywordMatcher score for the inverse cross-media song
    /// fallback to auto-play (the BaseHandler home since JF-440). Live calibration
    /// (minix, 12766 songs): the WRONG half-coverage phonetic hit ('rolling
    /// stones' -> 'Like a Rolling Stone') scores ~34; the RIGHT near-full phonetic
    /// match ('screenwriters blues' -> 'Screenwriter's Blues') scores ~72; exact
    /// full coverage scores ~105. The bar at 65 keeps 31 points of rejection margin
    /// over the wrong-substitution class and 7 over the legitimate phonetic class.
    /// The artist-side mirror gates fuzzy scores at 85 (different scale, not shared).
    /// </summary>
    protected const double CrossMediaSongThreshold = 65.0;

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
    /// Get a video-audio URL that combines album art with audio into an HLS stream
    /// for Echo Show VideoApp playback with native progress bar controls.
    /// HLS provides correct duration and seek support from the very first play.
    /// </summary>
    /// <param name="itemId">Id of the audio item.</param>
    /// <returns>URL to the HLS video-audio endpoint.</returns>
    public string GetVideoAudioUrl(string itemId)
        => new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/{itemId}/stream.m3u8?token={StreamTokenHelper.Mint(itemId, _config.StreamTokenSecret)}").ToString();

    /// <summary>
    /// Get a video-audio URL for an audiobook that concatenates all chapters into
    /// one continuous HLS stream. The parent ID is the book folder containing all
    /// AudioBook chapter items. Segments are served by the existing segment endpoint
    /// using the parent GUID as the cache key (no collision with single-item entries).
    /// </summary>
    /// <param name="parentId">Id of the audiobook parent folder.</param>
    /// <returns>URL to the audiobook HLS concat endpoint.</returns>
    public string GetAudiobookVideoAudioUrl(string parentId)
        => new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/audiobook/{parentId}/stream.m3u8?token={StreamTokenHelper.Mint(parentId, _config.StreamTokenSecret)}").ToString();

    /// <summary>
    /// Get a resume-aware audiobook HLS URL with a start-position hint. The endpoint reads
    /// <c>?start=&lt;ticks&gt;</c> and injects <c>#EXT-X-START</c> into the playlist so VideoApp
    /// can resume at position (VideoApp.Launch has no offset parameter of its own).
    /// </summary>
    /// <param name="parentId">Id of the audiobook parent folder.</param>
    /// <param name="startTicks">Resume position in .NET ticks.</param>
    /// <returns>URL to the resume-aware audiobook HLS endpoint.</returns>
    public string GetAudiobookResumeUrl(string parentId, long startTicks)
        => new Uri(new Uri(_config.ServerAddress), $"alexaskill/api/video-audio/audiobook/{parentId}/stream.m3u8?start={startTicks}&token={StreamTokenHelper.Mint(parentId, _config.StreamTokenSecret)}").ToString();

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
    /// Build a pause response: AudioPlayer.Stop with session ended.
    /// Alexa routes resume to the skill automatically when audio was playing.
    /// </summary>
    public static SkillResponse BuildPauseResponse()
    {
        var response = ResponseBuilder.AudioPlayerStop();
        response.Response.ShouldEndSession = true;
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
    /// Build a Dialog.ElicitSlot response (context-preserving elicit): the session stays
    /// inside the target intent's dialog, so the user's next utterance fills the named
    /// slot and already-filled slots survive the round-trip. The directive's
    /// updatedIntent declares EVERY slot of the target intent (allSlotNames) because
    /// Amazon rejects a partial updatedIntent (live INVALID_RESPONSE 2026-08-28 21:17:
    /// "All slots must be defined when sending updated intent in the Dialog.ElicitSlot
    /// directive"). The target intent must be registered in the model's dialog.intents
    /// with elicitationRequired=false (manual dialog control, CLAUDE.md anti-pattern #9).
    /// </summary>
    /// <param name="intentName">The intent whose dialog the elicit continues.</param>
    /// <param name="slotToElicit">The slot the user's next utterance fills.</param>
    /// <param name="allSlotNames">Every slot of the target intent, for updatedIntent.</param>
    /// <param name="prompt">The spoken question (also used as the reprompt: every current elicit repeats its question when the user stays silent).</param>
    /// <param name="sessionAttributes">Session state for a flow that owns state (e.g. FindSong's search state); leave null when the dialog lives Amazon-side.</param>
    /// <param name="activeFlowKeys">The calling flow's session keys (JF-398 mutual exclusion); pass none when the elicit owns no flow state of its own.</param>
    /// <returns>The elicitation response.</returns>
    protected static SkillResponse BuildElicitSlotResponse(
        string intentName,
        string slotToElicit,
        string[] allSlotNames,
        string prompt,
        Dictionary<string, object>? sessionAttributes = null,
        params string[] activeFlowKeys)
    {
        var response = new SkillResponse
        {
            Version = "1.0",
            SessionAttributes = sessionAttributes,
            Response = new ResponseBody
            {
                ShouldEndSession = false,
                OutputSpeech = new PlainTextOutputSpeech { Text = prompt },
                Reprompt = new Reprompt(prompt),
                Directives = new List<IDirective> { new ElicitSlotDirective(slotToElicit, intentName, allSlotNames) }
            }
        };

        ConversationalFlows.MarkOthersInactive(response, activeFlowKeys);
        return response;
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
        return BuildAudioPlayerResponse(playBehavior, streamUrl, itemId, item, user, null, offsetInMilliseconds, announceLocale: null);
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
    public SkillResponse BuildAudioPlayerResponse(PlayBehavior playBehavior, string streamUrl, string itemId, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User user, Context? context, int offsetInMilliseconds = 0, string? announceLocale = null)
    {
        // Record the last user-initiated play for this device (ReplaceAll = a new item starts).
        // This is the universal chokepoint: every play path flows through here, including APL
        // carousel taps and resume confirmations that bypass SetQueue. Captures VideoApp.Launch
        // plays too (which don't update context.AudioPlayer.Token), giving LaunchRequestHandler
        // a reliable device-specific "what did this Echo last play" signal.
        if (playBehavior == PlayBehavior.ReplaceAll)
        {
            string? deviceId = context?.System?.Device?.DeviceID;
            if (!string.IsNullOrEmpty(deviceId))
            {
                Plugin.Instance?.DeviceQueueManager?.RecordLastPlayed(deviceId, itemId);
            }
        }

        // Route initial playback through VideoApp when native controls are enabled for the
        // item's category. Enqueue/ReplaceEnqueued stay as AudioPlayer for queue building.
        // Resume (offset > 0) also stays as AudioPlayer since VideoApp has no offset support
        // (audiobook resume is handled separately via a resume-aware HLS playlist).
        // AudioBook items use a special concat HLS endpoint that joins all chapters into
        // one continuous stream, giving the full book duration in the seek bar.
        if (playBehavior == PlayBehavior.ReplaceAll && offsetInMilliseconds == 0)
        {
            bool wantsNativeControls = false;
            if (item != null)
            {
                if (item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal))
                {
                    wantsNativeControls = Plugin.Instance?.Configuration?.NativeControlsForBooks == true;
                }
                else if (item is MediaBrowser.Controller.Entities.Audio.Audio)
                {
                    wantsNativeControls = GetVideoAppForAudio(user);
                }
            }

            if (wantsNativeControls)
            {
                return BuildVideoAppAudioResponse(itemId, item, user, announceLocale);
            }
        }

        Logger.LogDebug("BuildAudioPlayerResponse: itemId={ItemId}, behavior={Behavior}, offsetMs={OffsetMs}, title={Title}, streamUrl={StreamUrl}",
            itemId, playBehavior, offsetInMilliseconds, item?.Name, RequestLogRedactor.RedactUrl(streamUrl));
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
            string cardTitle = item.Name ?? string.Empty;
            var parts = new List<string>();

            if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
            {
                string? artist = audio.Artists?.Count > 0 ? audio.Artists[0] : null;
                if (!string.IsNullOrEmpty(artist))
                {
                    parts.Add(artist);
                }

                if (!string.IsNullOrEmpty(audio.Album))
                {
                    string album = audio.Album;
                    if (audio.IndexNumber.HasValue)
                    {
                        album = $"#{audio.IndexNumber.Value} — {album}";
                    }

                    parts.Add(album);
                }
                else if (audio.IndexNumber.HasValue)
                {
                    parts.Add($"Track #{audio.IndexNumber.Value}");
                }
            }

            string cardContent = parts.Count > 0
                ? $"{cardTitle}\n{string.Join("\n", parts)}"
                : cardTitle;

            long runTimeTicks = item.RunTimeTicks ?? 0;
            if (runTimeTicks > 0)
            {
                string total = FormatPosition(runTimeTicks);
                if (offsetInMilliseconds > 0)
                {
                    long posTicks = (long)offsetInMilliseconds * TimeSpan.TicksPerMillisecond;
                    cardContent += $"\n{FormatPosition(posTicks)} / {total}";
                }
                else
                {
                    cardContent += $"\n0:00 / {total}";
                }
            }

            response.Response.Card = new StandardCard
            {
                Title = cardTitle,
                Content = cardContent
            };
        }

        AttachAnnounceIfEnabled(response, item, user, announceLocale, offsetInMilliseconds);
        return response;
    }

    /// <summary>
    /// Build a VideoApp.Launch response for audio playback using the video-audio
    /// endpoint, which combines album art with audio into a streamable MP4.
    /// Gives native progress bar / scrubber on Echo Show.
    /// For AudioBook items, uses a special concat HLS endpoint that joins all chapters
    /// into one continuous stream so the seek bar shows the full book duration.
    /// </summary>
    public SkillResponse BuildVideoAppAudioResponse(string itemId, BaseItem? item, Entities.User user, string? announceLocale = null)
    {
        bool isAudioBook = item != null && item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal);

        string videoAudioUrl;
        if (isAudioBook && item!.ParentId != Guid.Empty)
        {
            // Multi-chapter audiobook: use concat HLS endpoint keyed by parent book ID.
            // The endpoint concatenates all chapters into one continuous HLS stream,
            // giving the full book duration in the Echo Show seek bar.
            videoAudioUrl = GetAudiobookVideoAudioUrl(item.ParentId.ToString());
            Logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, parentId={ParentId}, title={Title}, url={Url} (audiobook concat)", itemId, item.ParentId, item.Name, videoAudioUrl);
        }
        else
        {
            videoAudioUrl = GetVideoAudioUrl(itemId);
            Logger.LogDebug("BuildVideoAppAudioResponse: itemId={ItemId}, title={Title}, url={Url}", itemId, item?.Name, videoAudioUrl);
        }

        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                // VideoApp.Launch must NOT include shouldEndSession — Alexa rejects it.
                // Null omits the field from JSON serialization.
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

        AttachAnnounceIfEnabled(response, item, user, announceLocale);
        return response;
    }

    /// <summary>
    /// Build a VideoApp.Launch response for an audiobook RESUME, pointing at the resume-aware
    /// HLS playlist (<c>?start=&lt;ticks&gt;</c>). The position is encoded in the playlist via
    /// <c>#EXT-X-START</c> — VideoApp.Launch has no offset parameter, so this keeps the seek bar
    /// AND resumes at position. Use the book's parent-folder ID for the concat stream.
    /// </summary>
    /// <param name="item">An audiobook chapter item (its ParentId is the book folder).</param>
    /// <param name="startTicks">Resume position in .NET ticks.</param>
    /// <returns>A VideoApp.Launch SkillResponse targeting the resume playlist.</returns>
    public SkillResponse BuildAudiobookResumeResponse(MediaBrowser.Controller.Entities.BaseItem item, long startTicks)
    {
        Guid parentId = item.ParentId != Guid.Empty ? item.ParentId : item.Id;
        string videoAudioUrl = GetAudiobookResumeUrl(parentId.ToString(), startTicks);

        Logger.LogDebug(
            "BuildAudiobookResumeResponse: itemId={ItemId}, parentId={ParentId}, startTicks={Ticks}, url={Url}",
            item.Id, parentId, startTicks, videoAudioUrl);

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                // VideoApp.Launch must NOT include shouldEndSession.
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
                                Title = item.Name ?? string.Empty,
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
    /// <param name="args">Optional format arguments. String values are interpolated into
    /// SSML as-is, so callers MUST pre-escape reserved XML chars with EscapeXml (unlike
    /// BuildOutputSpeech, which escapes internally).</param>
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
    /// Build an OutputSpeech using SSML with plaintext fallback. Tries the SSML key
    /// first; falls back to the plain key if SSML is unavailable. Callers pass RAW
    /// (unescaped) args: the SSML path escapes reserved XML chars here, while the
    /// plain-text fallback keeps them raw, so an ampersand in a title is spoken as
    /// a real ampersand rather than the escaped SSML entity.
    /// </summary>
    public static IOutputSpeech BuildOutputSpeech(string ssmlKey, string plainKey, string locale, params object[] args)
    {
        string? ssml = GetSsml(ssmlKey, locale, EscapeStringArgs(args));
        if (ssml != null)
        {
            return new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" };
        }

        return new PlainTextOutputSpeech { Text = ResponseStrings.Get(plainKey, locale, args) };
    }

    /// <summary>
    /// Build a session-opening Ask using SSML when available, with a plaintext fallback
    /// (JF-407 item 3). Consolidates the hand-written GetSsml-then-AskSsml-or-Ask
    /// pattern that was duplicated across DisambiguationHelper (3x),
    /// FallbackIntentHandler, LaunchRequestHandler, and BaseHandler (2x), where the
    /// reprompt-key handling and XML escaping drifted between sites. Args are RAW
    /// (unescaped): the SSML path escapes them internally, the plaintext path keeps
    /// them raw. The reprompt is always emitted as PlainText. The one behavior change
    /// from the sites it replaced (review 2026-08-29): the LaunchRequestHandler resume
    /// site previously used the AskSsml(string, string) overload, which wrapped the
    /// reprompt in speak tags (SSML output); this helper emits PlainText, which is
    /// strictly more robust (the old wrapping would produce INVALID SSML if a future
    /// localized reprompt contained a raw XML char) but is a wire-format difference.
    /// The Welcome flow's dual SSML prompt+reprompt stays hand-written in
    /// LaunchRequestHandler (it is the only site with an SSML reprompt variant).
    /// </summary>
    /// <param name="ssmlKey">The ResponseStrings key for the SSML prompt variant.</param>
    /// <param name="textKey">The ResponseStrings key for the plain-text prompt variant.</param>
    /// <param name="repromptKey">The ResponseStrings key for the plain-text reprompt.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="args">Format args for both prompt variants (raw, not XML-escaped).</param>
    /// <returns>A session-opening Ask response.</returns>
    public static SkillResponse AskLocalized(
        string ssmlKey, string textKey, string repromptKey, string locale, params object[] args)
    {
        string reprompt = ResponseStrings.Get(repromptKey, locale);
        string? ssml = GetSsml(ssmlKey, locale, EscapeStringArgs(args));
        if (ssml != null)
        {
            return AskSsml(ssml, new Reprompt(reprompt));
        }

        string prompt = ResponseStrings.Get(textKey, locale, args);
        return ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
    }

    /// <summary>
    /// Escape SSML-reserved chars in string args for safe interpolation into &lt;speak&gt;.
    /// Non-string args (counts, etc.) pass through unchanged.
    /// </summary>
    private static object[] EscapeStringArgs(object[] args)
    {
        if (args.Length == 0)
        {
            return args;
        }

        var escaped = new object[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            escaped[i] = args[i] is string s ? EscapeXml(s) : args[i];
        }

        return escaped;
    }

    /// <summary>
    /// The now-playing announce shared by every video-launch handler. Wraps
    /// BuildOutputSpeech with the NowPlaying SSML/plain keys; the title is escaped for SSML.
    /// </summary>
    public static IOutputSpeech? BuildNowPlayingSpeech(string name, string locale, bool announceOn = true)
        => announceOn ? BuildOutputSpeech("NowPlayingSsml", "NowPlaying", locale, name) : null;

    /// <summary>
    /// Attach the gated now-playing announce to a MUSIC play response when the caller passes a
    /// locale and the audio-announce toggle is on. Only music handlers pass announceLocale, so the
    /// gate is <see cref="GetAnnounceAudioPlays"/> (opt-in, default false per JF-352.4) — NOT the
    /// video/book <see cref="GetAnnounceNowPlaying"/> toggle. When offsetMs &gt; 0 the announce is a
    /// resume ("Resuming X") rather than a fresh "Now playing X". The OutputSpeech-occupied guard
    /// is idempotency only: callers that set a more specific announcement (e.g. FoundAlbumInstead)
    /// do so AFTER this call and overwrite it themselves.
    /// </summary>
    protected void AttachAnnounceIfEnabled(SkillResponse response, MediaBrowser.Controller.Entities.BaseItem? item, Entities.User? user, string? announceLocale, int offsetInMilliseconds = 0)
    {
        if (string.IsNullOrEmpty(announceLocale) || item is null || response.Response.OutputSpeech is not null)
        {
            return;
        }

        if (!GetAnnounceAudioPlays(user))
        {
            return;
        }

        response.Response.OutputSpeech = offsetInMilliseconds > 0
            ? BuildOutputSpeech("ResumingSsml", "Resuming", announceLocale, item.Name)
            : BuildNowPlayingSpeech(item.Name, announceLocale, announceOn: true);
    }

    /// <summary>
    /// Resume-aware video-launch announce: "Resuming X from Y" when the user has playback
    /// progress, else the now-playing announce. VideoApp.Launch cannot honor the offset, so
    /// this only informs the user where they left off (playback still starts from the beginning).
    /// The fresh-play announce (resumeTicks == 0) is suppressed when announceOn is false; the
    /// resume announce is always spoken (position info, not the now-playing readout).
    /// </summary>
    protected static IOutputSpeech? BuildVideoLaunchSpeech(BaseItem item, string locale, long resumeTicks, bool announceOn = true)
    {
        if (resumeTicks > 0)
        {
            return new PlainTextOutputSpeech(ResponseStrings.Get("ResumingVideo", locale, item.Name, FormatPosition(resumeTicks)));
        }

        return BuildNowPlayingSpeech(item.Name, locale, announceOn);
    }

    /// <summary>
    /// Resume-aware video-launch announce that fetches the playback position itself. Falls back
    /// to the (gated) now-playing announce if the deps are unavailable.
    /// </summary>
    protected static IOutputSpeech? BuildVideoLaunchSpeech(BaseItem item, string locale, IUserDataManager? userDataManager, Jellyfin.Database.Implementations.Entities.User? jellyfinUser, bool announceOn = true)
    {
        long resumeTicks = (userDataManager is not null && jellyfinUser is not null)
            ? (userDataManager.GetUserData(jellyfinUser, item)?.PlaybackPositionTicks ?? 0)
            : 0;
        return BuildVideoLaunchSpeech(item, locale, resumeTicks, announceOn);
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
    /// Use in handlers whose whole payoff is one media type (JF-467 gates the music-only
    /// handlers PlaySong/PlayAlbum/FindSong/PlayMoodMusic at entry this way): place the
    /// call AFTER the empty-slot prompt (a disabled user with no slot still gets the
    /// slot prompt) and BEFORE the first library query and the "searching" announcement.
    /// Reads Plugin.Instance live, so a standard config API replacement takes effect
    /// without a restart. Logs the disable at Information itself: callers need no
    /// second log line (the IfFeatureDisabled call sites follow the same bare idiom).
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
    /// Live read of the global music flag (global-only: no per-user override exists).
    /// Reads Plugin.Instance.Configuration FIRST so a standard-API configuration
    /// replacement takes effect without a restart (same read source as
    /// <see cref="IfMediaTypeDisabled"/> and <see cref="FilterByContentAccess"/>,
    /// JF-467 alignment); falls back to the injected configuration only when the
    /// plugin instance is absent (off-host unit tests), where
    /// <see cref="IfMediaTypeDisabled"/> instead returns null (allow).
    /// </summary>
    protected bool IsMusicEnabled => (Plugin.Instance?.Configuration ?? _config).MusicEnabled;

    /// <summary>
    /// Applies per-user library filtering to a query by setting TopParentIds.
    /// Resolves CollectionFolder IDs to physical folder IDs for correct filtering.
    /// No-op when the user has no library restrictions configured, and when every
    /// included kind lives outside any media library (playlists, live-TV channels:
    /// Util.LibraryFilter.IsOutOfLibraryKind), where the filter could only return
    /// zero rows. When the filter IS applied to a query that includes MusicArtist,
    /// the items-by-name bypass is set automatically (Util.LibraryFilter, JF-456).
    /// </summary>
    protected static void ApplyLibraryFilter(InternalItemsQuery query, Entities.User? user, ILibraryManager libraryManager, ILogger? logger = null)
        => Util.LibraryFilter.ApplyLibraryFilter(query, user, libraryManager, logger);

    /// <summary>
    /// Send a progressive response to keep the Alexa session alive during long operations.
    /// Resets the 8-second timeout. Only works with IntentRequest/LaunchRequest.
    /// Uses the dedicated HttpClientProgressive (factory-backed, fresh per call with a 2s
    /// timeout) because ProgressiveResponse sets BaseAddress internally, which cannot be
    /// modified on an HttpClient that has already sent a request.
    /// </summary>
    /// <remarks>
    /// This call is best-effort and non-critical: it is invoked FIRE-AND-FORGET from
    /// handler paths (via <see cref="RunFireAndForget"/>) so it never blocks the final
    /// handler response (50-200ms Alexa API round-trip). The entire body is wrapped in
    /// try/catch so the returned <see cref="Task"/> can never fault — callers MUST NOT
    /// await it inside request handlers. Use <see cref="RunFireAndForget"/> to discard
    /// the task safely and analyzer-cleanly (observes the result to avoid CA2012).
    /// </remarks>
    /// <param name="context">The Alexa context containing API access token.</param>
    /// <param name="request">The request containing the request ID.</param>
    /// <param name="message">The message to speak to the user.</param>
    /// <returns>A task representing the async operation. Always completes successfully (never faults).</returns>
    protected async Task SendProgressiveResponse(Context context, Request request, string message)
    {
        Logger.LogDebug("SendProgressiveResponse: sending message={Message}", message);
        try
        {
            // JF-314: use the dedicated progressive client (factory-backed, fresh per call, 2s timeout)
            var progressiveResponse = new ProgressiveResponse(
                context.System.ApiAccessToken,
                request.RequestId,
                context.System?.ApiEndpoint ?? "https://api.amazonalexa.com",
                Plugin.HttpClientProgressive);
            await progressiveResponse.SendSpeech(message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort ping: never propagate. Swallowing here guarantees the discarded
            // Task at call sites can never fault (no unobserved-exception escalation).
            Logger.LogWarning(ex, "Failed to send progressive response");
        }
    }

    /// <summary>
    /// Safely run a best-effort <see cref="Task"/> fire-and-forget without awaiting it.
    /// Attaches a continuation that observes the task's completion, which prevents
    /// CA2012 (unobserved task exceptions) and keeps the build analyzer-clean under
    /// TreatWarningsAsErrors + AllEnabledByDefault. The task is expected to already be
    /// self-protecting (its own try/catch), so the continuation only logs on the rare
    /// case where the task still faults despite that.
    /// </summary>
    /// <param name="task">The task to run without awaiting.</param>
    /// <param name="operationName">Optional label for diagnostic logging if the task faults.</param>
    protected void RunFireAndForget(Task task, string operationName = "FireAndForget")
    {
        // CA2007: ConfigureAwait(false) is the project convention for library code.
        // CA2012: observing via ContinueWith marks the exception as observed.
        task.ConfigureAwait(false)
            .GetAwaiter()
            .OnCompleted(() =>
            {
                if (task.IsFaulted)
                {
                    Logger.LogWarning(task.Exception, "{Operation} task faulted unexpectedly", operationName);
                }
            });
    }

    /// <summary>
    /// JF-425/JF-447: the ONE stop-report sequence shared by the stop-shaped event
    /// handlers (PlaybackStopped/Finished/Failed). Registers the stop for correction
    /// duty (the displacement classification is folded into RecordStop, so a null
    /// registration means the stop displaces an already-replaced stream and must not
    /// correct anything), reports it to the server with the registration completed in a
    /// finally (a correcting start report waits for it instead of firing a concurrent
    /// duplicate), and restores the new track's session entry when the stop was a
    /// displacement (its own server-side write cleared the entry the new track owns).
    /// Callers that need the displacement flag BEFORE building the stop info (Stopped
    /// zeroes the saved position) classify early and may pass their own reason.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (per-device ordering key).</param>
    /// <param name="rawToken">The event's raw stream token, for the displacement classification.</param>
    /// <param name="stopInfo">The stop report to send (replayed verbatim as the correction).</param>
    /// <param name="displacementRestoreReason">Reason stamped into the classification and restore logs.</param>
    /// <returns>A task representing the report and, for a displacement, the restore.</returns>
    protected async Task ReportStopOrderedAsync(string deviceId, string? rawToken, PlaybackStopInfo stopInfo, string displacementRestoreReason)
    {
        Playback.PlaybackReportOrdering.StopRegistration? registration =
            Playback.PlaybackReportOrdering.RecordStop(deviceId, rawToken, stopInfo);

        bool isDisplacement = registration == null;
        if (isDisplacement)
        {
            Logger.LogDebug(
                "{Reason}: displacement detected, item={Token} but the device's latest start is a different item; not recording the stop",
                displacementRestoreReason, rawToken);
        }

        try
        {
            await SessionManager.OnPlaybackStopped(stopInfo).ConfigureAwait(false);
        }
        finally
        {
            registration?.MarkReportCompleted();
        }

        if (isDisplacement)
        {
            await Playback.PlaybackReportOrdering.RestoreCurrentStartAsync(
                SessionManager, deviceId, Logger, displacementRestoreReason).ConfigureAwait(false);
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
    protected T? FuzzyMatchPhonetic<T>(string query, IEnumerable<T> candidates, Func<T, string> selector, Func<T, Guid> idSelector, IArtistIndex? artistIndex, Entities.User? user = null, int threshold = -1)
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

        Logger.LogDebug("FuzzyMatchPhonetic: query={Query}, best={BestMatch}, threshold={Threshold}, matched={Matched}",
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
    protected async Task<IReadOnlyList<BaseItem>> GetArtistSongsAsync(
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

        ApplyLibraryFilter(query, user, libraryManager, Logger);

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
    /// <param name="itemTypes">The item types to search (e.g. Audio, MusicAlbum). Queries whose kinds are ALL out-of-library skip the TopParentIds filter (<see cref="Util.LibraryFilter.IsOutOfLibraryKind"/>, JF-456).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="operationLabel">Label for logging.</param>
    /// <returns>The best match + score, or null if nothing above threshold.</returns>
    protected async Task<(BaseItem Item, int Score)?> SearchItemsFuzzyAsync(
        string query,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User user,
        ILibraryManager libraryManager,
        BaseItemKind[] itemTypes,
        CancellationToken cancellationToken,
        string operationLabel = "FuzzyFallback",
        Guid[]? artistIds = null,
        int minQueryLength = 3,
        MediaType[]? mediaTypes = null)
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

        ApplyLibraryFilter(fallbackQuery, user, libraryManager, Logger);

        IReadOnlyList<BaseItem> allItems = await RetryAsync(
            () => libraryManager.GetItemList(fallbackQuery),
            operationLabel,
            cancellationToken).ConfigureAwait(false);

        if (allItems.Count == 0)
        {
            return null;
        }

        var match = FuzzyMatcher.FindBestMatchWithScore(query, allItems, item => item.Name);
        if (match.HasValue && match.Value.Score >= FuzzyMatcher.GetDefaultThreshold(user))
        {
            Logger.LogInformation(
                "{Op}: fuzzy fallback matched '{Name}' score={Score} for query='{Query}'",
                operationLabel, match.Value.Item.Name, match.Value.Score, query);
            return match;
        }

        return null;
    }

    /// <summary>
    /// Gets the effective search response mode for a user, falling back to the global default.
    /// Per-user setting (when explicitly set, i.e. non-null) takes precedence.
    /// </summary>
    protected SearchResponseMode GetSearchResponseMode(Entities.User? user)
    {
        if (user?.SearchResponseMode.HasValue == true)
        {
            Logger.LogDebug("SearchResponseMode: user={UserId} mode={Mode} source=PerUser", user.Id, user.SearchResponseMode.Value);
            return user.SearchResponseMode.Value;
        }

        Logger.LogDebug("SearchResponseMode: user={UserId} mode={Mode} source=GlobalDefault", user?.Id, _config.DefaultSearchResponseMode);
        return _config.DefaultSearchResponseMode;
    }

    /// <summary>
    /// Gets the effective post-play behavior for a user, falling back to the global default.
    /// Per-user setting (when explicitly set, i.e. non-null) takes precedence.
    /// </summary>
    protected PostPlayBehavior GetPostPlayBehavior(Entities.User? user)
    {
        if (user?.PostPlayBehavior is { } userBehavior)
        {
            Logger.LogDebug("PostPlayBehavior: user={UserId} mode={Mode} source=PerUser", user.Id, userBehavior);
            return userBehavior;
        }

        Logger.LogDebug("PostPlayBehavior: user={UserId} mode={Mode} source=GlobalDefault", user?.Id, _config.DefaultPostPlayBehavior);
        return _config.DefaultPostPlayBehavior;
    }

    /// <summary>
    /// Gets the effective cross-media artist suggestion behavior for a user, falling back to
    /// the global default. Per-user setting (when explicitly set, i.e. non-null) takes
    /// precedence. Controls whether a sub-strict-threshold artist match (found when a
    /// song/album wasn't) is offered for confirmation, auto-served, or ignored.
    /// </summary>
    protected CrossMediaArtistSuggestion GetCrossMediaArtistSuggestion(Entities.User? user)
    {
        if (user?.CrossMediaArtistSuggestion is { } userMode)
        {
            Logger.LogDebug("CrossMediaArtistSuggestion: user={UserId} mode={Mode} source=PerUser", user.Id, userMode);
            return userMode;
        }

        Logger.LogDebug("CrossMediaArtistSuggestion: user={UserId} mode={Mode} source=GlobalDefault", user?.Id, _config.DefaultCrossMediaArtistSuggestion);
        return _config.DefaultCrossMediaArtistSuggestion;
    }

    /// <summary>
    /// Builds the cross-media artist OFFER response (Ask): "I didn't find a song/album
    /// '{query}'. Did you mean the artist {artist}?". Keeps the session open carrying the
    /// single best artist in the standard disambiguation session state, so YesIntentHandler
    /// routes a "yes" to PlayArtist unchanged. Also stashes the original not-found query +
    /// media type so NoIntentHandler can produce the correct clean not-found when the user
    /// declines (otherwise "no" would say "no more matches", which is wrong here). Single
    /// candidate only (per JF-363 design).
    /// </summary>
    /// <param name="query">The not-found song/album query (the raw slot value).</param>
    /// <param name="artist">The single best artist match to offer.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="notFoundMediaType">The media type the user originally asked for:
    /// exactly <see cref="DisambiguationHelper.MediaTypeSong"/> or
    /// <see cref="DisambiguationHelper.MediaTypeAlbum"/> (the wire value NoIntentHandler
    /// switches on for the decline response).</param>
    protected SkillResponse BuildCrossMediaArtistOfferAsk(string query, BaseItem artist, string locale, string notFoundMediaType)
    {
        // Const guard (JF-446 simplify): the attribute written below is compared verbatim
        // by NoIntentHandler's decline path, so only the two MediaType consts are valid.
        if (notFoundMediaType != DisambiguationHelper.MediaTypeSong
            && notFoundMediaType != DisambiguationHelper.MediaTypeAlbum)
        {
            Logger.LogWarning(
                "CrossMediaArtistSuggestion: unexpected notFoundMediaType '{MediaType}' (must be MediaTypeSong or MediaTypeAlbum); the decline path will speak the song not-found",
                notFoundMediaType);
        }

        SkillResponse response = AskLocalized(
            "CrossMediaArtistOfferSsml", "CrossMediaArtistOffer", "FuzzySuggestionReprompt", locale, query, artist.Name);

        var matchInfos = new List<DisambiguationHelper.MatchInfo>
        {
            new() { Id = artist.Id.ToString(), Name = artist.Name }
        };

        response.SessionAttributes = DisambiguationHelper.BuildAttributes(
            matchInfos,
            0,
            DisambiguationHelper.MediaTypeArtist,
            // JF-363: carry the original not-found request so NoIntentHandler can decline to
            // the right "song/album not found" instead of the generic "no more matches".
            (DisambiguationHelper.AttrCrossmediaQuery, query),
            (DisambiguationHelper.AttrCrossmediaType, notFoundMediaType));

        // JF-398: activating the cross-media artist offer (a disambiguation flavor)
        // supersedes any other flow's state.
        ConversationalFlows.MarkOthersInactive(response, ConversationalFlows.DisambiguationKeys);

        Logger.LogDebug(
            "CrossMediaArtistSuggestion: offering artist '{Artist}' for not-found query='{Query}' (type={Type})",
            artist.Name, query, notFoundMediaType);
        return response;
    }

    /// <summary>
    /// Gets the effective "speak the now-playing announce on launch" preference for a user,
    /// falling back to the global default. Per-user setting (when explicitly set) takes precedence.
    /// </summary>
    protected bool GetAnnounceNowPlaying(Entities.User? user)
    {
        if (user?.AnnounceNowPlaying is { } userPref)
        {
            Logger.LogDebug("AnnounceNowPlaying: user={UserId} on={On} source=PerUser", user.Id, userPref);
            return userPref;
        }

        Logger.LogDebug("AnnounceNowPlaying: user={UserId} on={On} source=GlobalDefault", user?.Id, _config.DefaultAnnounceNowPlaying);
        return _config.DefaultAnnounceNowPlaying;
    }

    /// <summary>
    /// Gets the effective "speak the now-playing announce on MUSIC plays" preference for a user,
    /// falling back to the global <see cref="Configuration.PluginConfiguration.AnnounceAudioPlays"/>
    /// default (false — audio plays are silent by default, JF-352.4). Per-user setting takes
    /// precedence. Video/book launches use <see cref="GetAnnounceNowPlaying"/> instead.
    /// </summary>
    protected bool GetAnnounceAudioPlays(Entities.User? user)
    {
        if (user?.AnnounceAudioPlays is { } userPref)
        {
            Logger.LogDebug("AnnounceAudioPlays: user={UserId} on={On} source=PerUser", user.Id, userPref);
            return userPref;
        }

        Logger.LogDebug("AnnounceAudioPlays: user={UserId} on={On} source=GlobalDefault", user?.Id, _config.AnnounceAudioPlays);
        return _config.AnnounceAudioPlays;
    }

    /// <summary>
    /// Gets the effective "play music via VideoApp" preference for a user, falling back to the
    /// global <see cref="Configuration.PluginConfiguration.NativeControlsForAudio"/> default.
    /// Per-user setting (when explicitly set, i.e. non-null) takes precedence. When true, music
    /// (Audio items) is routed through VideoApp.Launch (native seek bar, ffmpeg video-audio
    /// encode); when false, music uses plain AudioPlayer.Play with the raw stream URL. Audiobooks
    /// are governed by <c>NativeControlsForBooks</c> and are not affected by this resolver.
    /// </summary>
    protected bool GetVideoAppForAudio(Entities.User? user)
    {
        if (user?.VideoAppForAudio.HasValue == true)
        {
            Logger.LogDebug("VideoAppForAudio: user={UserId} value={Value} source=PerUser", user.Id, user.VideoAppForAudio.Value);
            return user.VideoAppForAudio.Value;
        }

        bool global = _config.NativeControlsForAudio;
        Logger.LogDebug("VideoAppForAudio: user={UserId} value={Value} source=GlobalDefault", user?.Id, global);
        return global;
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

            string? ssml = GetSsml("FuzzyAutoPlayAnnouncementSsml", locale, EscapeXml(selector(best)), EscapeXml(query));
            playResponse.Response.OutputSpeech = ssml != null
                ? new SsmlOutputSpeech { Ssml = $"<speak>{ssml}</speak>" }
                : new PlainTextOutputSpeech { Text = ResponseStrings.Get("FuzzyAutoPlayAnnouncement", locale, selector(best), query) };
            return (FuzzyMissOutcome.SuggestionHandled, playResponse);
        }

        // Confirm mode: "Did you mean X?"
        Logger.LogDebug("HandleFuzzyMiss: query={Query}, best={BestMatch}, score={Score}, candidates={CandidateCount} — disambiguating",
            query, selector(best), score, candidates.Count);
        var matches = matchExtractor(best) ?? new List<(Guid, string)>();
        SkillResponse response = AskLocalized(
            "FuzzySuggestionPromptSsml", "FuzzySuggestionPrompt", "FuzzySuggestionReprompt", locale, query, selector(best));

        var matchInfos = matches.Select(m => new DisambiguationHelper.MatchInfo { Id = m.Id.ToString(), Name = m.Name }).ToList();
        response.SessionAttributes = DisambiguationHelper.BuildAttributes(matchInfos, 0, mediaType);

        // JF-398: activating the disambiguation flow supersedes any other flow's state.
        ConversationalFlows.MarkOthersInactive(response, ConversationalFlows.DisambiguationKeys);
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
    /// Rebuilds <paramref name="session"/>'s <c>NowPlayingQueue</c> from a
    /// <see cref="Playback.DeviceQueue"/>'s current (possibly reshuffled) item
    /// order, preserving <c>PlaylistItemId</c> and other metadata on items that
    /// already exist. Used by the shuffle handlers so that
    /// <c>PlaybackNearlyFinishedEventHandler.ResolveNextItemId</c> advances
    /// through the shuffled order rather than the original one.
    /// </summary>
    /// <param name="queue">The device queue whose item order to mirror.</param>
    /// <param name="session">The Jellyfin session whose NowPlayingQueue to rebuild.</param>
    protected static void MirrorQueueToSession(Playback.DeviceQueue queue, SessionInfo session)
    {
        if (queue.ItemIds.Count == 0)
        {
            return;
        }

        // Index existing queue items by Id (first occurrence wins) so metadata
        // (e.g. PlaylistItemId) is retained. Playlists may contain duplicate
        // tracks, so ToDictionary would throw — use TryAdd instead.
        var existing = new Dictionary<Guid, QueueItem>();
        foreach (QueueItem q in session.NowPlayingQueue)
        {
            existing.TryAdd(q.Id, q);
        }

        var deviceIds = new HashSet<Guid>();
        var rebuilt = new List<QueueItem>(queue.ItemIds.Count);
        foreach (string id in queue.ItemIds)
        {
            if (Guid.TryParse(id, out Guid guid))
            {
                deviceIds.Add(guid);
                rebuilt.Add(existing.TryGetValue(guid, out QueueItem? qi)
                    ? qi
                    : new QueueItem { Id = guid });
            }
        }

        // Preserve any session items not represented in the device queue (e.g.
        // progressive-continuation tracks) so the playable queue never shrinks.
        foreach (QueueItem qi in session.NowPlayingQueue)
        {
            if (!deviceIds.Contains(qi.Id))
            {
                rebuilt.Add(qi);
            }
        }

        session.NowPlayingQueue = rebuilt;
    }

    /// <summary>
    /// Reports playback progress to Jellyfin so the session PlayState (and the
    /// dashboard UI) stays in sync with the plugin's view. Shared by the shuffle
    /// handlers, which differ only in the <paramref name="order"/> they report.
    /// </summary>
    /// <param name="session">The Jellyfin session to report on.</param>
    /// <param name="itemId">The currently-playing item ID.</param>
    /// <param name="offsetMs">The current playback offset in milliseconds.</param>
    /// <param name="order">The playback order to report (Shuffle or Default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async progress report.</returns>
    protected async Task ReportPlaybackProgress(SessionInfo session, Guid itemId, long offsetMs, PlaybackOrder order, CancellationToken cancellationToken)
    {
        long positionTicks = TimeSpan.FromMilliseconds(offsetMs).Ticks;
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            RepeatMode = session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            PositionTicks = positionTicks,
            PlaybackOrder = order,
        };

        await SessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared body of the loop-mode intents (LoopOn/LoopOff/LoopSongOn, JF-450):
    /// attaches the given repeat mode to the currently playing item via
    /// <see cref="SessionManager"/> progress reporting. The intent can arrive from
    /// an open session with nothing playing: there is no item to attach the mode
    /// to, so the localized no-media tell is returned instead of throwing.
    /// </summary>
    /// <param name="request">The skill request (locale source for the no-media tell).</param>
    /// <param name="context">The context of the skill intent request (AudioPlayer token).</param>
    /// <param name="session">The session instance to report progress on.</param>
    /// <param name="mode">The repeat mode to apply.</param>
    /// <param name="label">Log label identifying the calling intent.</param>
    /// <returns>An empty response, or the no-media tell when nothing is playing.</returns>
    protected async Task<SkillResponse> ApplyRepeatModeAsync(Request request, Context context, SessionInfo session, RepeatMode mode, string label)
    {
        PlaybackState? requestState = context.AudioPlayer;

        Logger.LogDebug("{Label}: entered, token={Token}, offset={OffsetMs}ms", label, requestState?.Token, requestState?.OffsetInMilliseconds);

        // The intent can arrive from an open session with nothing playing: there is
        // no item to attach the repeat mode to.
        if (requestState?.Token == null || !Guid.TryParse(requestState.Token, out Guid itemId))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", GetLocale(request)));
        }

        long positionTicks = TimeSpan.FromMilliseconds(requestState.OffsetInMilliseconds).Ticks;
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            PlaybackOrder = session.PlayState.PlaybackOrder,
            PositionTicks = positionTicks,
            RepeatMode = mode,
        };

        await SessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);

        return ResponseBuilder.Empty();
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
    internal static string EscapeXml(string? text)
    {
        return (text ?? string.Empty)
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
    /// Attach an APL NowPlaying screen directive to a response when the device supports APL.
    /// No-op on non-APL devices, when visuals are disabled, or when the response has no
    /// AudioPlayer directive (e.g. VideoApp path).
    /// </summary>
    private protected void TryAttachNowPlayingDirective(
        SkillResponse response,
        MediaBrowser.Controller.Entities.BaseItem item,
        string itemId,
        Entities.User user,
        Context? context)
    {
        if (!Apl.AplHelper.VisualsEnabled || !Apl.AplHelper.DeviceSupportsApl(context))
        {
            return;
        }

        // Only attach when the response carries an AudioPlayer.Play directive.
        // VideoApp responses render their own UI and don't need APL.
        if (!response.Response.Directives.Any(d => d is AudioPlayerPlayDirective))
        {
            return;
        }

        string imageUrl = GetImageUrl(itemId, user);
        var directive = Apl.AplHelper.BuildNowPlayingDirective(item, imageUrl, imageUrl, context);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);
        }
        else
        {
            Logger.LogDebug("APL BuildNowPlayingDirective returned null for item '{ItemName}'", item.Name);
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

    /// <summary>
    /// Build an AudioPlayer response that plays an artist's songs, sorted by popularity
    /// with optional shuffle and progressive queue continuation.
    /// Shared by PlaySongIntentHandler, PlayAlbumIntentHandler, and others that fall back
    /// to artist playback when the primary media-type search finds nothing.
    /// </summary>
    /// <param name="artistId">The artist's Jellyfin ID.</param>
    /// <param name="artistName">The artist's display name (for messages and logging).</param>
    /// <param name="jellyfinUser">The Jellyfin user for queries.</param>
    /// <param name="user">The Alexa user.</param>
    /// <param name="session">The current session.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="libraryManager">Library manager for querying items.</param>
    /// <param name="userDataManager">User data manager for resume-position lookup.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="logLabel">Label for log messages (e.g. "PlaySong fallback").</param>
    /// <param name="announcement">Optional speech to announce before playback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A skill response with AudioPlayer directive, or a "no songs" tell.</returns>
    protected async Task<SkillResponse> BuildArtistSongsResponseAsync(
        Guid artistId,
        string artistName,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        string logLabel,
        string? announcement = null,
        CancellationToken cancellationToken = default)
    {
        var artistSongsQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            // JF-358: IncludeItemTypes=Audio, not MediaTypes=Audio (see PlayArtistSongsIntentHandler).
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            OrderBy = PopularitySort,
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artistId },
            Limit = ProgressiveQueueConstants.GetInitialFetchSize()
        };
        ApplyLibraryFilter(artistSongsQuery, user, libraryManager);

        IReadOnlyList<BaseItem> artistItems = await RetryAsync(
            () => libraryManager.GetItemList(artistSongsQuery),
            logLabel + ":GetArtistSongs",
            cancellationToken).ConfigureAwait(false);

        Logger.LogDebug("{Label}: fetched {Count} songs for artist='{Artist}'", logLabel, artistItems.Count, artistName);

        if (artistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, artistName));
        }

        var (sortedItems, startIndex, _) = SortAndFindResumeIndex(
            artistItems, jellyfinUser, userDataManager, resumePosition: false);

        if (_config.ShuffleArtistSongs)
        {
            var shuffled = sortedItems.ToList();
            Shuffle(shuffled);
            sortedItems = shuffled;
            startIndex = 0;
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < sortedItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = sortedItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = sortedItems[startIndex];

        // Persist queue to device storage for crash recovery
        queueManager?.SetQueue(
            context.System.Device.DeviceID,
            sortedItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        if (artistItems.Count >= ProgressiveQueueConstants.GetInitialFetchSize())
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Artist",
                    ArtistId = artistId,
                    StartIndex = artistItems.Count,
                    TotalCount = int.MaxValue,
                    UserId = jellyfinUser.Id,
                    SortOrder = PopularitySort,
                    Shuffle = _config.ShuffleArtistSongs
                });
        }

        string itemId = sortedItems[startIndex].Id.ToString();
        Logger.LogDebug(
            "{Label}: returning AudioPlayer, itemId={ItemId}, startIndex={StartIndex}, queueSize={QueueSize}",
            logLabel, itemId, startIndex, queueItems.Count);
        SkillResponse response = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, sortedItems[startIndex], user, context, announceLocale: locale);

        ApplyAnnouncement(response, announcement);

        return response;
    }

    /// <summary>
    /// Overrides a play response's speech with the given announcement (JF-345: the
    /// ONE override site; was a triplicated 3-liner across the artist/album/song play
    /// builders). No-op for a null/whitespace announcement.
    /// </summary>
    /// <param name="response">The play response to speak over.</param>
    /// <param name="announcement">The announcement text, or null to keep the default speech.</param>
    protected static void ApplyAnnouncement(SkillResponse response, string? announcement)
    {
        if (!string.IsNullOrWhiteSpace(announcement))
        {
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = announcement };
        }
    }

    /// <summary>
    /// JF-440: the ONE single-song play shape (was the 4th/5th inline copy across
    /// handlers): one-song session queue, full-item bookkeeping, stale progressive
    /// continuation cleared (a one-song queue replacing an artist's progressive queue
    /// must not let the OLD artist resume after the song), AudioPlayer response with
    /// the optional announcement overriding the speech. Crash-recovery SetQueue is
    /// deliberately NOT persisted: every single-song site historically skips it and a
    /// one-song queue is trivially re-requestable (the divergence is tracked in JF-440's
    /// notes; normalize rather than silently change crash-recovery behavior).
    /// </summary>
    /// <param name="song">The audio item to play.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="session">The Alexa session.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="announcement">Optional spoken announcement replacing the default speech.</param>
    /// <returns>The AudioPlayer play response.</returns>
    protected SkillResponse BuildSingleSongResponse(
        BaseItem song,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        string? announcement = null)
    {
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };
        session.FullNowPlayingItem = song;
        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);

        string itemId = song.Id.ToString();
        SkillResponse response = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, song, user, context, announceLocale: locale);
        ApplyAnnouncement(response, announcement);

        return response;
    }

    /// <summary>
    /// JF-439/JF-440 inverse cross-media fallback (BaseHandler home): no artist
    /// matched, so try the song index with the musician value (the NLU coin-flips
    /// musician-shaped song titles into artist intents). Serves the best song above
    /// <see cref="CrossMediaSongThreshold"/> with a FoundSongInstead announcement;
    /// returns null (caller falls through to its clean not-found) when the index is
    /// absent/warming (an opportunistic fallback must never worsen the not-found
    /// path) or nothing clears the bar. NO word-count guard by design: a spaceless
    /// CJK title tokenizes to one token (JF-439 review).
    /// </summary>
    /// <param name="musician">The raw musician slot value.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="session">The Alexa session.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="songIndex">The song n-gram index (null in minimal setups).</param>
    /// <param name="libraryManager">Library manager for the library-filter walk.</param>
    /// <param name="cancellationToken">Shutdown token.</param>
    /// <returns>The play response, or null to fall through to the caller's not-found.</returns>
    protected SkillResponse? TrySongFallback(
        string musician,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ISongNgramIndex? songIndex,
        ILibraryManager libraryManager,
        string logLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (songIndex == null)
        {
            return null;
        }

        var keywordTokens = KeywordMatcher.Tokenize(musician, locale);
        if (keywordTokens.Length == 0)
        {
            return null;
        }

        // The index's topParentMap holds PHYSICAL library folder ids; ResolveForUser
        // emits the union of both id spaces, so the membership test matches the index
        // maps (JF-439/JF-455) and returns null when the user is unrestricted.
        Guid[]? topParentIds = LibraryFilter.ResolveForUser(user, libraryManager, Logger);

        List<(BaseItem Item, double Score)> scored;
        try
        {
            scored = songIndex.SearchWithPhoneticFallback(keywordTokens, locale, topParentIds, _config.PhoneticSongSearchEnabled);
        }
        catch (Exceptions.SkillWarmingUpException)
        {
            // The warming gate's refusal answers the ORIGINAL request; this
            // opportunistic fallback must not convert a not-found into a warming Tell.
            Logger.LogDebug("{Label}: song fallback skipped, song index warming", logLabel);
            return null;
        }

        if (scored.Count == 0 || scored[0].Score < CrossMediaSongThreshold)
        {
            Logger.LogDebug(
                "{Label}: song fallback rejected for query='{Query}' (best score {Score:F0} over {Count} candidates, bar={Bar})",
                logLabel, musician, scored.Count > 0 ? scored[0].Score : 0, scored.Count, CrossMediaSongThreshold);
            return null;
        }

        BaseItem song = scored[0].Item;
        Logger.LogInformation(
            "Song fallback found '{SongName}' itemId={ItemId} (score={Score:F0}) for query='{Query}'",
            song.Name, song.Id, scored[0].Score, musician);

        return BuildSingleSongResponse(
            song, user, session, context, locale,
            // JF-345: the third cross-media substitution announcement joins the flag
            // (an artist-intent request served a song is the same substitution class).
            announcement: _config.AnnounceCrossMediaSubstitution
                ? ResponseStrings.Get("FoundSongInstead", locale, song.Name)
                : null);
    }

    /// <summary>
    /// Entity fallback for greedy <c>AMAZON.SearchQuery</c> intents that misroute an
    /// artist query (e.g. it-IT "di miles davis" captured as a mood). Strips locale
    /// stop-words via <see cref="KeywordMatcher.Tokenize"/> (all 11 language prefixes of
    /// the 17 locales since JF-389; English stop words are stripped under every locale
    /// since JF-384), reuses
    /// the phonetic artist search pipeline, and respects the cross-media word-count guard
    /// (<see cref="CrossMediaArtistMaxWords"/>) and threshold. Returns null when no
    /// confident match is found so the caller falls through to its own not-found response.
    /// JF-464: returns null immediately when the global music flag
    /// (<see cref="PluginConfiguration.MusicEnabled"/>) is off: the fallback plays artist
    /// songs, and every caller must inherit that gate here rather than re-wire it.
    /// JF-446: the ONE cross-media artist gate. PlaySong and PlayAlbum route their
    /// no-results fallback here instead of inline copies (the copies counted RAW words,
    /// so an article-carrying elicit answer like "di pink floyd" dead-ended at the guard,
    /// and accepted via non-phonetic scoring, so ASR drift in [60,85) like "cup" for
    /// "Koop" never played). Acceptance scores through the SAME phonetic matcher the
    /// musician-slot path uses (FuzzyMatcher's Double Metaphone overload): a
    /// length-banded code collision floors at PhoneticFloorScore = 91 (ContainmentScore
    /// 90 + 1), above the strict bar of max(normal, 85) while the user's threshold
    /// stays at or below 91; a user threshold above 91 drops the floored collision
    /// into the sub-strict band instead, so accent drift plays while non-phonetic
    /// plausible matches stay in the JF-363
    /// Confirm/AutoServe band. The band is opt-in via <paramref name="notFoundMediaType"/>
    /// because its decline path must speak a media-type not-found: FindSong re-prompts
    /// for the title instead (not a terminal song not-found) and PlayMoodMusic declines
    /// to NotFoundMood, so neither can reuse the band's decline contract.
    /// </summary>
    /// <param name="notFoundMediaType">When non-null (<see cref="DisambiguationHelper.MediaTypeSong"/>
    /// or <see cref="DisambiguationHelper.MediaTypeAlbum"/>), enables the JF-363
    /// sub-strict band: a single best artist scoring in [normalThreshold, strict) is
    /// offered for confirmation (or auto-served per config), and the offer's decline
    /// speaks the media-type not-found. Null keeps the pre-band behavior (clean miss).</param>
    protected async Task<SkillResponse?> TryEntityFallbackAsync(
        string slotText,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        IArtistIndex? artistIndex,
        string logLabel,
        CancellationToken cancellationToken,
        string? notFoundMediaType = null)
    {
        // Restored from the deleted PlaySong/PlayAlbum inline copies (JF-446 review):
        // the gate's entry point must name the query it is about to interpret.
        Logger.LogDebug(
            "{Label}: no results found, trying artist fallback with query='{Query}'",
            logLabel, slotText);

        // JF-464: the fallback's whole payoff is playing music (artist songs), and its
        // artist queries skip FilterByContentAccess, so the global music flag must gate
        // it HERE at the shared entry rather than in any caller's wiring. JF-467: the
        // read is the LIVE Plugin.Instance configuration (IsMusicEnabled), so a
        // standard-API configuration replacement takes effect without a restart.
        if (!IsMusicEnabled)
        {
            Logger.LogInformation(
                "{Label}: artist fallback skipped, music is disabled via configuration, query='{Query}'",
                logLabel, slotText);
            return null;
        }

        if (!PassesCrossMediaWordGuard(slotText, locale, fallbackNoun: "artist", logLabel, out var tokens))
        {
            return null;
        }

        string cleaned = string.Join(' ', tokens);

        // JF-448 (review F2): pin the artist index once for this fallback so the search
        // chain below and the phonetic confirm after it read the SAME publish (a
        // mid-fallback refresh could otherwise serve the artist list of one snapshot
        // against another's phonetic codes). SearchAsync's own capture is idempotent on
        // this view, so no extra hop is added; Pin degrades to live reads when the
        // implementation cannot pin.
        IArtistIndex? pinnedArtistIndex = artistIndex.Pin();

        IReadOnlyList<BaseItem> artists = await ArtistSearch.SearchAsync(
            cleaned, user, libraryManager, pinnedArtistIndex, Logger,
            (q, ct) => RetryAsync(() => libraryManager.GetItemList(q), logLabel + ":GetArtistsFallback", ct),
            locale, cancellationToken).ConfigureAwait(false);

        if (artists.Count == 0)
        {
            return null;
        }

        // JF-446 finding 2: accept through the PHONETIC matcher when the artist index is
        // available (the same lookup FuzzyMatchPhonetic and ArtistSearch's own tiers use).
        // Threshold rationale: the strict bar stays Math.Max(normal, CrossMediaArtistThreshold)
        // because this is still a cross-media GUESS (the user asked for another media
        // type), so only near-exact or phonetically-colliding matches auto-play. The
        // phonetic overload is what makes the shared thresholds meaningful: ASR drift
        // ("cup" for "Koop", both code KP) floors at PhoneticFloorScore and plays, while
        // the plain overload scored it below every bar and dead-ended (the defect the
        // inline copies carried).
        var best = pinnedArtistIndex != null
            ? FuzzyMatcher.FindBestMatchWithScore(
                cleaned,
                artists,
                a => a.Name,
                a => a.Id,
                id => pinnedArtistIndex.TryGetPhoneticCode(id, out var codes) ? codes : null)
            : FuzzyMatcher.FindBestMatchWithScore(cleaned, artists, a => a.Name);
        int normalThreshold = FuzzyMatcher.GetDefaultThreshold(user);
        int threshold = Math.Max(normalThreshold, CrossMediaArtistThreshold);
        BaseItem? bestItem = best.HasValue ? best.Value.Item : null;
        int bestScore = best.HasValue ? best.Value.Score : 0;

        if (bestItem != null && bestScore >= threshold)
        {
            // Strict (or phonetic-floor) match: fall through to playback below.
            // Restored from the deleted PlaySong/PlayAlbum inline copies (JF-446
            // review): the acceptance must name the artist and its score.
            Logger.LogInformation(
                "{Label}: artist fallback found '{ArtistName}' with score={Score} for query='{Query}' (threshold={Threshold})",
                logLabel, bestItem.Name, bestScore, cleaned, threshold);
        }
        else if (bestItem != null && bestScore >= normalThreshold && notFoundMediaType != null)
        {
            // JF-363 sub-strict band [normalThreshold, threshold): offer or auto-serve
            // the single best artist instead of a dead-end miss. Confirm/AutoServe are
            // safe (no silent wrong substitution: Confirm asks first; AutoServe is
            // opt-in). The offer carries the RAW slot text so the decline speaks the
            // user's own words. Single candidate only (same design as the copies this
            // replaced: disambiguating among cross-media guesses is wrong UX).
            // PRECEDENCE (review finding, pinned by CrossMediaTypeFallbackTests
            // .JF363_BandWinsOverWordCoverageValve_Confirm): the band is checked
            // BEFORE the word-coverage valve so it wins for the callers that enabled
            // it (PlaySong/PlayAlbum). The valve auto-plays silently; letting it win
            // in the overlap would break the JF-363 contract of no silent
            // substitution in [normalThreshold, threshold).
            var suggestionMode = GetCrossMediaArtistSuggestion(user);
            if (suggestionMode == CrossMediaArtistSuggestion.Confirm)
            {
                return BuildCrossMediaArtistOfferAsk(slotText, bestItem, locale, notFoundMediaType);
            }

            if (suggestionMode == CrossMediaArtistSuggestion.Off)
            {
                Logger.LogDebug(
                    "{Label}: entity fallback artist '{Artist}' score={Score} in suggestion band but suggestion is Off, query='{Query}'",
                    logLabel, bestItem.Name, bestScore, cleaned);
                return null;
            }

            Logger.LogInformation(
                "{Label}: entity fallback artist suggestion AutoServe '{Artist}' score={Score} for query='{Query}'",
                logLabel, bestItem.Name, bestScore, cleaned);
        }
        else if (bestItem != null && bestScore >= normalThreshold
            && Util.ArtistSearch.WordCoverageCandidates(cleaned, new[] { bestItem }, locale).Count > 0)
        {
            // JF-440 (F4): a word-coverage tier match scores LOW on the fuzzy scale
            // ('The Beatles' vs 'beatles live' = 27, below every cross-media gate),
            // so the fuzzy bar alone rejects exactly the qualifier-query class the
            // tier exists to serve. Accept the match when the artist is a
            // word-subset of the cleaned query (same predicate as the search tier).
            // Review round: the word-coverage acceptance also needs the NORMAL fuzzy
            // floor. Without it, a one-word subset of a 2-word mood slot ('soft rock'
            // -> artist 'Soft') auto-substitutes at any score; with it, the gate is a
            // safety valve for genuinely-artist-shaped queries that just miss the
            // strict cross-media bar, never a bypass of every bar (the JF-437 search
            // tier, with its full selection + downstream gates, owns the main path).
            // Scoped to callers WITHOUT the JF-363 band (the band branch above wins
            // the overlap first): FindSong and PlayMoodMusic keep the valve behavior
            // the shared gate always had.
            Logger.LogInformation(
                "{Label}: entity fallback artist '{Artist}' below fuzzy threshold ({Score}<{Threshold}) but accepted as a word-coverage match for query='{Query}'",
                logLabel, bestItem.Name, bestScore, threshold, cleaned);
        }
        else
        {
            Logger.LogDebug(
                "{Label}: entity fallback artist score={Score} below threshold={Threshold}, query='{Query}'",
                logLabel, bestScore, threshold, cleaned);
            return null;
        }

        return await BuildArtistSongsResponseAsync(
            bestItem!.Id,
            bestItem.Name,
            jellyfinUser,
            user,
            session,
            context,
            locale,
            libraryManager,
            userDataManager,
            queueManager,
            logLabel,
            // JF-345: the cross-media substitution announcement (which artist is
            // playing instead of what was asked) is opt-out; the substitution itself
            // always plays. Same flag as the song-to-album cascade's announcement.
            announcement: _config.AnnounceCrossMediaSubstitution
                ? ResponseStrings.Get("FoundArtistInstead", locale, bestItem.Name)
                : null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Minimum album-query length to attempt the bounded fuzzy album tier. Shorter
    /// queries (e.g. "red", "aria") produce too many substring false positives. Shared
    /// by PlayAlbum's own fuzzy fallback and the JF-345 song-to-album cascade.
    /// </summary>
    protected const int MinFuzzyAlbumQueryLength = 4;

    /// <summary>
    /// The cheap DTO shape for queries that read only names/ids (JF-443/JF-446): no
    /// images, no userdata, no current program. Fresh instance per call (DtoOptions is
    /// mutable; a shared instance could be mutated through one query and leak into
    /// another). Shared by PlayAlbum's fuzzy fallback and the JF-345 song-to-album
    /// cascade.
    /// </summary>
    /// <returns>A minimal DtoOptions.</returns>
    protected static DtoOptions CheapDtoOptions() => new DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false };

    /// <summary>
    /// Builds a MusicAlbum query scoped to the user's libraries (with library
    /// filtering). Pass a search term for the exact indexed lookup, or null for the
    /// broad fuzzy-fallback scan. THE ONE album query shape (JF-345): PlayAlbum's own
    /// search and the song-to-album cascade build the same query so the cascade can
    /// never widen into a differently-shaped scan.
    /// </summary>
    /// <param name="libraryManager">Library manager for the library-scope filter.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="user">The plugin user whose library filter applies.</param>
    /// <param name="searchTerm">The exact-lookup search term, or null for the fuzzy scan.</param>
    /// <param name="artistIds">Optional artist scoping (AlbumArtistIds when <paramref name="albumArtistsOnly"/> is set).</param>
    /// <param name="albumArtistsOnly">True to match albums BY the artist (AlbumArtistIds) instead of also compilations containing them (ArtistIds).</param>
    /// <returns>The album query.</returns>
    protected InternalItemsQuery BuildAlbumQuery(
        ILibraryManager libraryManager,
        JellyfinUser? jellyfinUser,
        Entities.User user,
        string? searchTerm,
        Guid[]? artistIds,
        bool albumArtistsOnly = false)
    {
        var q = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            DtoOptions = new DtoOptions(true)
        };
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            q.SearchTerm = searchTerm;
        }

        if (artistIds is { Length: > 0 })
        {
            if (albumArtistsOnly)
            {
                // AlbumArtistIds matches albums BY the artist; ArtistIds would also match
                // compilations merely CONTAINING a track by them (live finding: "un disco
                // dei Koop" resolved to a compilation featuring Koop, not Koop's album).
                q.AlbumArtistIds = artistIds;
            }
            else
            {
                q.ArtistIds = artistIds;
            }
        }

        ApplyLibraryFilter(q, user, libraryManager);
        return q;
    }

    /// <summary>
    /// JF-345: song-to-album cascade. In the 16 free-text locales PlayAlbum's album slot
    /// is an <c>AMAZON.MusicRecording</c>-style free-text type (only it-IT has the
    /// catalog-backed AlbumName type), so a bare "play abbey road" routes away from
    /// PlayAlbum, misses, and dead-ends in a song not-found (deterministic since the
    /// bare album carriers were trimmed: PR #15 for the five English locales, JF-459
    /// for the other 11; before those trims the 11 were a routing coin flip. Caveat:
    /// "away from PlayAlbum" lands on PlaySong in 13 of the 16; in the es locales the
    /// bare Reproduce/Pon forms are captured by PlayByGenreIntent's bare genre carriers
    /// instead, so this cascade never fires for those shapes; JF-463). This gate recovers
    /// that recall: on a confirmed song miss (the
    /// caller only reaches it after its own song search AND the artist cascade both
    /// missed) it runs a bounded album search and plays a strong match with a
    /// FoundAlbumInstead announcement.
    ///
    /// PRECEDENCE (deliberate): song first (caller contract), then the ARTIST cascade,
    /// then this album tier. The album tier only sees queries where no artist matched at
    /// all. Album-before-artist was considered and rejected: self-titled albums are
    /// ubiquitous ("play metallica" would flip from today's correct artist playback to
    /// the single album named Metallica), and the artist gate is itself strict
    /// (>= max(normal, 85), phonetic floor 91), so when it fires it is not a weaker
    /// signal than an exact album name. Consequence: when a sub-strict artist match
    /// lands in the JF-363 Confirm band the artist offer wins and the album is never
    /// consulted; the offer is announced and declinable, so that overlap is acceptable.
    ///
    /// Gating (stricter than the artist cascade, per the task contract): the shared
    /// 2-content-word tokenized guard (<see cref="CrossMediaArtistMaxWords"/>), then
    /// <c>Math.Max(normal, CrossMediaAlbumThreshold=90)</c> (containment-grade; song and
    /// album names overlap far more than artists and moods), then the JF-408
    /// interior-containment rejection. Non-phonetic and unpinned by design: no album
    /// phonetic index exists and PlayAlbum's own fuzzy fallback is likewise non-phonetic
    /// (the album-path precedent). Bounded queries only (the f5c701c lesson): the
    /// indexed SearchTerm tier, then at most ONE cheap-DTO album-catalog scan (the same
    /// bounded shape PlayAlbum's own fuzzy fallback ships), never an Audio-catalog scan.
    /// </summary>
    /// <param name="slotText">The raw slot text that missed (e.g. the song slot value).</param>
    /// <param name="jellyfinUser">The Jellyfin user for queries.</param>
    /// <param name="user">The plugin user (threshold override, library filter).</param>
    /// <param name="session">The Jellyfin session receiving the queue.</param>
    /// <param name="context">The Alexa context (device id for crash-recovery persistence).</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="libraryManager">Library manager for queries.</param>
    /// <param name="userDataManager">User data manager for the resume-track lookup.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="logLabel">Label for log messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The album play response with the announcement, or null when no album clears the bar (caller falls through to its own not-found).</returns>
    protected async Task<SkillResponse?> TryAlbumFallbackAsync(
        string slotText,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        string logLabel,
        CancellationToken cancellationToken)
    {
        // JF-464: same music-disabled gate as the artist fallback. This cascade's
        // whole payoff is playing an album of music, and its queries skip
        // FilterByContentAccess, so the global flag must gate it here (null is the
        // shared no-match contract; the caller falls through to its own not-found).
        // JF-467: reads the LIVE Plugin.Instance configuration (IsMusicEnabled).
        if (!IsMusicEnabled)
        {
            Logger.LogInformation(
                "{Label}: album fallback skipped, music is disabled via configuration, query='{Query}'",
                logLabel, slotText);
            return null;
        }

        if (!PassesCrossMediaWordGuard(slotText, locale, fallbackNoun: "album", logLabel, out _))
        {
            return null;
        }

        // The query is the RAW trimmed slot text, deliberately NOT the stop-word-
        // stripped token join the artist gate uses: album titles are rarely article-
        // prefixed, and both the SearchTerm index and full-name fuzzy favor raw text.
        string query = slotText.Trim();

        // Tier 1 (indexed): exact SearchTerm over MusicAlbum, the same query PlayAlbum's
        // primary search runs.
        IReadOnlyList<BaseItem> candidates = await RetryAsync(
            () => libraryManager.GetItemList(BuildAlbumQuery(libraryManager, jellyfinUser, user, query, artistIds: null)),
            logLabel + ":GetAlbumsFallbackExact",
            cancellationToken).ConfigureAwait(false);

        // Tier 2 (bounded fuzzy): only on an exact miss, one cheap-DTO scan of the album
        // catalog (hundreds of rows, not the Audio catalog's thousands; JF-446 shape).
        if (candidates.Count == 0 && query.Length >= MinFuzzyAlbumQueryLength)
        {
            var fuzzyQuery = BuildAlbumQuery(libraryManager, jellyfinUser, user, searchTerm: null, artistIds: null);
            fuzzyQuery.DtoOptions = CheapDtoOptions();
            candidates = await RetryAsync(
                () => libraryManager.GetItemList(fuzzyQuery),
                logLabel + ":GetAlbumsFallbackFuzzy",
                cancellationToken).ConfigureAwait(false);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Single best over the tier's candidates (same decision shape as the artist
        // gate: a cross-media guess must never disambiguate among multiple guesses;
        // same-name candidates are picked arbitrarily, the JF-341 class PlayAlbum's
        // own fuzzy path documents).
        var best = FuzzyMatcher.FindBestMatchWithScore(query, candidates, a => a.Name);
        if (best is not { } match)
        {
            return null;
        }

        int threshold = Math.Max(FuzzyMatcher.GetDefaultThreshold(user), CrossMediaAlbumThreshold);
        if (match.Score < threshold)
        {
            Logger.LogDebug(
                "{Label}: album fallback score={Score} below threshold={Threshold} for query='{Query}', not substituting",
                logLabel, match.Score, threshold, query);
            return null;
        }

        if (Util.ArtistSearch.IsInteriorContainment(query, match.Item.Name))
        {
            // JF-408: the match exists only inside other words of the query (live
            // precedent: album "O" via the 'o' in "walls for cup"). The recall layer
            // returned the candidate; the substitution decision must not act on it.
            Logger.LogInformation(
                "{Label}: album fallback match '{Name}' score={Score} for query='{Query}' is interior containment, not substituting (JF-408)",
                logLabel, match.Item.Name, match.Score, query);
            return null;
        }

        Logger.LogInformation(
            "{Label}: album fallback found '{AlbumName}' score={Score} for query='{Query}' (threshold={Threshold})",
            logLabel, match.Item.Name, match.Score, query, threshold);

        return await BuildAlbumPlayResponseAsync(
            match.Item,
            jellyfinUser,
            user,
            session,
            context,
            locale,
            libraryManager,
            userDataManager,
            queueManager,
            logLabel,
            // JF-345: the substitution announcement is opt-out; the album still plays
            // when the flag is off, it just starts silently.
            announcement: _config.AnnounceCrossMediaSubstitution
                ? ResponseStrings.Get("FoundAlbumInstead", locale, match.Item.Name)
                : null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// JF-345: the ONE album play flow (was inline in PlayAlbumIntentHandler; extracted
    /// so the song-to-album cascade plays albums with the SAME queue semantics as a
    /// direct album request). Fetches the first track page by ParentId with the JF-338
    /// AlbumIds retry for malformed/split albums, applies the resume-track index,
    /// persists the session queue and the crash-recovery queue, stores the progressive
    /// continuation for the remaining tracks, and lets the optional announcement
    /// override the speech.
    /// </summary>
    /// <param name="album">The album to play.</param>
    /// <param name="jellyfinUser">The Jellyfin user for queries and resume data.</param>
    /// <param name="user">The plugin user (stream URL token).</param>
    /// <param name="session">The Jellyfin session receiving the queue.</param>
    /// <param name="context">The Alexa context (device id for crash-recovery persistence).</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="libraryManager">Library manager for track queries.</param>
    /// <param name="userDataManager">User data manager for the resume-track lookup.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="logLabel">Label for log messages.</param>
    /// <param name="announcement">Optional spoken announcement replacing the default speech.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An AudioPlayer response for the album's first (resume-aware) track, or a localized tell when the album has no playable tracks.</returns>
    protected async Task<SkillResponse> BuildAlbumPlayResponseAsync(
        BaseItem album,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        string logLabel,
        string? announcement = null,
        CancellationToken cancellationToken = default)
    {
        // Get the first page of album tracks for fast time-to-audio.
        // Remaining tracks will be fetched on demand by PlaybackNearlyFinished.
        Logger.LogDebug("{Label}: querying tracks for album='{AlbumName}' (id={AlbumId})", logLabel, album.Name, album.Id);
        QueryResult<BaseItem> albumResult = await RetryAsync(
            () => SafeGetItemsResult(libraryManager, new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                ParentId = album.Id,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                DtoOptions = new DtoOptions(true),
                OrderBy = QueueContinuationFetcher.AlbumTrackOrder,
                Limit = ProgressiveQueueConstants.GetInitialFetchSize()
            }),
            logLabel + ":GetAlbumTracks",
            cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("{Label}: Jellyfin returned {TrackCount} tracks (total={TotalCount})", logLabel, albumResult.Items.Count, albumResult.TotalRecordCount);
        if (albumResult.TotalRecordCount == 0)
        {
            // Tolerant fallback: for split / multi-disc / malformed-folder albums, the
            // folder-based ParentId query can return 0 even when the tracks exist (the
            // track's Album metadata still links them). Query by album membership, which
            // ignores folder structure. Verified on the malformed "Jazz Cafe" album:
            // ParentId+Recursive returns 0, AlbumIds returns all tracks. JF-338.
            Logger.LogDebug("{Label}: folder-based track query returned 0, retrying by AlbumIds for '{Name}'", logLabel, album.Name);
            albumResult = await RetryAsync(
                () => SafeGetItemsResult(libraryManager, new InternalItemsQuery()
                {
                    User = jellyfinUser,
                    Recursive = true,
                    AlbumIds = new[] { album.Id },
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    DtoOptions = new DtoOptions(true),
                    OrderBy = QueueContinuationFetcher.AlbumTrackOrder,
                    Limit = ProgressiveQueueConstants.GetInitialFetchSize()
                }),
                logLabel + ":GetAlbumTracksByAlbumIds",
                cancellationToken).ConfigureAwait(false);
            Logger.LogDebug("{Label}: AlbumIds fallback returned {TrackCount} tracks (total={TotalCount})", logLabel, albumResult.Items.Count, albumResult.TotalRecordCount);
        }

        if (albumResult.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album.Name));
        }

        IReadOnlyList<BaseItem> albumItems = albumResult.Items;

        // Check for existing queue position from server-side progress
        (int startIndex, _) = FindResumeTrackIndex(
            albumItems, jellyfinUser, userDataManager, resumePosition: false);

        if (startIndex > 0)
        {
            Logger.LogInformation(
                "{Label}: resuming queue from track {Index} ({Name})",
                logLabel, startIndex, albumItems[startIndex].Name);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < albumItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = albumItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = albumItems[startIndex];

        // Persist queue to device storage for crash recovery
        queueManager?.SetQueue(
            context.System.Device.DeviceID,
            albumItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        // Store continuation info so PlaybackNearlyFinished can fetch the rest.
        // StartIndex uses the original page size because the database offset is
        // independent of the resume slice.
        if (albumResult.TotalRecordCount > albumResult.Items.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Album",
                    ParentId = album.Id,
                    StartIndex = albumResult.Items.Count,
                    TotalCount = albumResult.TotalRecordCount,
                    UserId = jellyfinUser.Id
                });
        }

        string item_id = albumItems[startIndex].Id.ToString();

        Logger.LogDebug(
            "{Label}: returning AudioPlayer, itemId={ItemId}, album='{AlbumName}', startIndex={StartIndex}, queueSize={QueueSize}",
            logLabel, item_id, album.Name, startIndex, queueItems.Count);
        SkillResponse albumResponse = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, albumItems[startIndex], user, context, announceLocale: locale);

        // The caller may pass an announcement (fuzzy name correction in PlayAlbum,
        // cross-media substitution in the JF-345 cascade) so the user knows what is
        // playing instead of what was asked (JF-339).
        ApplyAnnouncement(albumResponse, announcement);

        return albumResponse;
    }

    /// <summary>
    /// Shared playlist-play flow used by <c>PlayPlaylistIntentHandler</c>
    /// (shuffle=false) and the shuffle-play handler (shuffle=true). Resolves the playlist,
    /// builds the initial queue, optionally shuffles it via
    /// <see cref="Playback.DeviceQueueManager.SetShuffledQueue"/>, persists the queue for
    /// crash recovery, stores progressive-continuation state, and returns an
    /// <c>AudioPlayer.Play</c> response for the first track.
    /// </summary>
    /// <param name="libraryManager">Library manager for querying playlists and items.</param>
    /// <param name="userManager">User manager for resolving the Jellyfin user.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery and shuffle.</param>
    /// <param name="playlistName">The playlist name to search for.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="session">The Jellyfin session.</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="shuffle">When true and <paramref name="queueManager"/> is non-null, shuffles the queue via <see cref="Playback.DeviceQueueManager.SetShuffledQueue"/>.</param>
    /// <param name="rng">Optional injectable random source for deterministic shuffle (tests); null uses <see cref="Random.Shared"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A skill response with an AudioPlayer directive, or a localized error tell.</returns>
    protected async Task<SkillResponse> BuildPlaylistPlayResponseAsync(
        ILibraryManager libraryManager,
        IUserManager userManager,
        Playback.DeviceQueueManager? queueManager,
        string playlistName,
        Context context,
        Entities.User user,
        SessionInfo session,
        string locale,
        bool shuffle,
        Random? rng,
        CancellationToken cancellationToken)
    {
        // Shared by PlayPlaylist (shuffle=false) and ShufflePlay (shuffle=true); the
        // shuffle flag distinguishes the calling path (follow-on logs keep the
        // "PlayPlaylist:" prefix as the shared method body is identical).
        Logger.LogDebug("BuildPlaylistPlayResponseAsync: entered, locale={Locale}, shuffle={Shuffle}", locale, shuffle);

        if (string.IsNullOrWhiteSpace(playlistName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchPlaylistName", locale));
        }

        Logger.LogDebug("Play playlist: {0}", playlistName);

        var (jellyfinUser, userError) = ResolveJellyfinUser(userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        InternalItemsQuery query = new InternalItemsQuery()
        {
            User = jellyfinUser,
            SearchTerm = playlistName,
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            DtoOptions = new DtoOptions(true),
        };

        // Playlists are user-scoped, not library-scoped: native Jellyfin playlists
        // live outside any media library (the PlaylistsFolder), so the kind-aware
        // ApplyLibraryFilter skips the TopParentIds filter for the all-Playlist kind
        // set (JF-455; single decision point in LibraryFilter since JF-456). Trade-off:
        // .m3u playlists stored inside an excluded library surface too. Visibility is
        // NOT guaranteed by query.User on this path: GetItemsResult goes straight to
        // the repository without the IsVisible post-filter GetItemList applies, so
        // other users' private playlists can come back and must be filtered here
        // (code-review P1, JF-455). The track resolver separately filters tracks per user.
        ApplyLibraryFilter(query, user, libraryManager, Logger);
        Logger.LogDebug("PlayPlaylist: querying Jellyfin with searchTerm='{PlaylistName}', types=Playlist", playlistName);
        QueryResult<BaseItem> playlists = await RetryAsync(() => SafeGetItemsResult(libraryManager, query), "GetPlaylists", cancellationToken).ConfigureAwait(false);
        var visiblePlaylists = playlists.Items.Where(p => p.IsVisible(jellyfinUser)).ToList();
        if (visiblePlaylists.Count != playlists.Items.Count)
        {
            Logger.LogDebug("PlayPlaylist: filtered {HiddenCount} playlist(s) not visible to the user", playlists.Items.Count - visiblePlaylists.Count);
        }

        // One meaning for TotalRecordCount: the count of visible playlists in Items.
        // Rebuilding unconditionally keeps the filtered and unfiltered shapes identical
        // (JF-456; the conditional rebuild left TotalRecordCount meaning the raw count
        // whenever nothing was hidden).
        playlists = new QueryResult<BaseItem> { Items = visiblePlaylists, TotalRecordCount = visiblePlaylists.Count };

        Logger.LogDebug("PlayPlaylist: Jellyfin returned {ResultCount} playlists", playlists.TotalRecordCount);

        if (playlists.TotalRecordCount == 0)
        {
            var fuzzy = await SearchItemsFuzzyAsync(playlistName, jellyfinUser, user, libraryManager, new[] { BaseItemKind.Playlist }, cancellationToken, "PlayPlaylistFuzzyFallback").ConfigureAwait(false);
            if (fuzzy != null)
            {
                playlists = new QueryResult<BaseItem> { Items = new List<BaseItem> { fuzzy.Value.Item }, TotalRecordCount = 1 };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
            }
        }

        BaseItem? playlistMatch = null;
        if (playlists.TotalRecordCount > 1)
        {
            Logger.LogDebug("PlayPlaylist: {Count} playlists matched, running disambiguation", playlists.TotalRecordCount);
            BaseItem? topMatch = FuzzyMatch(playlistName, playlists.Items, p => p.Name, user);
            if (topMatch != null)
            {
                playlistMatch = topMatch;
            }
            else
            {
                var (missOutcome, missResponse) = HandleFuzzyMiss(
                    playlistName,
                    playlists.Items,
                    p => p.Name,
                    best => new List<(Guid, string)> { (best.Id, best.Name) },
                    DisambiguationHelper.MediaTypePlaylist,
                    locale,
                    best =>
                    {
                        playlistMatch = best;
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
                    var matches = playlists.Items.Take(3).Select(p => (p.Id, p.Name, (string?)GetImageUrl(p.Id.ToString("N"), user))).ToList();
                    return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypePlaylist, locale, context);
                }
            }
        }
        else
        {
            playlistMatch = playlists.Items[0];
        }

        BaseItem playlist = playlistMatch!;
        Logger.LogDebug("PlayPlaylist: matched playlist='{PlaylistName}' (id={PlaylistId})", playlist.Name, playlist.Id);

        // Playlist members are linked children in the Playlists join table, NOT ParentId-owned
        // rows — querying ILibraryManager with ParentId=playlist.Id always returns 0 (issue #10).
        // Use Playlist.GetManageableItems(), the same API the Jellyfin web UI uses.
        Logger.LogDebug("PlayPlaylist: resolving tracks for playlist='{PlaylistName}'", playlist.Name);
        IReadOnlyList<BaseItem> allTracks = PlaylistTrackResolver.GetAudioTracks(playlist as Playlist, jellyfinUser);
        Logger.LogDebug("PlayPlaylist: resolved {TrackCount} audio tracks for playlist='{PlaylistName}'", allTracks.Count, playlist.Name);

        if (allTracks.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistEmpty", locale));
        }

        int totalCount = allTracks.Count;
        List<BaseItem> playlistItems = allTracks.Take(ProgressiveQueueConstants.GetInitialFetchSize()).ToList();

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < playlistItems.Count; i++)
        {
            BaseItem item = playlistItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
                PlaylistItemId = playlist.Id.ToString(),
            });
        }

        session.NowPlayingQueue = queueItems;  // ordered, so MirrorQueueToSession can read track metadata

        string deviceId = context.System.Device.DeviceID;
        List<string> idList = playlistItems.Select(i => i.Id.ToString()).ToList();
        BaseItem? firstItem;

        if (shuffle && queueManager != null)
        {
            queueManager.SetShuffledQueue(deviceId, idList, rng);
            // Mirror the shuffled DeviceQueue order back into the session queue (metadata preserved).
            Playback.DeviceQueue deviceQueue = queueManager.GetOrCreateQueue(deviceId);
            MirrorQueueToSession(deviceQueue, session);
            firstItem = libraryManager.GetItemById(Guid.Parse(deviceQueue.ItemIds[0]));
        }
        else
        {
            firstItem = libraryManager.GetItemById(queueItems[0].Id);
            if (firstItem != null)
            {
                queueManager?.SetQueue(deviceId, idList, 0);
            }
        }

        if (firstItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        session.FullNowPlayingItem = firstItem;

        // Store continuation info so PlaybackNearlyFinished can fetch the rest
        if (totalCount > playlistItems.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Playlist",
                    ParentId = playlist.Id,
                    PlaylistId = playlist.Id,
                    StartIndex = playlistItems.Count,
                    TotalCount = totalCount,
                    UserId = jellyfinUser!.Id,
                    // Cache the resolved tracks so continuation batches slice this list
                    // instead of re-resolving every linked child on each PlaybackNearlyFinished.
                    CachedTracks = allTracks
                });
        }

        string item_id = firstItem.Id.ToString();

        Logger.LogDebug(
            "PlayPlaylist: returning AudioPlayer, itemId={ItemId}, playlist='{PlaylistName}', queueSize={QueueSize}",
            item_id, playlist.Name, queueItems.Count);
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, firstItem, user, context);
    }
}
