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
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayPodcastIntent — plays the latest episode of a podcast.
/// Jellyfin stores podcasts as Series items with audio media type;
/// individual episodes are Episode items under the series.
/// </summary>
public class PlayPodcastIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PlayPodcastIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayPodcast, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? podcastName = intentRequest.Intent.Slots?.TryGetValue("podcast_name", out var nameSlot) == true
            ? nameSlot.Value
            : null;

        if (string.IsNullOrWhiteSpace(podcastName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchPodcastName", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingPodcast", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // Search for podcast series (Series with audio media type = podcast)
        var podcastQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = podcastName,
            IncludeItemTypes = new[] { BaseItemKind.Series },
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<BaseItem> podcasts = await RetryAsync(
            () => _libraryManager.GetItemList(podcastQuery),
            "GetPodcasts",
            cancellationToken).ConfigureAwait(false);

        if (podcasts.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPodcast", locale, podcastName));
        }

        if (podcasts.Count > 1)
        {
            BaseItem? topMatch = FuzzyMatch(podcastName, podcasts, p => p.Name);
            if (topMatch == null)
            {
                return DisambiguationHelper.AskFirstMatch(
                    podcasts.Select(p => (p.Id, p.Name)).ToList(),
                    DisambiguationHelper.MediaTypePodcast,
                    locale);
            }

            podcasts = new List<BaseItem> { topMatch };
        }

        BaseItem podcast = podcasts[0];

        // Get the latest episode under this podcast series
        var episodeQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { podcast.Id },
            MediaTypes = new[] { MediaType.Audio },
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<BaseItem> episodes = await RetryAsync(
            () => _libraryManager.GetItemList(episodeQuery),
            "GetPodcastEpisodes",
            cancellationToken).ConfigureAwait(false);

        if (episodes.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoEpisodesInPodcast", locale, podcast.Name));
        }

        BaseItem episode = episodes[0];
        string itemId = episode.Id.ToString();

        List<QueueItem> queueItems = new()
        {
            new QueueItem { Id = episode.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = episode;

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, episode, user, context);
    }
}
