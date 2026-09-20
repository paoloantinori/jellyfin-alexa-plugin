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
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

/// <summary>
/// Handles APL UserEvent requests from touch interactions on Echo Show devices.
/// Routes control actions (prev/pause/next), list item selections (selectItem/playTrack),
/// and carousel taps (carouselTap).
/// </summary>
public class AplUserEventHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager _queueManager;

    public AplUserEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        DeviceQueueManager queueManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is AplUserEventRequest;
    }

    /// <summary>
    /// Handle APL touch events with session attributes for pagination support.
    /// Routes "show more" taps to ListPaginationHelper.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        var aplEvent = (AplUserEventRequest)request;
        string? action = aplEvent.Arguments?.FirstOrDefault()?.ToString();

        Logger.LogDebug(
            "AplUserEvent: action={Action}, arguments={Args}",
            action,
            aplEvent.Arguments != null ? string.Join(", ", aplEvent.Arguments) : "(null)");

        if (action == "show more")
        {
            string locale = GetLocale(request);
            var paginationState = ListPaginationHelper.ReadState(sessionAttributes);
            if (paginationState == null)
            {
                return Task.FromResult(ResponseBuilder.Empty());
            }

            return Task.FromResult(ListPaginationHelper.BuildNextPageResponse(_libraryManager, paginationState, locale));
        }

        return HandleAsync(request, context, user, session, cancellationToken);
    }

    /// <summary>
    /// Handle APL touch events: playback controls (prev/pause/next),
    /// list item selection (selectItem/playTrack), and carousel taps (carouselTap).
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        var aplEvent = (AplUserEventRequest)request;
        string? action = aplEvent.Arguments?.FirstOrDefault()?.ToString();

        switch (action)
        {
            case "prev":
                // JF-582: both taps route through the shared adjacent-item serve
                // (rehydration guard, medium refusal and codec gate applied
                // uniformly; rationale on the serve).
                return Task.FromResult(Progress.ServeAdjacentQueueItem(
                    _queueManager,
                    _libraryManager,
                    session,
                    context,
                    user,
                    GetLocale(request),
                    ProgressReporter.AdjacentQueueDirection.Previous,
                    "AplUserEvent previous",
                    tapOrigin: true));
            case "pause":
                return Task.FromResult(BuildPauseResponse());
            case "next":
                return Task.FromResult(Progress.ServeAdjacentQueueItem(
                    _queueManager,
                    _libraryManager,
                    session,
                    context,
                    user,
                    GetLocale(request),
                    ProgressReporter.AdjacentQueueDirection.Next,
                    "AplUserEvent next",
                    tapOrigin: true));
            case "selectItem":
            case "playTrack":
            case "carouselTap":
                return HandleSelectItem(aplEvent, user, session, context, request);
            default:
                return Task.FromResult(ResponseBuilder.Empty());
        }
    }

    private Task<SkillResponse> HandleSelectItem(AplUserEventRequest aplEvent, Entities.User user, SessionInfo session, Context context, Request request)
    {
        string? itemIdStr = aplEvent.Arguments?.ElementAtOrDefault(1)?.ToString();
        if (string.IsNullOrEmpty(itemIdStr) || !Guid.TryParse(itemIdStr, out Guid itemId))
        {
            Logger.LogDebug("AplUserEvent HandleSelectItem: no valid item ID in arguments");
            return Task.FromResult(ResponseBuilder.Empty());
        }

        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            Logger.LogDebug("AplUserEvent HandleSelectItem: item {ItemId} not found in library", itemIdStr);
            return Task.FromResult(ResponseBuilder.Empty());
        }

        Logger.LogDebug(
            "AplUserEvent HandleSelectItem: resolved item={ItemName} ({ItemId}), type={ItemType}",
            item.Name, itemIdStr, item.GetType().Name);

        if (item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            session.NowPlayingQueue = new List<QueueItem> { new() { Id = item.Id } };
            session.FullNowPlayingItem = item;

            string locale = GetLocale(request);
            var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
            if (userError != null)
            {
                return Task.FromResult(userError);
            }

            // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
            return Task.FromResult(Launch.BuildVideoAppLaunchResponse(
                context,
                locale,
                Launch.GetVideoAppLaunchUrl(item, user),
                item.Name,
                Launch.BuildVideoLaunchSpeech(item, locale, _userDataManager, jellyfinUser, Launch.GetAnnounceNowPlaying(user))));
        }

        // Folder items (audiobooks, music folders, etc.) need to be resolved to their
        // first audio child. Without this, Launch.GetStreamUrl() generates /Audio/{folderId}/stream
        // which fails because Folders don't have media sources.
        if (item is Folder folder)
        {
            if (Util.PodcastEpisodeResolver.IsSeriesShape(folder))
            {
                // JF-599: a tapped podcast Series (the IlPost carousel surface). The
                // generic ParentId+MediaTypes=Audio child query below returns ZERO
                // episodes for this shape (they are Episode items under season
                // folders, so the series is an ancestor and the media type is Video),
                // and the tap would answer FolderNoPlayableContent. The shared
                // resolver plays the newest episode instead. REACH: this keys on
                // ANY Series (no podcast discriminator exists; the IlPost library
                // is CollectionType=tvshows like a real TV library), so a tap on a
                // real TV show in a series carousel also plays its newest episode
                // audio-only (the pre-change behavior was the FolderNoPlayableContent
                // refusal; tapped Episode items already played audio-only).
                string seriesLocale = GetLocale(request);
                var (seriesUser, seriesUserError) = ResolveJellyfinUser(_userManager, session.UserId, seriesLocale);
                if (seriesUserError != null)
                {
                    return Task.FromResult(seriesUserError);
                }

                var episodeQuery = Util.PodcastEpisodeResolver.BuildLatestEpisodeQuery(folder, seriesUser);
                var episodes = _libraryManager.GetItemList(episodeQuery);
                if (episodes.Count == 0)
                {
                    Logger.LogWarning("AplUserEvent HandleSelectItem: podcast series {FolderName} has no episodes", folder.Name);
                    return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("FolderNoPlayableContent", seriesLocale)));
                }

                item = episodes[0];
                itemIdStr = item.Id.ToString();
                session.NowPlayingQueue = new List<QueueItem> { new() { Id = item.Id } };
                session.FullNowPlayingItem = item;
                Logger.LogDebug(
                    "AplUserEvent HandleSelectItem: resolved podcast series {FolderName} to newest episode {ChildName} ({ChildId})",
                    folder.Name, item.Name, itemIdStr);
            }
            else
            {
                // Multi-disc albums play disc-then-track (JF-339 AC#3); other folders
                // (audiobook/artist folders) keep SortName.
                bool isAlbum = folder is MediaBrowser.Controller.Entities.Audio.MusicAlbum;
                var childQuery = new InternalItemsQuery
                {
                    ParentId = folder.Id,
                    MediaTypes = new[] { MediaType.Audio },
                    Recursive = true,
                    Limit = 500,
                    OrderBy = isAlbum
                        ? QueueContinuationFetcher.AlbumTrackOrder
                        : new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
                };

                var children = _libraryManager.GetItemList(childQuery);

                if (children.Count == 0)
                {
                    Logger.LogWarning("AplUserEvent HandleSelectItem: folder {FolderName} has no audio children", folder.Name);
                    string locale = GetLocale(request);
                    return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("FolderNoPlayableContent", locale)));
                }

                item = children[0];
                itemIdStr = item.Id.ToString();

                Logger.LogDebug(
                    "AplUserEvent HandleSelectItem: resolved folder {FolderName} to first child {ChildName} ({ChildId})",
                    folder.Name, item.Name, itemIdStr);

                // Queue remaining children
                var queueItems = children.Select(c => new QueueItem { Id = c.Id }).ToList();
                session.NowPlayingQueue = queueItems;
                session.FullNowPlayingItem = item;
            }
        }
        else
        {
            session.NowPlayingQueue = new List<QueueItem> { new() { Id = item.Id } };
            session.FullNowPlayingItem = item;
        }

        int offsetMs = GetResumeOffset(item, session, request);

        // The codec-routed audio source (JF-507): a resolved Episode whose audio
        // codec has no Echo decoder rides the audio-only transcode; every other
        // resolved item keeps the static URL GetStreamUrl built.
        AudioLaunchSource source = Launch.ResolveAudioLaunchSource(item, itemIdStr, user, offsetMs);
        var response = Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source, itemIdStr, item, user, context);

        Launch.TryAttachNowPlayingDirective(response, item, itemIdStr, user, context);

        return Task.FromResult(response);
    }

    private int GetResumeOffset(BaseItem item, SessionInfo session, Request request)
    {
        string itemIdStr = item.Id.ToString("N");

        // 1. Check plugin's per-item state first (bypasses Jellyfin's min-resume thresholds)
        var queue = _queueManager.GetOrCreateQueue(session.DeviceId);
        if (queue.ItemPositionState.TryGetValue(itemIdStr, out long cachedTicks) && cachedTicks > 0)
        {
            int offsetMs = (int)TimeSpan.FromTicks(cachedTicks).TotalMilliseconds;
            Logger.LogInformation(
                "APL tap: resuming {ItemName} from {OffsetMs}ms (ItemPositionState)",
                item.Name, offsetMs);
            return offsetMs;
        }

        // 2. Fall back to Jellyfin's UserData
        var (jellyfinUser, _) = ResolveJellyfinUser(_userManager, session.UserId, GetLocale(request));
        if (jellyfinUser == null)
        {
            Logger.LogWarning("APL tap: could not resolve Jellyfin user {UserId} for resume offset check", session.UserId);
            return 0;
        }

        UserItemData? data = _userDataManager.GetUserData(jellyfinUser, item);
        if (data?.PlaybackPositionTicks > 0 && !data.Played)
        {
            int offsetMs = (int)TimeSpan.FromTicks(data.PlaybackPositionTicks).TotalMilliseconds;
            Logger.LogInformation(
                "APL tap: resuming {ItemName} from {OffsetMs}ms (UserData)",
                item.Name, offsetMs);
            return offsetMs;
        }

        return 0;
    }
}
