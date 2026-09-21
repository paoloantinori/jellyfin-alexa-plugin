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
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayPodcastIntent, plays the latest episode of a podcast.
/// Jellyfin has no native podcast type, so podcasts are stored under one of two
/// shapes (JF-599): a MusicAlbum of Audio tracks in a Music library (the community
/// plugins), or a Series of Episode items under season folders (the IlPost plugin).
/// This handler queries MusicAlbum first, falls back to Series on a miss, and plays
/// the matched item's newest child (DateCreated descending) as the latest episode.
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

        // JF-550 (dead-mic sweep; JF-549 class).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "PlayPodcast") is { } elicitCancel)
        {
            return elicitCancel;
        }

        if (string.IsNullOrWhiteSpace(podcastName))
        {
            return BuildDialogElicitResponse("DidNotCatchPodcastName", locale, "podcast_name", IntentNames.PlayPodcast, "podcast_name");
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingPodcast", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // The MediaTypes filter is intentionally omitted on both shape queries: a
        // MusicAlbum AND a Series rollup are MediaType=Unknown in Jellyfin, so a
        // MediaTypes=Audio filter would exclude every podcast container (the dead
        // JF-373 query shape).
        async Task<IReadOnlyList<BaseItem>> QueryKindsAsync(BaseItemKind[] kinds, string label)
        {
            var query = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                SearchTerm = podcastName,
                IncludeItemTypes = kinds,
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(query, user, _libraryManager);

            return await RetryAsync(
                () => _libraryManager.GetItemList(query),
                label,
                cancellationToken).ConfigureAwait(false);
        }

        // JF-599: two storage shapes. The community plugins store a podcast as a
        // MusicAlbum of Audio tracks; the IlPost plugin stores podcasts as a Series
        // of Episode items under season folders (live-verified 2026-09-20: 78 series
        // / 677 episodes and ZERO MusicAlbums in that library), so a MusicAlbum-only
        // search never found them and the podcast play path answered NotFound for
        // the whole library. The Series fallback runs only when the album query
        // matched nothing, keeping the community-plugin path byte-identical. The
        // fallback deliberately admits real TV Series too: no type-level
        // discriminator exists (the IlPost library is CollectionType=tvshows, the
        // same as a real TV library), and a matched TV episode still rides the
        // codec-routed launch below, so the worst case is a content miss on a
        // "podcast"-phrased query, never a broken launch.
        IReadOnlyList<BaseItem> podcasts = await QueryKindsAsync(new[] { BaseItemKind.MusicAlbum }, "GetPodcasts").ConfigureAwait(false);
        if (podcasts.Count == 0)
        {
            podcasts = await QueryKindsAsync(new[] { BaseItemKind.Series }, "GetPodcastSeries").ConfigureAwait(false);
            if (podcasts.Count > 0)
            {
                Logger.LogDebug("PlayPodcast: album query missed, series shape matched {Count} candidates (first='{FirstName}' id={FirstId})", podcasts.Count, podcasts[0].Name, podcasts[0].Id);
            }
        }

        if (podcasts.Count == 0)
        {
            var fuzzy = await Search.SearchItemsFuzzyAsync(podcastName, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.MusicAlbum, BaseItemKind.Series }, cancellationToken, "PlayPodcastFuzzyFallback", locale: locale).ConfigureAwait(false);
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
            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                podcastName,
                podcasts,
                p => p.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypePodcast,
                locale,
                best =>
                {
                    podcastMatch = best;
                    return Task.FromResult<SkillResponse>(null!);
                },
                user: user).ConfigureAwait(false);

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
                    podcasts.Select(p => (p.Id, p.Name, (string?)Launch.GetImageUrl(p.Id.ToString("N"), user))).ToList(),
                    DisambiguationHelper.MediaTypePodcast,
                    locale,
                    context);
            }
        }

        BaseItem podcast = podcasts[0];

        // The shared resolve-to-launch tail (JF-605): the intent handler, the
        // disambiguation yes, and the APL tap all play through the same sequence
        // (retry-budgeted episode query, NoEpisodesInPodcast empty answer,
        // codec-routed launch). Fresh play, offset 0.
        return await Util.PodcastEpisodeResolver.PlayLatestEpisodeAsync(
            _libraryManager,
            Launch,
            Logger,
            "GetPodcastEpisodes",
            podcast,
            jellyfinUser,
            user,
            session,
            context,
            locale,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
