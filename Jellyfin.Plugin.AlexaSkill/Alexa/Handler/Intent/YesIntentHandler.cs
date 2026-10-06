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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Microsoft.Extensions.Logging;
using VideoAppDirective = Jellyfin.Plugin.AlexaSkill.Alexa.Directive;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handles AMAZON.YesIntent during search disambiguation.
/// Plays the current match from the disambiguation state.
/// </summary>
public class YesIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="YesIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface (JF-795: the book confirm leg's resume axis, consumed by the shared resolved-book play flow).</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager (JF-514/JF-522: the launch-scope store behind the resume offset rebase and the directive-time base recording; JF-795: also the confirmed book's device-queue write and ItemPositionState resume tier).</param>
    public YesIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is IntentRequest intentRequest
            && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonYes, StringComparison.Ordinal);
    }

    /// <summary>
    /// Handle without session attributes - no disambiguation in progress.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        Logger.LogDebug("Yes: no session attributes, responding with unexpected-yes");
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", GetLocale(request))));
    }

    /// <summary>
    /// Handle with session attributes - resolve resume confirmation, pagination, or disambiguation.
    /// Resume confirmation takes priority over pagination, which takes priority over disambiguation.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="sessionAttributes">The session attributes containing disambiguation, pagination, or resume state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // Check for resume confirmation first
        var resumeState = ResumeHelper.ReadState(sessionAttributes);
        if (resumeState != null)
        {
            Logger.LogDebug("Yes: confirming resume, itemId={ItemId}, offsetMs={OffsetMs}", resumeState.ItemId, resumeState.OffsetMs);
            return HandleResumeConfirmation(resumeState, user, session, context, locale);
        }

        // Check for active pagination state - "yes" acts as "show more"
        var paginationState = ListPaginationHelper.ReadState(sessionAttributes);
        if (paginationState != null)
        {
            Logger.LogDebug("Yes: continuing pagination type={ListType}", paginationState.Type);
            return HandlePaginationContinuation(paginationState, context, user, locale);
        }

        var state = DisambiguationHelper.ReadState(sessionAttributes);
        if (state == null)
        {
            Logger.LogDebug("Yes: no disambiguation state, responding with unexpected-yes");
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", locale)));
        }

        var (matches, index, mediaType) = state.Value;
        if (index < 0 || index >= matches.Count)
        {
            Logger.LogDebug("Yes: disambiguation index {Index} out of range (count={MatchCount})", index, matches.Count);
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", locale)));
        }

        Logger.LogDebug("Yes: confirming disambiguation, mediaType={MediaType}, index={Index}, matchCount={MatchCount}", mediaType, index, matches.Count);

        if (!Guid.TryParse(matches[index].Id, out Guid itemId))
        {
            Logger.LogWarning("Invalid GUID format in disambiguation state: {Id}", matches[index].Id);
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        BaseItem? item = _libraryManager.GetItemById(itemId);
        if (item == null)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return Task.FromResult<SkillResponse>(userError);
        }

        // JF-361: AudioBook items arrive with MediaTypeAlbum label (PlayBook disambiguation reuses
        // it). Route to the audiobook playback path instead of PlayAlbum. JF-793 code-review F1:
        // the payload can also be the book FOLDER itself (the candidate normalization emits
        // folder ids for multi-chapter books), and a Folder is not an AudioBook, so the bare
        // IsAudioBook gate misrouted those confirms into the PlayAlbum arm (unpaged whole-book
        // queue, no resume, no device queue, plain AudioPlayer under NativeControlsForBooks);
        // IsBookDisambiguationPayload covers both payload shapes while MusicAlbums stay on the
        // album leg. JF-795: the confirm rides the head's ONE resolved-book play flow (the
        // PodcastEpisodeResolver precedent), so the queue completeness (the JF-673/JF-674
        // continuation mint and the device queue) and the resume decision (page scan plus the
        // JF-793 finding-4 deep-resume re-slice) cannot drift from the direct ask; this
        // delegation returns the shared flow's task directly (the announcing launch builders
        // inside it are async), the sync play paths keep the Task.FromResult shape.
        if (mediaType == DisambiguationHelper.MediaTypeAlbum && AudiobookItems.IsBookDisambiguationPayload(item))
        {
            // JF-611 podcast-leg shape (adopted by the JF-795 /simplify round): the
            // feature flag gates here too, so a book prompt opened before an admin
            // disabled books cannot launch through the confirm (the confirm-must-
            // match-ask rule extends to the disabled answer the direct ask gives).
            SkillResponse? booksDisabled = IfFeatureDisabled(c => c.BooksEnabled, request);
            if (booksDisabled != null)
            {
                return Task.FromResult(booksDisabled);
            }

            Logger.LogDebug("Yes: routing AudioBook item {ItemId} to audiobook playback", itemId);
            return AudiobookPlayResolver.PlayBookAsync(
                _libraryManager, Launch, Logger, "Yes",
                item, spokenBookName: null, jellyfinUser!, user, session, context, request, locale,
                _userDataManager, _queueManager, cancellationToken);
        }

        // JF-805: the MusicAlbum confirm rides the head's ONE album play flow
        // (AlbumPlayService.BuildAlbumPlayResponseAsync, the JF-795 book pattern /
        // PodcastEpisodeResolver precedent chain), so the confirm inherits the
        // initial page, the resume scan (in-page and the JF-796 deep resume),
        // the JF-625 tracker override, the continuation mint, and the
        // device-queue write from the ONE composition and cannot drift from the
        // direct ask. The composition re-fetches the track page (the books'
        // JF-795 answer: the routing branch rides the head's own fetch flow; the
        // disambiguation state carries only id/name pairs, so nothing
        // prefetched is wasted). The music gate keeps the disabled answer
        // identical to the direct ask's (the JF-611/JF-795 shape). The JF-803
        // divergence axes live INSIDE the composition and ride along.
        if (mediaType == DisambiguationHelper.MediaTypeAlbum
            && item is MediaBrowser.Controller.Entities.Audio.MusicAlbum)
        {
            SkillResponse? musicDisabled = IfMediaTypeDisabled(c => c.MusicEnabled, request);
            if (musicDisabled != null)
            {
                return Task.FromResult(musicDisabled);
            }

            Logger.LogDebug("Yes: routing MusicAlbum item {ItemId} to the shared album play flow", itemId);
            return AlbumPlay.BuildAlbumPlayResponseAsync(
                item,
                jellyfinUser!,
                user,
                session,
                context,
                locale,
                _libraryManager,
                _userDataManager,
                _queueManager,
                "Yes",
                announcement: null,
                request: request,
                cancellationToken);
        }

        if (mediaType == DisambiguationHelper.MediaTypeVideo)
        {
            return PlayVideo(item, user, session, locale, context, request);
        }

        // JF-599/JF-605: the podcast disambiguation confirm rides the same
        // shared resolve-to-launch tail as the intent handler, so "yes" can never
        // dead-end a multi-match podcast prompt. Fresh play, offset 0.
        // JF-611 review: the feature flag gates here too (a prompt opened before
        // an admin disabled podcasts must not launch through the confirm).
        if (mediaType == DisambiguationHelper.MediaTypePodcast)
        {
            SkillResponse? podcastsDisabled = IfFeatureDisabled(c => c.PodcastsEnabled, request);
            if (podcastsDisabled != null)
            {
                return Task.FromResult(podcastsDisabled);
            }

            return Util.PodcastEpisodeResolver.PlayLatestEpisodeAsync(
                _libraryManager, Launch, Logger, "YesPodcastEpisodes", item, jellyfinUser!, user, session, context, locale,
                cancellationToken: cancellationToken);
        }

        SkillResponse response = mediaType switch
        {
            DisambiguationHelper.MediaTypeSong => PlaySong(item, user, session, context, locale),
            DisambiguationHelper.MediaTypeAlbum => PlayAlbum(item, jellyfinUser!, user, session, locale, context),
            DisambiguationHelper.MediaTypeArtist => PlayArtist(item, jellyfinUser!, user, session, locale, context),
            DisambiguationHelper.MediaTypePlaylist => PlayPlaylist(item, jellyfinUser!, user, session, locale, context),
            _ => ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale))
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// Handle pagination continuation: "yes" acts as "show more" when pagination state is active.
    /// </summary>
    private Task<SkillResponse> HandlePaginationContinuation(
        ListPaginationHelper.PaginationState paginationState,
        Context context,
        Entities.User user,
        string locale)
    {
        return Task.FromResult(ListPaginationHelper.BuildNextPageResponse(_libraryManager, paginationState, locale));
    }

    /// <summary>
    /// Handle resume confirmation: play the stored item from the stored offset.
    /// </summary>
    private Task<SkillResponse> HandleResumeConfirmation(
        ResumeHelper.ResumeState resumeState,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale)
    {
        BaseItem? item = null;
        if (Guid.TryParse(resumeState.ItemId, out Guid itemGuid))
        {
            item = _libraryManager.GetItemById(itemGuid);
        }

        if (item == null)
        {
            Logger.LogWarning("ResumeConfirmation: could not find item {ItemId}", resumeState.ItemId);
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale)));
        }

        string itemId = item.Id.ToString();
        // JF-693: the now-playing write and the resume announces below ride a
        // DELIVERED launch (the builder can answer the JF-687 empty-secret refusal
        // Tell instead of a directive; the refusal must not leave a phantom
        // now-playing behind nor be spoken over).

        // Audiobook resume via VideoApp resume playlist (keeps the seek bar). The position
        // slices the playlist via ?start= (ExoPlayer ignores #EXT-X-START); VideoApp.Launch has no offset param.
        if (resumeState.UseResumePlaylist)
        {
            long offeredTicks = TimeSpan.FromMilliseconds(Math.Min(resumeState.OffsetMs, int.MaxValue)).Ticks;
            string bookKey = AudiobookItems.ResolveTrackedBookKey(item, _libraryManager);
            // Tracker cleared between offer and confirm: fall back to the offered offset.
            long startTicks = ResumeMath.GetAudiobookStartTicks(bookKey, offeredTicks);

            // JF-699 item 1: the builder either threw the StreamTokenNotConfigured
            // refusal (RequestPipeline answers it; nothing below runs) or delivered
            // the launch, so the JF-693 verdict wrapper is gone and the state/announce
            // writes simply follow the launch.
            SkillResponse response = Launch.BuildAudiobookResumeResponse(item, startTicks, user, context, _libraryManager);

            session.FullNowPlayingItem = item;
            PlaybackLaunchBuilder.AttachAnnounceIfLaunched(
                response,
                _config.ResumeAnnounceTitle
                    ? SpeechBuilder.BuildOutputSpeech("ResumingSsml", "Resuming", locale, item.Name ?? ResponseStrings.Get("UnknownMedia", locale))
                    : SpeechBuilder.BuildOutputSpeech("ResumeBriefSsml", "ResumeBrief", locale));
            return Task.FromResult(response);
        }

        int offsetMs = (int)Math.Min(resumeState.OffsetMs, int.MaxValue);
        string? deviceId = context?.System?.Device?.DeviceID;

        // JF-514/JF-520/JF-522 provenance gate, shared with the ResumeIntent tail via
        // PlaybackLaunchBuilder.ResolveResumedAudioLaunch: the flag tells the helper which
        // timeline the offset counts (true = device-derived/stream-relative ->
        // rebase against the launch-scoped base, or drop to restart when none).
        // The ONLY seed that still flags stream-relative is the AudioPlayer-context
        // seed (Amazon wrote the offset; stream-relative by platform contract);
        // the device-last-played seed stopped flagging in JF-522 because the event
        // writers now persist item-absolute positions. Seed-binding (rebasing at the
        // offer seed and deleting the flag) stays REJECTED: the flag's remaining
        // consumer needs confirm-time base reads, and offers can outlive plugin
        // restarts that change what the base store holds.
        //
        // JF-507: the helper's resolve is the shared codec-gated audio-launch
        // decision. An EAC3-family video item resumed on the audio path (the
        // 2026-09-06 corr=f0240020 Dot incident: raw static audio died at 1ms)
        // routes to the audio-only episode HLS transcode with the offset baked
        // into the URL (?start=) and directive offset 0.
        AudioLaunchSource source = Launch.ResolveResumedAudioLaunch(
            item, itemId, user, offsetMs, resumeState.OffsetIsStreamRelative, deviceId, _queueManager, "ResumeConfirmation");
        SkillResponse standardResponse = Launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            source,
            itemId,
            item,
            user,
            context,
            queueManager: _queueManager,
            libraryManager: _libraryManager);

        // JF-699 item 1: throw-or-launch (the JF-507 transcode-routed source can be
        // token-gated; a refusal throws and RequestPipeline answers it, so nothing
        // below runs on it) - the state write and resume announce simply follow the
        // delivered launch.
        session.FullNowPlayingItem = item;

        // Replace default speech with resume announcement
        PlaybackLaunchBuilder.AttachAnnounceIfLaunched(
            standardResponse,
            _config.ResumeAnnounceTitle
                ? SpeechBuilder.BuildOutputSpeech(
                    "ResumingSsml", "Resuming", locale, item.Name ?? ResponseStrings.Get("UnknownMedia", locale))
                : SpeechBuilder.BuildOutputSpeech("ResumeBriefSsml", "ResumeBrief", locale));

        return Task.FromResult(standardResponse);
    }

    private SkillResponse PlaySong(BaseItem song, Entities.User user, SessionInfo session, Context context, string locale)
    {
        // JF-440: the ONE single-song play shape (adds the stale-continuation clear
        // the other sites already had).
        return CrossMedia.BuildSingleSongResponse(song, user, session, context, locale);
    }

    private SkillResponse PlayAlbum(BaseItem album, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale, Context? context)
    {
        // JF-805: the MusicAlbum confirm routes through the ONE album play flow
        // (AlbumPlayService.BuildAlbumPlayResponseAsync) ABOVE, so this method now
        // serves the DEFENSIVE payloads only, and no live producer reaches it:
        // book payloads (AudioBook items and non-MusicAlbum FOLDERS,
        // IsBookDisambiguationPayload) route to PlayBookAsync, MusicAlbums route
        // to the shared composition, and PlayAlbum's own disambiguation matches
        // are MusicAlbum-only. What remains below is kept verbatim as the
        // belt-and-braces enumeration for a FUTURE DIRECT CALLER (a new call
        // site handing this method a MusicAlbum, or the routing intercept
        // above being removed; the JF-361/JF-672 defensive-leg convention):
        // the JF-767 Finding A MusicAlbum enumeration through the album
        // builder's JF-338 retry triple, the chapters sibling with its
        // MediaTypes=Audio axis (the JF-361 kind discipline
        // IncludeItemTypes=Audio would drop), and the single-file fallback;
        // never a private initializer with the refuted AlbumTrackOrder
        // composite.
        bool isMusicAlbum = album is MediaBrowser.Controller.Entities.Audio.MusicAlbum;
        var tracksQuery = isMusicAlbum
            ? QueueContinuationFetcher.BuildScopedAlbumTracksQueryUnpaged(
                jellyfinUser, user, _libraryManager, Logger, album.Id, byAlbumIds: false)
            : QueueContinuationFetcher.BuildScopedAudiobookChaptersQueryUnpaged(
                jellyfinUser, user, _libraryManager, Logger, album.Id);

        IReadOnlyList<BaseItem> albumItems = _libraryManager.GetItemList(tracksQuery);

        // JF-338 retry, MusicAlbum only (mirrors AlbumPlayService's head fallback): for
        // split / multi-disc / malformed-folder albums the folder-based ParentId query
        // returns 0 even when the tracks exist (the track's Album metadata still links
        // them), so a confirmed disambiguation would answer NoSongsInAlbum while the direct
        // ask plays. Query by album membership instead.
        if (albumItems.Count == 0 && isMusicAlbum)
        {
            albumItems = _libraryManager.GetItemList(
                QueueContinuationFetcher.BuildScopedAlbumTracksQueryUnpaged(
                    jellyfinUser, user, _libraryManager, Logger, album.Id, byAlbumIds: true));
        }

        // JF-361: single-file audiobooks (AudioBook items that ARE the audio track, with no
        // child chapters) would answer "NoSongsInAlbum" here, so fall back to treating the
        // item as its own single track, matching PlayBookIntentHandler's single-file logic.
        // DEFENSIVE-ONLY today (JF-672 gate-marker correction): an AudioBook confirm routes
        // to PlayBook() above before PlayAlbum, so nothing reaches this fallback with a
        // book; it guards a future producer (or the JF-791 folder resolution) that hands
        // this path a single-file book.
        if (albumItems.Count == 0 && album is MediaBrowser.Controller.Entities.IHasMediaSources)
        {
            albumItems = new List<BaseItem> { album };
        }

        if (albumItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album.Name));
        }

        List<QueueItem> queueItems = albumItems.Select(i => new QueueItem { Id = i.Id }).ToList();
        string itemId = albumItems[0].Id.ToString();
        // JF-625: a confirmed MUSIC album plays the whole-album concat stream in seek
        // mode, same as a direct PlayAlbum request (the parallel-dispatch rule; a
        // confirm must not produce a different seek bar than the original ask).
        // MusicAlbum only: the JF-361 single-file audiobooks that also arrive here
        // have a non-album ParentId and must not build a collection URL.
        Guid? collectionParent = album is MediaBrowser.Controller.Entities.Audio.MusicAlbum ? album.Id : null;
        // JF-699 item 5 (code-review F2): launch build BEFORE the session writes; the
        // MusicAlbum leg mints the token-gated concat URL, so a JF-687 refusal must
        // not leave a phantom queue/now-playing behind (AlbumPlayService's ordering).
        SkillResponse response = Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, albumItems[0], user, context, collectionParentId: collectionParent);
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = albumItems[0];
        return response;
    }

    private SkillResponse PlayArtist(BaseItem artist, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale, Context? context)
    {
        var artistQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            // JF-358: MediaTypes does not constrain an ArtistIds query (entire audio
            // library on 10.11.x, zero rows at offset on 12.x); use IncludeItemTypes.
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            // JF-690 (code-review finding): align the confirm leg with the direct
            // play paths (PlayArtistSongsIntentHandler, CrossMediaFallback), which
            // all order by popularity: an unsorted query started the confirm on an
            // arbitrary DB-order track. Deliberately NO Limit: this confirm plays
            // the artist's WHOLE catalog as the queue (the direct paths page via
            // QueueContinuationStore, which this confirm leg does not wire).
            OrderBy = CrossMediaFallback.PopularitySort,
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artist.Id }
        };
        ApplyLibraryFilter(artistQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> artistItems = _libraryManager.GetItemList(artistQuery);

        if (artistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, artist.Name));
        }

        List<QueueItem> queueItems = artistItems.Select(i => new QueueItem { Id = i.Id }).ToList();
        string itemId = artistItems[0].Id.ToString();

        // JF-699 item 5: launch build BEFORE the now-playing writes (the ordering
        // policy lives on EnsureStreamTokenDeliverable).
        SkillResponse response = Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, artistItems[0], user, context, libraryManager: _libraryManager);
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = artistItems[0];
        return response;
    }

    private async Task<SkillResponse> PlayVideo(BaseItem video, Entities.User user, SessionInfo session, string locale, Context context, Request request)
    {
        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        // JF-501: the announce is spoken progressively (directive-only final response).
        // JF-587: the episode screenless degrade (audio-only on Dots); movies keep
        // the capability refusal inside the builder. Fresh play: no resume ticks.
        // JF-699 item 5: the session writes follow the launch build (the ordering
        // policy lives on EnsureStreamTokenDeliverable; the codec-routed source is
        // token-gated).
        SkillResponse response = await Launch.BuildEpisodeLaunchResponseAsync(
            context,
            request,
            locale,
            video,
            user,
            Launch.GetVideoAppLaunchUrl(video, user),
            resumeTicks: 0,
            SpeechBuilder.BuildNowPlayingSpeech(video.Name, locale, Launch.GetAnnounceNowPlaying(user))).ConfigureAwait(false);
        // JF-714/JF-718: the video confirm arm's now-playing writes ride a DELIVERED
        // launch; the rationale lives on AttachNowPlayingIfLaunched.
        PlaybackLaunchBuilder.AttachNowPlayingIfLaunched(response, session, video);
        return response;
    }

    private SkillResponse PlayPlaylist(BaseItem playlist, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale, Context? context)
    {
        IReadOnlyList<BaseItem> playlistItems = ((Folder)playlist).GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
        });

        if (playlistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistEmpty", locale));
        }

        List<QueueItem> queueItems = playlistItems.Select(i => new QueueItem { Id = i.Id, PlaylistItemId = playlist.Id.ToString() }).ToList();
        string itemId = playlistItems[0].Id.ToString();

        // JF-699 item 5: launch build BEFORE the now-playing writes (the ordering
        // policy lives on EnsureStreamTokenDeliverable).
        SkillResponse response = Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, playlistItems[0], user, context, libraryManager: _libraryManager);
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = playlistItems[0];
        return response;
    }
}
