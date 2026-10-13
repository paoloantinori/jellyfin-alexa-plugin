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
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
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

    // JF-882: the song-carrier guard's delegation target, REQUIRED (the
    // JF-876/JF-624 family shape: a dropped DI registration fails at container
    // construction, not on the first misrouted request).
    private readonly PlaySongIntentHandler _playSongHandler;

    public PlayEpisodeIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ITVSeriesManager tvSeriesManager,
        ILoggerFactory loggerFactory,
        PlaySongIntentHandler playSongHandler) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _tvSeriesManager = tvSeriesManager;
        _playSongHandler = playSongHandler;
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
            // JF-882: the JF-876 song-carrier misroute guard, at the MISS TAIL
            // (code-review round 2): series_name is the SAME catalog-backed
            // shape the movie slot is (SeriesName is catalog-wired in every
            // locale), so the same ER pull can select PlayEpisodeIntent for a
            // SONG carrier ask ("metti il brano X", X also a series name) on a
            // future trainer roll, and the statistical fill swallows the
            // carrier noun (the JF-509 class). Like the book site (and unlike
            // the movie site, whose PlayVideoIntent twin keeps a preemptive
            // guard safe), no alternate intent resolves a series and the song
            // path's JF-871 cascade video tier searches Movie+Episode names
            // only, so a preemptive delegation would make a genuine
            // carrier-titled series («La Traccia Perfetta» on a stale catalog)
            // unresolvable. The raw-first tail fixes both sides: the series
            // resolution above already served every resolvable series, and
            // only the confirmed miss delegates. TWO extra policy legs: an ask
            // with parseable numbers is an episode ask by construction (stand
            // down; it belongs to the NotFoundEpisode flow below), and the
            // whole-span escape (canonical-verbatim fill) keeps the honest
            // series not-found. Accepted cost (the JF-624 tap-delegation
            // family shape): the misrouted ask pays the series search first,
            // hears the searching announcement twice, and the video flag
            // answers before the tail can delegate.
            if (!hasExplicitNumbers
                && MultiValueErDisambiguation.IsSongCarrierMisroute(
                    seriesName,
                    Util.SlotValueHelper.GetCanonicalValue(intentRequest, "series_name"),
                    out string detectedSongQuery))
            {
                Logger.LogDebug("PlayEpisode: series miss on a song-carrier fill, delegating to PlaySong with '{SongQuery}'", detectedSongQuery);
                return await _playSongHandler.HandleAsync(
                    MultiValueErDisambiguation.BuildSongDelegation(intentRequest, seriesName!),
                    context, user, session, cancellationToken).ConfigureAwait(false);
            }

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

            // JF-624 item 4: flag on, when the asked season EXISTS with episodes
            // (the wrong-number shape: the miss is the episode number, not the
            // season), the NotFoundEpisode Tell ALSO carries the tappable
            // paginated episode picker so the user can see and tap the season's
            // real episodes. The spoken response is unchanged for both flag
            // states. Each row carries its own exact tap arguments (season,
            // episode, series name) via the documented per-item primaryAction
            // override: the row ordinal is NOT the episode number when seasons
            // have gaps or specials, and the tap must re-enter the REAL
            // PlayEpisode entry with exact numbers (its own feature gate, series
            // resolution, codec routing, and announce all re-run). A stateless
            // delegation: nothing rides session attributes, so the picker keeps
            // working after this Tell ends the session. A season query that
            // returned zero episodes (the wrong-SEASON shape) attaches nothing:
            // there is no list of this season's episodes to pick from. Rows are
            // only episodes WITH an IndexNumber: the tap re-enters by season +
            // episode number, so a numberless row would be a dead row.
            var notFound = ResponseBuilder.Tell(ResponseStrings.Get("NotFoundEpisode", locale, seasonNumber.ToString(CultureInfo.InvariantCulture), episodeNumber.ToString(CultureInfo.InvariantCulture), seriesName));
            if (episodes.Count > 0)
            {
                AplHelper.AttachPaginatedListIfEnabled(
                    notFound,
                    () => episodes
                        .Where(e => e.IndexNumber.HasValue)
                        .OrderBy(e => e.IndexNumber!.Value)
                        // The cap is applied before the per-row build so nothing
                        // past it is ever constructed (the builder re-checks it as
                        // the defensive layer).
                        .Take(AplHelper.PaginatedListRowCap)
                        .Select(e => new Apl.PaginatedListRow(
                            e.Name ?? string.Empty,
                            $"S{e.ParentIndexNumber ?? seasonNumber} · E{e.IndexNumber}",
                            new[]
                            {
                                AplHelper.EpisodePickTapAction,
                                (e.ParentIndexNumber ?? seasonNumber).ToString(CultureInfo.InvariantCulture),
                                e.IndexNumber!.Value.ToString(CultureInfo.InvariantCulture),
                                seriesName
                            }))
                        .ToList(),
                    series.Name ?? seriesName,
                    AplHelper.EpisodePickTapAction,
                    context);
            }

            return notFound;
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
