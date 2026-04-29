using System;
using System.Collections.Generic;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
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

    public PlayVideoIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "PlayVideoIntent", StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        IntentRequest intentRequest = (IntentRequest)request;
        string? titleQuery = intentRequest.Intent.Slots?.TryGetValue("title", out var slot) == true ? slot.Value : null;

        if (string.IsNullOrWhiteSpace(titleQuery))
        {
            return ResponseBuilder.Tell("I didn't catch the video title. Please try again.");
        }

        Jellyfin.Data.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);

        List<BaseItem> videos = _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = titleQuery,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            DtoOptions = new DtoOptions(true)
        });

        if (videos.Count == 0)
        {
            return ResponseBuilder.Tell(FormattableString.Invariant($"Sorry, I couldn't find any video with the title {titleQuery}."));
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
                ShouldEndSession = true,
                Directives = new List<IDirective>
                {
                    new VideoAppLaunchDirective
                    {
                        VideoItem = new VideoItem
                        {
                            Source = GetStreamUrl(itemId, user),
                            Metadata = new VideoItemMetadata
                            {
                                Title = video.Name
                            }
                        }
                    }
                }
            }
        };

        return response;
    }
}
