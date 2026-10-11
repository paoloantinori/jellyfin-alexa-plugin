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
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

/// <summary>
/// Handles APL UserEvent requests from touch interactions on Echo Show devices.
/// Routes control actions (prev/pause/next), list item selections (selectItem/playTrack),
/// and carousel taps (carouselTap).
/// </summary>
public class AplUserEventHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager _queueManager;
    private readonly YesIntentHandler _yesHandler;
    private readonly FindSongIntentHandler _findSongHandler;
    private readonly GoToChapterIntentHandler _goToChapterHandler;
    private readonly PlayEpisodeIntentHandler _playEpisodeHandler;

    public AplUserEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        DeviceQueueManager queueManager,
        ILoggerFactory loggerFactory,
        YesIntentHandler yesHandler,
        FindSongIntentHandler findSongHandler,
        GoToChapterIntentHandler goToChapterHandler,
        PlayEpisodeIntentHandler playEpisodeHandler) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
        // JF-624 item 3: the disambiguation taps delegate to the SAME handlers the
        // spoken answers run, so a tap can never drift from the spoken word (the
        // per-type play routing, the feature/warming confirm gates, and the
        // resume/pagination precedence all come from the real entry points).
        _yesHandler = yesHandler;
        _findSongHandler = findSongHandler;
        // JF-624 item 4: same rule for the paginated picker taps (chapters and
        // episodes): the tap synthesizes the intent the spoken ask would have
        // delivered and runs the real handler entry.
        _goToChapterHandler = goToChapterHandler;
        _playEpisodeHandler = playEpisodeHandler;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is AplUserEventRequest;
    }

    /// <summary>
    /// Handle APL touch events with session attributes for pagination support.
    /// Routes "show more" taps to ListPaginationHelper.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        var aplEvent = (AplUserEventRequest)request;
        string? action = aplEvent.Arguments?.FirstOrDefault()?.ToString();

        Logger.LogDebug(
            "AplUserEvent: action={Action}, arguments={Args}",
            action,
            aplEvent.Arguments != null ? string.Join(", ", aplEvent.Arguments) : "(null)");

        if (action == "show more")
        {
            string locale = GetLocale(request);
            var paginationState = ListPaginationHelper.ReadState(sessionAttributes);
            if (paginationState == null)
            {
                return Task.FromResult(ResponseBuilder.Empty());
            }

            return Task.FromResult(ListPaginationHelper.BuildNextPageResponse(_libraryManager, paginationState, locale));
        }

        // JF-624 item 3: taps on the disambiguation text list ride this handler
        // like every other APL tap (no new handler architecture); they map onto
        // the answer state machine the rendered list belongs to.
        if (action == AplHelper.DisambiguationTapAction || action == AplHelper.FindSongTapAction)
        {
            return HandleDisambiguationTap(aplEvent, action!, sessionAttributes, user, session, context, cancellationToken);
        }

        // JF-624 item 4: taps on the paginated chapter/episode pickers ride the
        // same chain; each maps onto the real intent entry of its flow.
        if (action == AplHelper.ChapterPickTapAction || action == AplHelper.EpisodePickTapAction)
        {
            return HandlePickerTap(aplEvent, action!, context, user, session, cancellationToken);
        }

        return HandleAsync(request, context, user, session, cancellationToken);
    }

    /// <summary>
    /// Map a paginated-picker tap onto the real intent entry of the flow the
    /// picker was rendered for (JF-624 item 4). The taps are STATELESS by design:
    /// the pickers mount on session-ending Tells, so nothing can ride session
    /// attributes; the tap payload carries everything (the chapter picker's
    /// documented ${ordinal} is the 1-based chapter number because its rows are
    /// the whole chapter list; the episode picker's rows carry their own exact
    /// season/episode/series arguments via the documented per-item primaryAction
    /// override). The synthesized intent request is what the spoken ask would
    /// have delivered, and the REAL handler re-runs everything it always runs
    /// (the GoToChapter live-session and range checks; the PlayEpisode feature
    /// gate, series resolution, codec routing, and announce), so a stale
    /// document degrades to the flow's own honest answer (NoMediaPlaying,
    /// ChapterNotFound, NotFoundEpisode) instead of a wrong pick. A malformed
    /// payload (missing or non-numeric pick arguments) degrades to the empty
    /// keep-alive, the queue/carousel tap convention.
    /// </summary>
    private Task<SkillResponse> HandlePickerTap(
        AplUserEventRequest aplEvent,
        string action,
        Context context,
        Entities.User user,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        if (action == AplHelper.ChapterPickTapAction)
        {
            // Slot names match the handler's own reads ("chapter_number"): the
            // synthesized request must look exactly like the spoken ask.
            if (!TryTapArg(aplEvent, 1, out int chapterNumber) || chapterNumber < 1)
            {
                Logger.LogDebug("AplUserEvent chapter picker tap: no valid chapter number (args={Args})", aplEvent.Arguments);
                return Task.FromResult(ResponseBuilder.Empty());
            }

            IntentRequest tapRequest = SynthesizeTapIntent(
                IntentNames.GoToChapter,
                aplEvent,
                ("chapter_number", chapterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            Logger.LogDebug("AplUserEvent chapter picker tap: chapter {Chapter} mapped onto the GoToChapter entry", chapterNumber);
            return _goToChapterHandler.HandleAsync(tapRequest, context, user, session, cancellationToken);
        }

        // Episode picker: [action, season, episode, seriesName] (the row's own
        // numbers, so gaps/specials can never misresolve through position). A
        // season of 0 is valid (the specials season).
        if (!TryTapArg(aplEvent, 1, out int seasonNumber)
            || !TryTapArg(aplEvent, 2, out int episodeNumber)
            || seasonNumber < 0
            || episodeNumber < 1
            || string.IsNullOrWhiteSpace(aplEvent.Arguments?.ElementAtOrDefault(3)?.ToString()))
        {
            Logger.LogDebug(
                "AplUserEvent episode picker tap: invalid payload (season={Season}, episode={Episode}, series={Series})",
                aplEvent.Arguments?.ElementAtOrDefault(1)?.ToString(),
                aplEvent.Arguments?.ElementAtOrDefault(2)?.ToString(),
                aplEvent.Arguments?.ElementAtOrDefault(3)?.ToString());
            return Task.FromResult(ResponseBuilder.Empty());
        }

        string seriesName = aplEvent.Arguments![3]!.ToString();
        IntentRequest episodeTapRequest = SynthesizeTapIntent(
            IntentNames.PlayEpisode,
            aplEvent,
            ("series_name", seriesName),
            ("season_number", seasonNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("episode_number", episodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Logger.LogDebug(
            "AplUserEvent episode picker tap: S{Season}E{Episode} of '{Series}' mapped onto the PlayEpisode entry",
            seasonNumber, episodeNumber, seriesName);
        return _playEpisodeHandler.HandleAsync(episodeTapRequest, context, user, session, cancellationToken);
    }

    /// <summary>
    /// Read the Nth SendEvent argument of a tap as an invariant-culture integer
    /// (JF-624 item 4): the ONE tap-number parse contract for every tap family
    /// in this file (the disambiguation ordinal, the chapter number, and the
    /// episode season/number all read through it).
    /// </summary>
    private static bool TryTapArg(AplUserEventRequest aplEvent, int index, out int value)
    {
        string? raw = aplEvent.Arguments?.ElementAtOrDefault(index)?.ToString();
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Build the intent request a tap delegates to (JF-624 item 4): the request
    /// must look EXACTLY like what the spoken ask would have delivered, so the
    /// delegated handler's own slot reads and locale resolution behave
    /// identically (each slot's Name equals its key and matches the target
    /// handler's own reads; the locale rides the tap's locale, without which the
    /// delegated response would fall back to the default locale's speech; the
    /// RequestId rides the tap's own id, without which every progressive send
    /// the delegated flow makes would address a null request and always fail).
    /// </summary>
    // Internal (not private) as the tap-synthesis contract seam: the pin suite
    // asserts the request shape the delegated handlers depend on (RequestId,
    // Locale, slot names), the exact contract the JF-624 item 4 review's F1
    // found silently droppable.
    internal static IntentRequest SynthesizeTapIntent(
        string intentName,
        AplUserEventRequest source,
        params (string Slot, string Value)[] slots)
    {
        return new IntentRequest
        {
            RequestId = source.RequestId,
            Intent = new()
            {
                Name = intentName,
                Slots = slots.ToDictionary(
                    s => s.Slot,
                    s => new Slot { Name = s.Slot, Value = s.Value })
            },
            Locale = source.Locale
        };
    }

    /// <summary>
    /// Map a disambiguation text-list tap onto the state machine the list was
    /// rendered for (JF-624 item 3). The tap payload's second argument is the
    /// tapped row's 1-based <c>${ordinal}</c> (the AlexaTextList-documented
    /// binding). A disambigSelect tap rewrites the disambiguation state's cursor
    /// to the tapped row and runs the SAME yes-leg the spoken "yes" runs, so a
    /// tap behaves like the spoken answer with the chosen candidate
    /// (per-type play routing, confirm gates, resume/pagination precedence; the
    /// JF-690 multi-value ER state included, whose state list can outrank the
    /// spoken three-name cap). A findSongSelect tap becomes the digit pick the
    /// spoken flow's ResolvePick already understands and runs the FindSong
    /// Disambiguating leg. A stale document (state gone, ordinal outside the
    /// list) degrades to the empty keep-alive, the same convention as an unknown
    /// item id on the queue/carousel taps.
    /// ONE known divergence from the spoken answer, a platform limit not a
    /// handler defect: the progressive-response vehicle exists only for
    /// IntentRequest/LaunchRequest (the Amazon contract PlaybackLaunchBuilder's
    /// announce gate cites), so a VIDEO-type tap's now-playing announce rides
    /// the final response (the pre-JF-501 shape) instead of the progressive
    /// send the spoken confirm gets. The pre-JF-624 carousel tap had the same
    /// shape; no regression.
    /// </summary>
    private Task<SkillResponse> HandleDisambiguationTap(
        AplUserEventRequest aplEvent,
        string action,
        Dictionary<string, object>? sessionAttributes,
        Entities.User user,
        SessionInfo session,
        Context context,
        CancellationToken cancellationToken)
    {
        // The ordinal parse rides the shared tap-argument reader (JF-624 item 4
        // review F3): one definition of the invariant-culture tap-number contract
        // for every tap family in this file.
        if (!TryTapArg(aplEvent, 1, out int ordinal) || ordinal < 1)
        {
            Logger.LogDebug("AplUserEvent disambiguation tap: no valid ordinal (action={Action}, args={Args})", action, aplEvent.Arguments);
            return Task.FromResult(ResponseBuilder.Empty());
        }

        if (action == AplHelper.FindSongTapAction)
        {
            var findSongState = FindSongIntentHandler.ReadSessionData(sessionAttributes);
            if (findSongState?.State != FindSongState.Disambiguating
                || findSongState.Candidates is not { Count: > 0 }
                || ordinal > findSongState.Candidates.Count)
            {
                Logger.LogDebug("AplUserEvent FindSong tap: ordinal {Ordinal} outside the Disambiguating state (state={State}, candidates={Count})", ordinal, findSongState?.State.ToString() ?? "none", findSongState?.Candidates?.Count ?? 0);
                return Task.FromResult(ResponseBuilder.Empty());
            }

            // A tap IS the digit answer ("2" = the second candidate): the digit
            // form is ResolvePick's exact-match path, so the tap runs the same
            // gates and the same play tail as the spoken ordinal. The candidate
            // list is capped at four by construction; a hypothetical longer list
            // degrades to the invalid-pick elicit, never a wrong play. The
            // target-typed news avoid naming the Intent/Slot types, which this
            // file's own trailing namespace segment (Handler.Intent) would
            // shadow.
            var tapRequest = new IntentRequest
            {
                Intent = new()
                {
                    Name = IntentNames.FindSongIntent,
                    Slots = new Dictionary<string, Slot>
                    {
                        [IntentNames.Slots.TitleKeywords] = new()
                        {
                            Name = IntentNames.Slots.TitleKeywords,
                            Value = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }
                    }
                },
                Locale = aplEvent.Locale
            };

            Logger.LogDebug("AplUserEvent FindSong tap: ordinal {Ordinal} mapped onto the Disambiguating pick", ordinal);
            return _findSongHandler.HandleAsync(tapRequest, context, user, session, sessionAttributes, cancellationToken);
        }

        var disambiguation = DisambiguationHelper.ReadState(sessionAttributes);
        if (disambiguation == null || ordinal > disambiguation.Value.Matches.Count)
        {
            Logger.LogDebug("AplUserEvent disambiguation tap: ordinal {Ordinal} outside the disambiguation state (matches={Count})", ordinal, disambiguation?.Matches.Count ?? 0);
            return Task.FromResult(ResponseBuilder.Empty());
        }

        // The tap moves the yes/no cursor to the tapped row and answers "yes":
        // the state machine is the SAME one the spoken cycle drives (disambig_*
        // attributes), extended only by the cursor being set by the tap payload.
        sessionAttributes![DisambiguationHelper.AttrIndex] = ordinal - 1;
        Logger.LogDebug(
            "AplUserEvent disambiguation tap: ordinal {Ordinal} mapped onto the yes leg (mediaType={MediaType}, matches={Count})",
            ordinal, disambiguation.Value.MediaType, disambiguation.Value.Matches.Count);
        return _yesHandler.HandleAsync(aplEvent, context, user, session, sessionAttributes, cancellationToken);
    }

    /// <summary>
    /// Handle APL touch events: playback controls (prev/pause/next),
    /// list item selection (selectItem/playTrack), and carousel taps (carouselTap).
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        var aplEvent = (AplUserEventRequest)request;
        string? action = aplEvent.Arguments?.FirstOrDefault()?.ToString();

        switch (action)
        {
            case "prev":
                // JF-582: both taps route through the shared adjacent-item serve
                // (rehydration guard, medium refusal and codec gate applied
                // uniformly; rationale on the serve).
                return Task.FromResult(Progress.ServeAdjacentQueueItem(
                    _queueManager,
                    _libraryManager,
                    session,
                    context,
                    user,
                    GetLocale(request),
                    ProgressReporter.AdjacentQueueDirection.Previous,
                    "AplUserEvent previous",
                    tapOrigin: true));
            case "pause":
                return Task.FromResult(BuildPauseResponse());
            case "next":
                return Task.FromResult(Progress.ServeAdjacentQueueItem(
                    _queueManager,
                    _libraryManager,
                    session,
                    context,
                    user,
                    GetLocale(request),
                    ProgressReporter.AdjacentQueueDirection.Next,
                    "AplUserEvent next",
                    tapOrigin: true));
            case "selectItem":
            case "playTrack":
            case "carouselTap":
                return HandleSelectItem(aplEvent, user, session, context, request, cancellationToken);
            default:
                return Task.FromResult(ResponseBuilder.Empty());
        }
    }

    private async Task<SkillResponse> HandleSelectItem(AplUserEventRequest aplEvent, Entities.User user, SessionInfo session, Context context, Request request, CancellationToken cancellationToken)
    {
        // JF-625: set when the tapped folder is a MusicAlbum; drives the whole-album
        // concat stream in seek mode (same seek bar as the voice ask).
        Guid? albumFolderId = null;
        string? itemIdStr = aplEvent.Arguments?.ElementAtOrDefault(1)?.ToString();
        if (string.IsNullOrEmpty(itemIdStr) || !Guid.TryParse(itemIdStr, out Guid itemId))
        {
            Logger.LogDebug("AplUserEvent HandleSelectItem: no valid item ID in arguments");
            return ResponseBuilder.Empty();
        }

        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            Logger.LogDebug("AplUserEvent HandleSelectItem: item {ItemId} not found in library", itemIdStr);
            return ResponseBuilder.Empty();
        }

        Logger.LogDebug(
            "AplUserEvent HandleSelectItem: resolved item={ItemName} ({ItemId}), type={ItemType}",
            item.Name, itemIdStr, item.GetType().Name);

        if (item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            string locale = GetLocale(request);
            var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
            if (userError != null)
            {
                return userError;
            }

            // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
            SkillResponse movieResponse = Launch.BuildVideoAppLaunchResponse(
                context,
                locale,
                Launch.GetVideoAppLaunchUrl(item, user),
                item.Name,
                Launch.BuildVideoLaunchSpeech(item, locale, _userDataManager, jellyfinUser, Launch.GetAnnounceNowPlaying(user)));

            // JF-718: the writes follow the launch build and ride the delivered-launch
            // gate; the rationale lives on AttachNowPlayingIfLaunched. Practically
            // unreachable (an APL tap implies a rendered document, i.e. a screen),
            // so the gate is belt here.
            PlaybackLaunchBuilder.AttachNowPlayingIfLaunched(movieResponse, session, item);
            return movieResponse;
        }

        // JF-718: the queue each arm resolves rides the delivered-launch gate at the
        // shared attach below (the writes used to run before the launch build, so the
        // token-refusal throw left a phantom now-playing for a refused start).
        IEnumerable<QueueItem> launchQueue;

        // Folder items (audiobooks, music folders, etc.) need to be resolved to their
        // first audio child. Without this, Launch.GetStreamUrl() generates /Audio/{folderId}/stream
        // which fails because Folders don't have media sources.
        if (item is Folder folder)
        {
            if (Util.PodcastEpisodeResolver.IsSeriesShape(folder))
            {
                // JF-599: a tapped podcast Series (the IlPost carousel surface). The
                // generic ParentId+MediaTypes=Audio child query below returns ZERO
                // episodes for this shape (they are Episode items under season
                // folders, so the series is an ancestor and the media type is Video),
                // and the tap would answer FolderNoPlayableContent. The shared
                // resolver plays the newest episode instead. REACH: this keys on
                // ANY Series (no podcast discriminator exists; the IlPost library
                // is CollectionType=tvshows like a real TV library), so a tap on a
                // real TV show in a series carousel also plays its newest episode
                // audio-only (the pre-change behavior was the FolderNoPlayableContent
                // refusal; tapped Episode items already played audio-only).
                SkillResponse? podcastsDisabled = IfFeatureDisabled(c => c.PodcastsEnabled, request);
                if (podcastsDisabled != null)
                {
                    return podcastsDisabled;
                }

                string seriesLocale = GetLocale(request);
                var (seriesUser, seriesUserError) = ResolveJellyfinUser(_userManager, session.UserId, seriesLocale);
                if (seriesUserError != null)
                {
                    return seriesUserError;
                }

                // The shared tail (JF-605): retry-budgeted query, the podcast
                // empty answer, codec-routed launch, and the TAP's resume policy
                // via the offset delegate (an in-progress episode resumes; the
                // intent and yes paths play fresh).
                return await Util.PodcastEpisodeResolver.PlayLatestEpisodeAsync(
                    _libraryManager,
                    Launch,
                    Logger,
                    "AplPodcastEpisodes",
                    folder,
                    seriesUser!,
                    user,
                    session,
                    context,
                    seriesLocale,
                    offsetFor: episode => GetResumeOffset(episode, session, request),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Multi-disc albums play disc-then-track (JF-339 AC#3); other folders
                // (audiobook/artist folders) keep SortName (the shared chapter-order
                // constant, JF-672: one SortName definition so a future chapter-order
                // change reaches this tap path with the voice paths).
                bool isAlbum = folder is MediaBrowser.Controller.Entities.Audio.MusicAlbum;
                var childQuery = new InternalItemsQuery
                {
                    ParentId = folder.Id,
                    MediaTypes = new[] { MediaType.Audio },
                    Recursive = true,
                    Limit = 500,
                    OrderBy = isAlbum
                        ? QueueContinuationFetcher.AlbumTrackOrder
                        : QueueContinuationFetcher.AudiobookChapterOrder
                };

                var children = _libraryManager.GetItemList(childQuery);

                if (children.Count == 0)
                {
                    Logger.LogWarning("AplUserEvent HandleSelectItem: folder {FolderName} has no audio children", folder.Name);
                    string locale = GetLocale(request);
                    return ResponseBuilder.Tell(ResponseStrings.Get("FolderNoPlayableContent", locale));
                }

                item = children[0];
                itemIdStr = item.Id.ToString();

                Logger.LogDebug(
                    "AplUserEvent HandleSelectItem: resolved folder {FolderName} to first child {ChildName} ({ChildId})",
                    folder.Name, item.Name, itemIdStr);

                // Queue remaining children: the lazy Select rides the IEnumerable
                // overload so the up-to-500-entry queue materializes ONLY inside
                // the delivered-launch gate (the favorites/PlayRandom shape; the
                // throw path allocates nothing).
                if (isAlbum)
                {
                    albumFolderId = folder.Id;
                }

                launchQueue = children.Select(c => new QueueItem { Id = c.Id });
            }
        }
        else
        {
            launchQueue = new List<QueueItem> { new() { Id = item.Id } };
        }

        int offsetMs = GetResumeOffset(item, session, request);

        // JF-625: albumFolderId (set in the folder branch above) makes an album FOLDER
        // tap play the whole-album concat stream in seek mode, same seek bar as the
        // voice ask (the parallel-dispatch rule); null for every non-album tap.
        // Deliberate choice (review #10): the tap starts the album from its BEGINNING
        // (collectionStartTicks stays 0) while the voice ask resumes mid-album. A tap
        // is a fresh visual pick; the voice "play album" is a continuation. If this
        // reads wrong on the device, thread the resolved child's prefix sum here.

        // The codec-routed audio source (JF-507): a resolved Episode whose audio
        // codec has no Echo decoder rides the audio-only transcode; every other
        // resolved item keeps the static URL GetStreamUrl built.
        AudioLaunchSource source = Launch.ResolveAudioLaunchSource(item, itemIdStr, user, offsetMs);
        // The NowPlaying screen rides the BuildAudioPlayerResponse chokepoint since
        // JF-623 (every ReplaceAll play auto-attaches); the manual attach here was the
        // carousel-only leftover and would double-render.
        SkillResponse response = Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source, itemIdStr, item, user, context, collectionParentId: albumFolderId, libraryManager: _libraryManager);

        // JF-718: the writes follow the launch build and ride the delivered-launch
        // gate; the rationale lives on AttachNowPlayingIfLaunched
        // (BuildAudioPlayerResponse is throw-or-launch today, so the gate is belt).
        PlaybackLaunchBuilder.AttachNowPlayingIfLaunched(response, session, launchQueue, item);
        return response;
    }

    private int GetResumeOffset(BaseItem item, SessionInfo session, Request request)
    {
        // 1. Check plugin's per-item state first (bypasses Jellyfin's min-resume
        // thresholds). JF-860: through the manager's LOCKED read seam
        // (GetStoredPositionTicks) instead of a raw map read, so an unlocked
        // TryGetValue cannot race the locked write/trim mid-resize (the
        // JF-425/JF-447/JF-738 class the seam's doc names); it also skips the
        // queue-creating side effect the old GetOrCreateQueue tap had.
        long? cachedTicks = _queueManager.GetStoredPositionTicks(session.DeviceId, item.Id.ToString());
        if (cachedTicks is > 0)
        {
            int offsetMs = (int)TimeSpan.FromTicks(cachedTicks.Value).TotalMilliseconds;
            Logger.LogInformation(
                "APL tap: resuming {ItemName} from {OffsetMs}ms (ItemPositionState)",
                item.Name, offsetMs);
            return offsetMs;
        }

        // 2. Fall back to Jellyfin's UserData
        var (jellyfinUser, _) = ResolveJellyfinUser(_userManager, session.UserId, GetLocale(request));
        if (jellyfinUser == null)
        {
            Logger.LogWarning("APL tap: could not resolve Jellyfin user {UserId} for resume offset check", session.UserId);
            return 0;
        }

        UserItemData? data = _userDataManager.GetUserData(jellyfinUser, item);
        if (data?.PlaybackPositionTicks > 0 && !data.Played)
        {
            int offsetMs = (int)TimeSpan.FromTicks(data.PlaybackPositionTicks).TotalMilliseconds;
            Logger.LogInformation(
                "APL tap: resuming {ItemName} from {OffsetMs}ms (UserData)",
                item.Name, offsetMs);
            return offsetMs;
        }

        return 0;
    }
}
