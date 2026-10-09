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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;

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

        // Escape hatch from the elicitation trap (JF-549; the shared hoisted leg
        // lives in BuildCancelDuringOpenElicit, JF-550).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "PlayEpisode") is { } elicitCancel)
        {
            return elicitCancel;
        }

        string? seasonRaw = intentRequest.Intent.Slots?.TryGetValue("season_number", out var seasonSlot) == true ? seasonSlot.Value : null;
        string? episodeRaw = intentRequest.Intent.Slots?.TryGetValue("episode_number", out var episodeSlot) == true ? episodeSlot.Value : null;

        if (string.IsNullOrWhiteSpace(seriesName))
        {
            // JF-549 live incident 2026-09-12 17:45: this branch used to Tell the
            // question, so it shipped with shouldEndSession=true and Alexa asked
            // "Quale serie vorresti guardare?" with the mic closed; the user's answer
            // went nowhere (three identical device attempts). Elicit instead: the
            // reply is captured into series_name and the intent re-arrives complete.
            // Requires PlayEpisodeIntent in the model's dialog.intents (all 17 already
            // register it, anti-pattern #9).
            // JF-814 gate-marker F1 (the JF-614 contract): echo the already-captured
            // numbers as slotValues; a value-less updatedIntent may be treated as
            // dialog-state replacement and wipe them.
            return BuildElicitSlotResponse(
                IntentNames.PlayEpisode,
                "series_name",
                Util.ElicitSlots.For(IntentNames.PlayEpisode),
                ResponseStrings.Get("DidNotCatchSeriesName", locale),
                slotValues: new Dictionary<string, string?>
                {
                    ["series_name"] = null,
                    ["season_number"] = seasonRaw,
                    ["episode_number"] = episodeRaw,
                });
        }

        Logger.LogDebug("PlayEpisode: seriesName='{SeriesName}', season={Season}, episode={Episode}, locale={Locale}", seriesName, seasonRaw, episodeRaw, locale);

        // The season_number / episode_number slots are AMAZON.NUMBER in ALL 17
        // locales since JF-814 part 1 (the it-IT ItalianNumber custom type carried
        // no compounds: "cinquantaquattro" arrived as 4), so answers normally
        // arrive as digits; ItalianNumberWords keeps parsing Italian number words
        // as a tolerance layer for word-shaped deliveries ("stagione due").
        int seasonNumber = 0;
        int episodeNumber = 0;
        bool episodeParsed = Util.ItalianNumberWords.TryParse(episodeRaw, out episodeNumber);
        bool seasonParsed = Util.ItalianNumberWords.TryParse(seasonRaw, out seasonNumber);

        // JF-814: an explicit episode number WITHOUT a season must not fall through
        // to the NextUp core (that silently substitutes the next-up episode for the
        // requested one, the wrong-item class this task closes). Elicit the season;
        // the re-arrived answer parses via ItalianNumberWords (digits from the
        // AMAZON.NUMBER slot in every locale, Italian words in it-IT). The
        // fully-numberless series-only request (JF-324's original case) still takes
        // the NextUp fallback below. NOTE: a season-WITHOUT-episode ask still falls
        // to NextUp (no locale template carries that sample shape today); a future
        // season-only sample family must extend this gate or it re-opens the
        // wrong-item class (simplify/altitude finding, 2026-10-09).
        if (episodeParsed && !seasonParsed)
        {
            Logger.LogDebug(
                "PlayEpisode: episode number present but season missing (season='{Season}', episode='{Episode}'), eliciting season_number",
                seasonRaw,
                episodeRaw);
            // JF-614 contract (gate-marker F1): the captured series name and episode
            // number ride the updatedIntent as slotValues so the round-trip cannot
            // wipe them; the elicited season stays value-less.
            return BuildElicitSlotResponse(
                IntentNames.PlayEpisode,
                "season_number",
                Util.ElicitSlots.For(IntentNames.PlayEpisode),
                ResponseStrings.Get("DidNotCatchSeasonNumber", locale),
                slotValues: new Dictionary<string, string?>
                {
                    ["series_name"] = seriesName,
                    ["season_number"] = null,
                    ["episode_number"] = episodeRaw,
                });
        }

        bool hasExplicitNumbers = seasonParsed && episodeParsed;
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

        var (series, seriesError) = await TvNextUp.ResolveSeriesForPlaybackAsync(_libraryManager, jellyfinUser!, user, seriesName, locale, cancellationToken).ConfigureAwait(false);
        if (seriesError != null || series is null)
        {
            return seriesError!;
        }

        Logger.LogDebug("PlayEpisode: matched series='{SeriesName}' (id={SeriesId})", series.Name, series.Id);

        if (!hasExplicitNumbers)
        {
            return await TvNextUp.PlayNextUpEpisodeAsync(_tvSeriesManager, _libraryManager, _userDataManager, jellyfinUser!, user, session, series, locale, context, request, cancellationToken).ConfigureAwait(false);
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
        bool absoluteFallback = false;

        if (episode == null)
        {
            // JF-843: the per-season miss may be an ABSOLUTE-numbering ask (a
            // continuous run stored per-season; the full incident story lives on
            // TvNextUpService.GetEpisodeByAbsoluteNumberAsync). Fall back to the
            // Nth episode of the whole run in air order; the launch below
            // ANNOUNCES the mapping (asked number + resolved season/episode),
            // never a silent substitution. Covers the direct season-ed ask and
            // the post-elicit arrival alike: the JF-614 slotValues echo carries
            // the episode number through the season elicit, so both land here
            // with both numbers parsed.
            Logger.LogDebug(
                "PlayEpisode: episode S{Season}E{Episode} not found per-season for series='{SeriesName}', trying absolute resolution",
                seasonNumber, episodeNumber, seriesName);
            episode = await TvNextUp.GetEpisodeByAbsoluteNumberAsync(_libraryManager, jellyfinUser!, series, episodeNumber, cancellationToken).ConfigureAwait(false);
            absoluteFallback = episode != null;
            if (episode != null)
            {
                Logger.LogInformation(
                    "PlayEpisode: per-season S{Season}E{Episode} miss for series='{SeriesName}' resolved ABSOLUTELY to '{EpisodeName}' (S{ResolvedSeason}E{ResolvedEpisode})",
                    seasonNumber, episodeNumber, seriesName, episode.Name, episode.ParentIndexNumber, episode.IndexNumber);
            }
        }

        if (episode == null)
        {
            Logger.LogDebug("PlayEpisode: episode S{Season}E{Episode} not found for series='{SeriesName}'", seasonNumber, episodeNumber, seriesName);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundEpisode", locale, seasonNumber.ToString(CultureInfo.InvariantCulture), episodeNumber.ToString(CultureInfo.InvariantCulture), seriesName));
        }

        Logger.LogDebug("PlayEpisode: matched episode='{EpisodeName}' (id={EpisodeId})", episode.Name, episode.Id);

        string itemId = episode.Id.ToString();

        Logger.LogDebug(
            "PlayEpisode: returning VideoApp, itemId={ItemId}, episode='{EpisodeName}'",
            itemId, episode.Name);

        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        // JF-501: the announce is spoken progressively (directive-only final response).
        // JF-586: on a screenless device (an Echo Dot) the episode launch degrades to
        // the AudioPlayer audio-only route instead of the screen-required refusal;
        // resumeTicks stays 0 (the JF-565 fresh-play pin: an explicit season/episode
        // ask is a relaunch-from-scratch).
        // JF-843: the absolute fallback's announce is UNCONDITIONAL, unlike the
        // cosmetic now-playing readout the AnnounceNowPlaying toggle governs: the
        // mapping (the asked number IS the played season/episode) is load-bearing,
        // a silent substitution is exactly the wrong-item class this task closes.
        // The title rides the episode-announce family contract
        // (FormatEpisodeAnnounceTitle, the anti double-number-read formatter); the
        // Ssml twin carries the same text as the plain key (no markup needed).
        IOutputSpeech? announce = absoluteFallback
            ? SpeechBuilder.BuildOutputSpeech(
                "PlayingEpisodeByAbsoluteNumberSsml",
                "PlayingEpisodeByAbsoluteNumber",
                locale,
                episodeNumber.ToString(CultureInfo.InvariantCulture),
                episode.ParentIndexNumber.GetValueOrDefault().ToString(CultureInfo.InvariantCulture),
                episode.IndexNumber.GetValueOrDefault().ToString(CultureInfo.InvariantCulture),
                SpeechBuilder.FormatEpisodeAnnounceTitle(episode, locale) ?? episode.Name)
            : SpeechBuilder.BuildNowPlayingSpeech(episode.Name, locale, Launch.GetAnnounceNowPlaying(user));
        SkillResponse response = await Launch.BuildEpisodeLaunchResponseAsync(
            context,
            request,
            locale,
            episode,
            user,
            Launch.GetVideoAppLaunchUrl(episode, user),
            resumeTicks: 0,
            announce).ConfigureAwait(false);

        // JF-718: the now-playing writes follow the launch build and ride the
        // delivered-launch gate; the rationale lives on AttachNowPlayingIfLaunched
        // (the remux URL is token-gated, so a refusal throws before any write; the
        // episode degrade always carries a directive, so the gate is belt here).
        PlaybackLaunchBuilder.AttachNowPlayingIfLaunched(response, session, episode);
        return response;
    }
}
