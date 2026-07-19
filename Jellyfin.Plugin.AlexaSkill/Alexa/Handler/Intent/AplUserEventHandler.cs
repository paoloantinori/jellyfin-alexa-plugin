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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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
                return HandlePrevious(user, session, context);
            case "pause":
                return Task.FromResult(BuildPauseResponse());
            case "next":
                return HandleNext(user, session, context);
            case "selectItem":
            case "playTrack":
            case "carouselTap":
                return HandleSelectItem(aplEvent, user, session, context, request);
            default:
                return Task.FromResult(ResponseBuilder.Empty());
        }
    }

    private Task<SkillResponse> HandleNext(Entities.User user, SessionInfo session, Context context)
    {
        if (session.NowPlayingQueue.Count == 0 || session.FullNowPlayingItem == null)
        {
            return Task.FromResult(ResponseBuilder.Empty());
        }

        for (int i = 0; i < session.NowPlayingQueue.Count - 1; i++)
        {
            if (session.NowPlayingQueue[i].Id == session.FullNowPlayingItem.Id)
            {
                Guid nextItemId = session.NowPlayingQueue[i + 1].Id;
                string nextIdStr = nextItemId.ToString();
                BaseItem? nextItem = _libraryManager.GetItemById(nextItemId);
                if (nextItem == null)
                {
                    return Task.FromResult(ResponseBuilder.Empty());
                }

                session.FullNowPlayingItem = nextItem;
                return Task.FromResult(BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(nextIdStr, user), nextIdStr, nextItem, user, context));
            }
        }

        return Task.FromResult(ResponseBuilder.Empty());
    }

    private Task<SkillResponse> HandlePrevious(Entities.User user, SessionInfo session, Context context)
    {
        if (session.NowPlayingQueue.Count == 0 || session.FullNowPlayingItem == null)
        {
            return Task.FromResult(ResponseBuilder.Empty());
        }

        for (int i = 1; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == session.FullNowPlayingItem.Id)
            {
                Guid prevItemId = session.NowPlayingQueue[i - 1].Id;
                string prevIdStr = prevItemId.ToString();
                BaseItem? prevItem = _libraryManager.GetItemById(prevItemId);
                if (prevItem == null)
                {
                    return Task.FromResult(ResponseBuilder.Empty());
                }

                session.FullNowPlayingItem = prevItem;
                return Task.FromResult(BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(prevIdStr, user), prevIdStr, prevItem, user, context));
            }
        }

        return Task.FromResult(ResponseBuilder.Empty());
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
            var (jellyfinUser, _) = ResolveJellyfinUser(_userManager, session.UserId, locale);
            return Task.FromResult(new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    // VideoApp.Launch must NOT include shouldEndSession
                    ShouldEndSession = null,
                    OutputSpeech = BuildVideoLaunchSpeech(item, locale, _userDataManager, jellyfinUser),
                    Directives = new List<IDirective>
                    {
                        new VideoAppLaunchDirective
                        {
                            VideoItem = new Directive.VideoItem
                            {
                                Source = GetVideoStreamUrl(itemIdStr, user),
                                Metadata = new Directive.VideoItemMetadata { Title = item.Name }
                            }
                        }
                    }
                }
            });
        }

        // Folder items (audiobooks, music folders, etc.) need to be resolved to their
        // first audio child. Without this, GetStreamUrl() generates /Audio/{folderId}/stream
        // which fails because Folders don't have media sources.
        if (item is Folder folder)
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
        else
        {
            session.NowPlayingQueue = new List<QueueItem> { new() { Id = item.Id } };
            session.FullNowPlayingItem = item;
        }

        int offsetMs = GetResumeOffset(item, session, request);

        var response = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemIdStr, user), itemIdStr, item, user, context, offsetMs);

        TryAttachNowPlayingDirective(response, item, itemIdStr, user, context);

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
