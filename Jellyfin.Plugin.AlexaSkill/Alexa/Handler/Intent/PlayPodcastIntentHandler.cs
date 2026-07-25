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
/// Handler for PlayPodcastIntent, plays the latest episode of a podcast.
/// Jellyfin has no native podcast type, so a podcast is stored as a MusicAlbum of
/// Audio tracks in a Music library; this handler queries MusicAlbum by name and plays
/// its newest Audio child (DateCreated descending) as the latest episode.
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
        if (IfFeatureDisabled(c => c.PodcastsEnabled, request) is { } disabled)
        {
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? podcastName = intentRequest.Intent.Slots?.TryGetValue("podcast_name", out var nameSlot) == true
            ? nameSlot.Value
            : null;

        if (string.IsNullOrWhiteSpace(podcastName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchPodcastName", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingPodcast", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // Jellyfin has no native podcast type: a podcast is stored as a MusicAlbum of
        // Audio tracks in a Music library (verified against live Jellyfin 10.11.x; a
        // Series rollup is always MediaType=Unknown, so the old Series+MediaTypes=Audio
        // query matched nothing). Query MusicAlbum by name; the MediaTypes=Audio filter
        // is intentionally omitted because the album rollup is also MediaType=Unknown.
        var podcastQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = podcastName,
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(podcastQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> podcasts = await RetryAsync(
            () => _libraryManager.GetItemList(podcastQuery),
            "GetPodcasts",
            cancellationToken).ConfigureAwait(false);

        if (podcasts.Count == 0)
        {
            var fuzzy = await SearchItemsFuzzyAsync(podcastName, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.MusicAlbum }, cancellationToken, "PlayPodcastFuzzyFallback").ConfigureAwait(false);
            if (fuzzy != null)
            {
                podcasts = new List<BaseItem> { fuzzy.Value.Item };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPodcast", locale, podcastName));
            }
        }

        if (podcasts.Count > 1)
        {
            BaseItem? podcastMatch = null;
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                podcastName,
                podcasts,
                p => p.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypePodcast,
                locale,
                best =>
                {
                    podcastMatch = best;
                    return null!;
                },
                user: user);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                podcasts = new List<BaseItem> { podcastMatch! };
            }
            else
            {
                return DisambiguationHelper.AskFirstMatch(
                    podcasts.Select(p => (p.Id, p.Name, (string?)GetImageUrl(p.Id.ToString("N"), user))).ToList(),
                    DisambiguationHelper.MediaTypePodcast,
                    locale,
                    context);
            }
        }

        BaseItem podcast = podcasts[0];

        // Get the latest episode (newest Audio track) under this podcast album.
        // ParentId (not AncestorIds) matches the album-track convention in PlayAlbumIntentHandler:
        // an Audio track's direct parent is the MusicAlbum, with no intermediate "season" level.
        var episodeQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            ParentId = podcast.Id,
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
