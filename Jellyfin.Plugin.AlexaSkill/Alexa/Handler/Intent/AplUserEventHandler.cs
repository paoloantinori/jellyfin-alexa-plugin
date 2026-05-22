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
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

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

    public AplUserEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is AplUserEventRequest;
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
            return Task.FromResult(ResponseBuilder.Empty());
        }

        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return Task.FromResult(ResponseBuilder.Empty());
        }

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = item.Id } };
        session.FullNowPlayingItem = item;

        if (item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            return Task.FromResult(new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    ShouldEndSession = true,
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

        int offsetMs = GetResumeOffset(item, session, request);

        return Task.FromResult(BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemIdStr, user), itemIdStr, item, user, context, offsetMs));
    }

    private int GetResumeOffset(BaseItem item, SessionInfo session, Request request)
    {
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
                "APL tap: resuming {ItemName} from {OffsetMs}ms (saved position)",
                item.Name, offsetMs);
            return offsetMs;
        }

        return 0;
    }
}
