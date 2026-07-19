using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayVideoIntent — searches for movies and episodes by title
/// and launches video playback via the Alexa VideoApp interface.
/// </summary>
public class PlayVideoIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public PlayVideoIntentHandler(
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
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayVideo, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.VideoPlaybackEnabled, request) is { } disabled)
        {
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? titleQuery = intentRequest.Intent.Slots?.TryGetValue("title", out var slot) == true ? slot.Value : null;

        if (string.IsNullOrWhiteSpace(titleQuery))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchVideoTitle", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var videoSearchQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = titleQuery,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(videoSearchQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> videos = await RetryAsync(
            () => _libraryManager.GetItemList(videoSearchQuery),
            "GetVideos",
            cancellationToken).ConfigureAwait(false);

        if (videos.Count == 0)
        {
            var fuzzy = await SearchItemsFuzzyAsync(titleQuery, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.Movie, BaseItemKind.Episode }, cancellationToken, "PlayVideoFuzzyFallback").ConfigureAwait(false);
            if (fuzzy != null)
            {
                videos = new List<BaseItem> { fuzzy.Value.Item };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundVideo", locale, titleQuery));
            }
        }

        if (videos.Count > 1)
        {
            BaseItem? videoMatch = null;
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                titleQuery,
                videos,
                v => v.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeVideo,
                locale,
                best =>
                {
                    videoMatch = best;
                    return null!;
                },
                user: user);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                videos = new List<BaseItem> { videoMatch! };
            }
            else
            {
                var matches = videos.Take(3).Select(v => (v.Id, v.Name, (string?)GetImageUrl(v.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeVideo, locale, context);
            }
        }

        BaseItem video = videos[0];
        string itemId = video.Id.ToString();

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new QueueItem { Id = video.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = video;

        var response = new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                // VideoApp.Launch must NOT include shouldEndSession
                ShouldEndSession = null,
                Directives = new List<IDirective>
                {
                    new VideoAppLaunchDirective
                    {
                        VideoItem = new VideoItem
                        {
                            Source = GetVideoStreamUrl(itemId, user),
                            Metadata = new VideoItemMetadata
                            {
                                Title = video.Name
                            }
                        }
                    }
                }
            }
        };

        // Check for existing playback progress and announce resume position.
        // Note: Alexa VideoApp does not support seek/offset natively, so the video
        // will start from the beginning, but we inform the user where they left off.
        UserItemData? userData = _userDataManager.GetUserData(jellyfinUser!, video);
        long resumeTicks = userData?.PlaybackPositionTicks ?? 0;
        if (resumeTicks > 0)
        {
            Logger.LogInformation("PlayVideo: resuming {Title} from {Position}", video.Name, FormatPosition(resumeTicks));
        }

        // Alexa VideoApp does not support seek/offset natively (the video starts from the
        // beginning); the announce only informs the user where they left off.
        response.Response.OutputSpeech = BuildVideoLaunchSpeech(video, locale, resumeTicks);

        return response;
    }
}
