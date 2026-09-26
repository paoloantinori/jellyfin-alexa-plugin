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
using MediaBrowser.Controller.Entities.Audio;
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
/// Both shape queries always run and an exact case-insensitive name match across
/// their union wins before any fuzzy scoring (JF-640); fuzzy acceptance is
/// type-guarded so a podcast query never silently plays a music album. The matched
/// item's newest child (DateCreated descending) plays as the latest episode.
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
            return BuildDialogElicitResponse("DidNotCatchPodcastName", locale, "podcast_name", IntentNames.PlayPodcast, Util.ElicitSlots.For(IntentNames.PlayPodcast));
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
        // the whole library. JF-640: BOTH shape queries now run unconditionally. The
        // old conditional fallback (query Series only when the album query returned
        // zero) made the Series shape unreachable whenever any music album matched
        // the search term (live: SearchTerm=morning returned 4 music albums, so the
        // user's exactly-named 'Morning' podcast series was never queried and the
        // fuzzy fallback played a song off the album 'Euphoria Morning'). Two cheap
        // queries; the Series shape deliberately admits real TV Series too: no
        // type-level discriminator exists (the IlPost library is CollectionType=tvshows,
        // the same as a real TV library), and a matched TV episode still rides the
        // codec-routed launch below, so the worst case is a content miss on a
        // "podcast"-phrased query, never a broken launch.
        IReadOnlyList<BaseItem> albumMatches = await QueryKindsAsync(new[] { BaseItemKind.MusicAlbum }, "GetPodcasts").ConfigureAwait(false);
        IReadOnlyList<BaseItem> seriesMatches = await QueryKindsAsync(new[] { BaseItemKind.Series }, "GetPodcastSeries").ConfigureAwait(false);
        if (seriesMatches.Count > 0)
        {
            Logger.LogDebug("PlayPodcast: series shape matched {Count} candidates (first='{FirstName}' id={FirstId}) alongside {AlbumCount} album matches", seriesMatches.Count, seriesMatches[0].Name, seriesMatches[0].Id, albumMatches.Count);
        }

        var podcasts = new List<BaseItem>(albumMatches.Count + seriesMatches.Count);
        podcasts.AddRange(albumMatches);
        podcasts.AddRange(seriesMatches);

        // JF-640 exact-name pass: a candidate whose Name equals the spoken query
        // (case-insensitive) wins outright, before any fuzzy scoring. This is what
        // makes the exactly-named podcast reachable when music albums also matched
        // the search term (exact beats fuzzy; a Series exact beats an album fuzzy).
        // Multiple exact matches keep the multi-candidate disambiguation below,
        // scoped to the exact set only.
        List<BaseItem> exactMatches = podcasts
            .Where(p => string.Equals(p.Name, podcastName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatches.Count > 0)
        {
            Logger.LogDebug("PlayPodcast: exact name match for '{Query}' ({Count} candidates, first='{FirstName}')", podcastName, exactMatches.Count, exactMatches[0].Name);
            podcasts = exactMatches;
        }

        if (podcasts.Count == 0)
        {
            var fuzzy = await Search.SearchItemsFuzzyAsync(podcastName, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.MusicAlbum, BaseItemKind.Series }, cancellationToken, "PlayPodcastFuzzyFallback", locale: locale).ConfigureAwait(false);
            if (fuzzy != null)
            {
                // JF-640 cross-type fuzzy guard (single-candidate shape): a podcast
                // query must never silently play a music album (the JF-471 cross-shape
                // doctrine; live: 'morning' fuzzy-accepted 'Euphoria Morning' at 90
                // and played a song). A MusicAlbum fuzzy hit downgrades to the JF-377
                // yes/no confirm prompt even at score >= 90; a Series hit keeps the
                // auto-accept.
                if (fuzzy.Value.Item is MusicAlbum)
                {
                    Logger.LogDebug("PlayPodcast: fuzzy fallback matched music album '{Name}' score={Score} for query='{Query}' - downgrading to confirm prompt", fuzzy.Value.Item.Name, fuzzy.Value.Score, podcastName);
                    BaseItem album = fuzzy.Value.Item;
                    return DisambiguationHelper.AskFirstMatch(
                        new List<(Guid, string, string?)> { (album.Id, album.Name, Launch.GetImageUrl(album.Id.ToString("N"), user)) },
                        DisambiguationHelper.MediaTypePodcast,
                        locale,
                        context);
                }

                podcasts = new List<BaseItem> { fuzzy.Value.Item };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPodcast", locale, podcastName));
            }
        }

        if (podcasts.Count > 1)
        {
            // JF-640 cross-type fuzzy guard (multi-candidate shape): when the fuzzy
            // best would be a MusicAlbum, suppress the auto-play delegate so
            // HandleFuzzyMiss falls to its Confirm "did you mean" prompt instead of
            // auto-accepting at score >= 90. Exact album names were already picked
            // off by the exact pass above, so an album here is a fuzzy-only hit, and
            // a podcast query must never silently play a music album (the JF-471
            // cross-shape doctrine). A Series best keeps the auto-accept.
            BaseItem? podcastMatch = null;
            Func<BaseItem, Task<SkillResponse>>? autoPlay =
                best =>
                {
                    podcastMatch = best;
                    return Task.FromResult<SkillResponse>(null!);
                };
            var fuzzyBest = FuzzyMatcher.FindBestMatchWithScore(podcastName, podcasts, p => p.Name);
            if (fuzzyBest?.Item is MusicAlbum)
            {
                Logger.LogDebug("PlayPodcast: fuzzy best for '{Query}' is the music album '{Name}' (score={Score}) - downgrading to confirm prompt", podcastName, fuzzyBest.Value.Item.Name, fuzzyBest.Value.Score);
                autoPlay = null;
            }

            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                podcastName,
                podcasts,
                p => p.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypePodcast,
                locale,
                autoPlay,
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
            jellyfinUser!,
            user,
            session,
            context,
            locale,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
