using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE latest-episode query for a matched podcast container, shared by every
/// entry point that resolves a podcast to its newest episode (JF-599): the
/// PlayPodcast intent handler, the disambiguation "yes" confirm (YesIntentHandler),
/// and the APL carousel tap (AplUserEventHandler). Podcasts live under two storage
/// shapes: a MusicAlbum of Audio tracks (the community plugins, direct children so
/// ParentId) or a Series of Episode items under season folders (the IlPost plugin,
/// the series is an ANCESTOR so AncestorIds). The series shape takes only the newest
/// episode (Limit 1, matching the TvNextUpService newest-episode cores) and skips
/// virtual placeholder episodes; the album shape keeps its pre-JF-599 form.
/// </summary>
public static class PodcastEpisodeResolver
{
    /// <summary>
    /// Gets a value indicating whether the matched podcast container is the Series
    /// storage shape (IlPost) rather than the MusicAlbum shape (community plugins).
    /// </summary>
    /// <param name="podcast">The matched podcast container item.</param>
    /// <returns>True when episodes must be resolved by ancestor, not parent.</returns>
    public static bool IsSeriesShape(BaseItem podcast)
        => podcast is MediaBrowser.Controller.Entities.TV.Series;

    /// <summary>Gets the storage-shape tag for debug logs ("series" or "album").</summary>
    /// <param name="podcast">The matched podcast container.</param>
    /// <returns>The shape name.</returns>
    private static string DescribeShape(BaseItem podcast)
        => IsSeriesShape(podcast) ? "series" : "album";

    /// <summary>
    /// The ONE podcast resolve-to-launch sequence (JF-605), shared by every entry
    /// point that plays a podcast's newest episode (the intent handler, the
    /// disambiguation "yes", the APL carousel tap). Policies reconciled
    /// deliberately: the episode query ALWAYS rides the shared request-budget
    /// retry (the APL tap used to call GetItemList raw, JF-609); the empty answer
    /// is ALWAYS <c>NoEpisodesInPodcast</c> (podcast-specific wording beats the
    /// folder-generic key the tap used to speak); the launch offset defaults to 0
    /// (fresh play) and a tap passes <paramref name="offsetFor"/> so an in-progress
    /// episode resumes.
    /// </summary>
    /// <param name="libraryManager">The library manager for the episode query.</param>
    /// <param name="launch">The caller's launch builder (codec-routed audio source, JF-507).</param>
    /// <param name="logger">The caller's logger (retry + triage lines).</param>
    /// <param name="retryLabel">The caller-scoped retry/triage label.</param>
    /// <param name="podcast">The matched podcast container (MusicAlbum or Series).</param>
    /// <param name="jellyfinUser">The linked Jellyfin user.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="session">The Jellyfin session (now-playing queue + item).</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="locale">The request locale, for the empty answer.</param>
    /// <param name="offsetFor">Optional map from the resolved episode to a resume offset in ms (the APL tap); null plays from 0.</param>
    /// <param name="attachScreen">Optional post-build hook receiving the response and the resolved episode (the APL tap attaches the NowPlaying screen; the intent and yes paths have none).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The play response, or the no-episodes Tell.</returns>
    public static async Task<SkillResponse> PlayLatestEpisodeAsync(
        ILibraryManager libraryManager,
        PlaybackLaunchBuilder launch,
        ILogger logger,
        string retryLabel,
        BaseItem podcast,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context? context,
        string locale,
        Func<BaseItem, int>? offsetFor = null,
        Action<SkillResponse, BaseItem>? attachScreen = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("{Label}: podcast container='{Name}' id={Id} shape={Shape}", retryLabel, podcast.Name, podcast.Id, DescribeShape(podcast));
        var episodeQuery = BuildLatestEpisodeQuery(podcast, jellyfinUser);

        IReadOnlyList<BaseItem> episodes = await RetryHelper.ExecuteWithRequestBudgetAsync(
            () => libraryManager.GetItemList(episodeQuery),
            logger,
            retryLabel,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (episodes.Count == 0)
        {
            logger.LogWarning("{Label}: podcast '{Name}' (id={Id}) has no episodes", retryLabel, podcast.Name, podcast.Id);
            return ResponseBuilder.Tell(ResponseStrings.Get("NoEpisodesInPodcast", locale, podcast.Name));
        }

        BaseItem episode = episodes[0];
        logger.LogDebug("{Label}: newest episode='{EpisodeName}' id={EpisodeId}", retryLabel, episode.Name, episode.Id);
        string itemId = episode.Id.ToString();
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = episode.Id } };
        session.FullNowPlayingItem = episode;

        // The codec-routed audio source (JF-507): a series-shape Episode whose
        // audio codec has no Echo decoder (a TV series matched by name, eac3/ac3)
        // rides the audio-only HLS transcode; an Audio item resolves to the same
        // static URL GetStreamUrl built.
        int offsetMs = offsetFor?.Invoke(episode) ?? 0;
        AudioLaunchSource source = launch.ResolveAudioLaunchSource(episode, itemId, user, offsetMs);
        SkillResponse response = launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source, itemId, episode, user, context);
        attachScreen?.Invoke(response, episode);
        return response;
    }

    /// <summary>
    /// Builds the newest-episode query for a matched podcast container, newest
    /// first. Private since JF-605: <see cref="PlayLatestEpisodeAsync"/> is the
    /// only consumer, so no caller can bypass its retry-budgeted read.
    /// </summary>
    /// <param name="podcast">The matched podcast container (MusicAlbum or Series).</param>
    /// <param name="jellyfinUser">The linked Jellyfin user.</param>
    /// <returns>The query for the episode read.</returns>
    private static InternalItemsQuery BuildLatestEpisodeQuery(BaseItem podcast, Jellyfin.Database.Implementations.Entities.User? jellyfinUser)
    {
        bool isSeriesShape = IsSeriesShape(podcast);
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = isSeriesShape
                ? new[] { BaseItemKind.Episode, BaseItemKind.Audio }
                : new[] { BaseItemKind.Audio },
            Limit = isSeriesShape ? 1 : null,
            IsVirtualItem = isSeriesShape ? false : null,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };
        if (isSeriesShape)
        {
            query.AncestorIds = new[] { podcast.Id };
        }
        else
        {
            query.ParentId = podcast.Id;
        }

        return query;
    }
}
