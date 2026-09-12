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
    /// <summary>
    /// Every PlayEpisodeIntent slot, for the series_name elicit's updatedIntent
    /// (Amazon rejects a partial one; hoisted because a constant array argument
    /// fires CA1861 and these slots have no IntentNames.Slots constants).
    /// </summary>
    private static readonly string[] AllSlotNames = { "series_name", "season_number", "episode_number" };

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

        // Escape hatch from the elicitation trap (JF-549, same regime FindSong hit live
        // 2026-08-28): while the series_name elicit below is open, Alexa captures the
        // user's next utterance INTO the slot instead of routing it, so a bare
        // stop/cancel word arrives as series_name="stop"/"ferma" with dialogState
        // IN_PROGRESS. That is a cancel, not a search for a series named "ferma".
        if (Util.CancelWords.IsDialogInProgress(intentRequest) && Util.CancelWords.AnySlotIsCancelWord(intentRequest, locale))
        {
            Logger.LogInformation("PlayEpisode: cancel during open series elicit, ending flow");
            return ResponseBuilder.Tell(ResponseStrings.Get("FindSongCancelled", locale));
        }

        if (string.IsNullOrWhiteSpace(seriesName))
        {
            // JF-549 live incident 2026-09-12 17:45: this branch used to Tell the
            // question, so it shipped with shouldEndSession=true and Alexa asked
            // "Quale serie vorresti guardare?" with the mic closed; the user's answer
            // went nowhere (three identical device attempts). Elicit instead: the
            // reply is captured into series_name and the intent re-arrives complete.
            // Requires PlayEpisodeIntent in the model's dialog.intents (all 17 already
            // register it, anti-pattern #9).
            return BuildDialogElicitResponse(
                "DidNotCatchSeriesName",
                locale,
                "series_name",
                IntentNames.PlayEpisode,
                AllSlotNames);
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
            return await PlayNextUpEpisodeAsync(_tvSeriesManager, _libraryManager, _userDataManager, jellyfinUser!, user, session, series, locale, context, request, cancellationToken).ConfigureAwait(false);
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

        Logger.LogDebug(
            "PlayEpisode: returning VideoApp, itemId={ItemId}, episode='{EpisodeName}'",
            itemId, episode.Name);

        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        // JF-501: the announce is spoken progressively (directive-only final response).
        return await BuildVideoAppLaunchResponseAsync(
            context,
            request,
            locale,
            GetVideoAppLaunchUrl(episode, user),
            episode.Name,
            BuildNowPlayingSpeech(episode.Name, locale, GetAnnounceNowPlaying(user))).ConfigureAwait(false);
    }
}
