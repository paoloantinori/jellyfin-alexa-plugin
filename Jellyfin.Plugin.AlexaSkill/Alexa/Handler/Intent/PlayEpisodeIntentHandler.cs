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
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayEpisodeIntent: plays a specific TV episode by series name,
/// season number, and episode number via the Alexa VideoApp interface. Explicit
/// season+episode still wins; a series-only request (JF-324) no longer hard-fails
/// with "didn't catch the episode number" and falls back to the shared NextUp core
/// instead.
/// </summary>
public class PlayEpisodeIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ITVSeriesManager _tvSeriesManager;

    public PlayEpisodeIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ITVSeriesManager tvSeriesManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _tvSeriesManager = tvSeriesManager;
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
        if (IfFeatureDisabled(c => c.VideoPlaybackEnabled, request) is { } disabled)
        {
            Logger.LogDebug("PlayEpisode: feature disabled (VideoPlaybackEnabled), returning disabled response");
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? seriesName = intentRequest.Intent.Slots?.TryGetValue("series_name", out var seriesSlot) == true ? seriesSlot.Value : null;

        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchSeriesName", locale));
        }

        string? seasonRaw = intentRequest.Intent.Slots?.TryGetValue("season_number", out var seasonSlot) == true ? seasonSlot.Value : null;
        string? episodeRaw = intentRequest.Intent.Slots?.TryGetValue("episode_number", out var episodeSlot) == true ? episodeSlot.Value : null;

        Logger.LogDebug("PlayEpisode: seriesName='{SeriesName}', season={Season}, episode={Episode}, locale={Locale}", seriesName, seasonRaw, episodeRaw, locale);

        // ItalianNumberWords parses digits (every locale) AND the Italian number
        // words the it-IT model delivers for the ItalianNumber-typed season_number /
        // episode_number slots ("stagione due" arrives as "due", not "2"; JF-451
        // adoption).
        int seasonNumber = 0;
        int episodeNumber = 0;
        bool hasExplicitNumbers = Util.ItalianNumberWords.TryParse(seasonRaw, out seasonNumber)
            && Util.ItalianNumberWords.TryParse(episodeRaw, out episodeNumber);
        if (!hasExplicitNumbers)
        {
            Logger.LogDebug(
                "PlayEpisode: season/episode not parseable (season='{Season}', episode='{Episode}'), falling back to next-up",
                seasonRaw,
                episodeRaw);
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var (series, seriesError) = await ResolveSeriesForPlaybackAsync(_libraryManager, jellyfinUser!, user, seriesName, locale, cancellationToken).ConfigureAwait(false);
        if (seriesError != null || series is null)
        {
            return seriesError!;
        }

        Logger.LogDebug("PlayEpisode: matched series='{SeriesName}' (id={SeriesId})", series.Name, series.Id);

        if (!hasExplicitNumbers)
        {
            return await PlayNextUpEpisodeAsync(_tvSeriesManager, _libraryManager, _userDataManager, jellyfinUser!, user, session, series, locale, cancellationToken).ConfigureAwait(false);
        }

        var episodeQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series.Id },
            ParentIndexNumber = seasonNumber,
            DtoOptions = new DtoOptions(true)
        };
        Logger.LogDebug("PlayEpisode: querying episodes for seriesId={SeriesId}, season={Season}", series.Id, seasonNumber);
        IReadOnlyList<BaseItem> episodes = await RetryAsync(() => _libraryManager.GetItemList(episodeQuery), "GetEpisodes", cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("PlayEpisode: Jellyfin returned {EpisodeCount} episodes for season {Season}", episodes.Count, seasonNumber);

        BaseItem? episode = episodes.FirstOrDefault(e => e.IndexNumber == episodeNumber);

        if (episode == null)
        {
            Logger.LogDebug("PlayEpisode: episode S{Season}E{Episode} not found for series='{SeriesName}'", seasonNumber, episodeNumber, seriesName);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundEpisode", locale, seasonNumber.ToString(CultureInfo.InvariantCulture), episodeNumber.ToString(CultureInfo.InvariantCulture), seriesName));
        }

        Logger.LogDebug("PlayEpisode: matched episode='{EpisodeName}' (id={EpisodeId})", episode.Name, episode.Id);

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
                // VideoApp.Launch must NOT include shouldEndSession
                ShouldEndSession = null,
                OutputSpeech = BuildNowPlayingSpeech(episode.Name, locale, GetAnnounceNowPlaying(user)),
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

        Logger.LogDebug(
            "PlayEpisode: returning VideoApp, itemId={ItemId}, episode='{EpisodeName}'",
            itemId, episode.Name);
        return response;
    }
}
