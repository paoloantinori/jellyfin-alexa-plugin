using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Handler for PlayEpisodeIntent — plays a specific TV episode by series name,
/// season number, and episode number via the Alexa VideoApp interface.
/// </summary>
public class PlayEpisodeIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PlayEpisodeIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "PlayEpisodeIntent", StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? seriesName = intentRequest.Intent.Slots?.TryGetValue("series_name", out var seriesSlot) == true ? seriesSlot.Value : null;

        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchSeriesName", locale));
        }

        string? seasonRaw = intentRequest.Intent.Slots?.TryGetValue("season_number", out var seasonSlot) == true ? seasonSlot.Value : null;
        string? episodeRaw = intentRequest.Intent.Slots?.TryGetValue("episode_number", out var episodeSlot) == true ? episodeSlot.Value : null;

        if (!int.TryParse(seasonRaw, CultureInfo.InvariantCulture, out int seasonNumber)
            || !int.TryParse(episodeRaw, CultureInfo.InvariantCulture, out int episodeNumber))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchEpisodeNumber", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        Jellyfin.Database.Implementations.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);

        var seriesQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = seriesName,
            IncludeItemTypes = new[] { BaseItemKind.Series },
            DtoOptions = new DtoOptions(true)
        };
        IReadOnlyList<BaseItem> seriesList = _libraryManager.GetItemList(seriesQuery);

        if (seriesList.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSeries", locale, seriesName));
        }

        BaseItem series = seriesList[0];

        var episodeQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series.Id },
            ParentIndexNumber = seasonNumber,
            DtoOptions = new DtoOptions(true)
        };
        IReadOnlyList<BaseItem> episodes = _libraryManager.GetItemList(episodeQuery);

        BaseItem? episode = episodes.FirstOrDefault(e => e.IndexNumber == episodeNumber);

        if (episode == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundEpisode", locale, seasonNumber.ToString(CultureInfo.InvariantCulture), episodeNumber.ToString(CultureInfo.InvariantCulture), seriesName));
        }

        string itemId = episode.Id.ToString();

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new QueueItem { Id = episode.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = episode;

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
                                Title = episode.Name
                            }
                        }
                    }
                }
            }
        };

        return response;
    }
}
