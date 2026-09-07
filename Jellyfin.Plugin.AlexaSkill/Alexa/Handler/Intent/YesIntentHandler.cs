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
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="YesIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager (JF-514: the transcode-base ledger behind the resume offset rebase).</param>
    public YesIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
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
        // it). Route to the audiobook playback path instead of PlayAlbum.
        SkillResponse response;
        if (mediaType == DisambiguationHelper.MediaTypeAlbum && item is AudioBook)
        {
            Logger.LogDebug("Yes: routing AudioBook item {ItemId} to audiobook playback", itemId);
            response = PlayBook(item, jellyfinUser!, user, session, locale, context);
        }
        else
        {
            response = mediaType switch
            {
                DisambiguationHelper.MediaTypeSong => PlaySong(item, user, session, context, locale),
                DisambiguationHelper.MediaTypeAlbum => PlayAlbum(item, jellyfinUser!, user, session, locale, context),
                DisambiguationHelper.MediaTypeArtist => PlayArtist(item, jellyfinUser!, user, session, locale, context),
                DisambiguationHelper.MediaTypeVideo => PlayVideo(item, user, session, locale, context),
                DisambiguationHelper.MediaTypePlaylist => PlayPlaylist(item, jellyfinUser!, user, session, locale, context),
                _ => ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale))
            };
        }

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
        session.FullNowPlayingItem = item;

        // Audiobook resume via VideoApp resume playlist (keeps the seek bar). The position
        // is encoded in the playlist via #EXT-X-START; VideoApp.Launch has no offset param.
        if (resumeState.UseResumePlaylist)
        {
            string bookId = (item.ParentId != Guid.Empty ? item.ParentId : item.Id).ToString("N");
            long startTicks = Plugin.Instance?.AudiobookPositionTracker?.GetPositionTicks(bookId) ?? 0;
            if (startTicks <= 0)
            {
                // Tracker cleared between offer and confirm — fall back to the offered offset.
                startTicks = TimeSpan.FromMilliseconds(Math.Min(resumeState.OffsetMs, int.MaxValue)).Ticks;
            }

            SkillResponse response = BuildAudiobookResumeResponse(item, startTicks, user, context);
            response.Response.OutputSpeech = _config.ResumeAnnounceTitle
                ? BuildOutputSpeech("ResumingSsml", "Resuming", locale, item.Name ?? ResponseStrings.Get("UnknownMedia", locale))
                : BuildOutputSpeech("ResumeBriefSsml", "ResumeBrief", locale);
            return Task.FromResult(response);
        }

        int offsetMs = (int)Math.Min(resumeState.OffsetMs, int.MaxValue);
        string? deviceId = context?.System?.Device?.DeviceID;

        // Provenance gate (JF-514, the offer-path twin of ResumeIntentHandler's),
        // BEFORE any resolve: the offer was seeded from the AudioPlayer CONTEXT offset,
        // which is device-derived and therefore relative to the previous playback's
        // OUTPUT timeline. For a transcode-routed item that timeline starts at the
        // stream's ?start= base, so minting the raw offset seeks the wrong position (an
        // episode started at absolute 20:00 and paused at stream 5:00 would silently
        // skip BACK 15 minutes). The launch that minted that base recorded it in the
        // device queue ledger (BaseHandler.ResolveAudioLaunchSource), so rebase:
        // ?start = base + offset, both terms item-absolute. The probe-and-read must run
        // BEFORE the resolve because the resolve WRITES the ledger; no recorded base
        // (pre-deploy launch, ledger wiped, device unknown) falls back to the JF-507
        // interim rule: drop the offset and restart, never mint a stream-relative value
        // silently. Seeds with OffsetIsStreamRelative=false (server progress, device
        // last-played) BYPASS this gate by design today, but their positions are NOT
        // guaranteed item-absolute either: for a transcode-routed item the device event
        // writers persist the raw stream-relative offset into server progress (see
        // ResumeIntentHandler's class doc). That residual hole (a flag=false offer can
        // still mint a stream-relative ?start) is tracked as JF-520.
        int effectiveOffsetMs = offsetMs;
        if (RoutesToAudioTranscode(item) && offsetMs > 0 && resumeState.OffsetIsStreamRelative)
        {
            long? transcodeBaseMs = GetAudioTranscodeBase(deviceId, itemId, _queueManager);
            if (transcodeBaseMs.HasValue)
            {
                effectiveOffsetMs = (int)Math.Min(transcodeBaseMs.Value + offsetMs, int.MaxValue);
                Logger.LogInformation(
                    "ResumeConfirmation: item {ItemId} routes to the audio-only transcode and the offered offset ({OffsetMs}ms) is device-derived (stream-relative); recorded launch base {BaseMs}ms, minting ?start={StartMs}ms (item-absolute)",
                    itemId, offsetMs, transcodeBaseMs.Value, effectiveOffsetMs);
            }
            else
            {
                effectiveOffsetMs = 0;
                Logger.LogInformation(
                    "ResumeConfirmation: item {ItemId} routes to the audio-only transcode but the offered offset ({OffsetMs}ms) is device-derived (stream-relative) and no launch base is recorded for device {DeviceId}; dropping it so playback restarts instead of minting a false ?start=",
                    itemId, offsetMs, deviceId);
            }
        }

        // JF-507: the shared codec-gated audio-launch decision. An EAC3-family video
        // item resumed on the audio path (the 2026-09-06 corr=f0240020 Dot incident:
        // raw static audio died at 1ms) routes to the audio-only episode HLS transcode
        // with the offset baked into the URL (?start=) and directive offset 0.
        AudioLaunchSource source = ResolveAudioLaunchSource(item, itemId, user, effectiveOffsetMs, deviceId, _queueManager);
        SkillResponse standardResponse = BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll,
            source.Url,
            itemId,
            item,
            user,
            context,
            source.OffsetMs);

        // Replace default speech with resume announcement
        if (_config.ResumeAnnounceTitle)
        {
            string title = item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
            standardResponse.Response.OutputSpeech = BuildOutputSpeech("ResumingSsml", "Resuming", locale, title);
        }
        else
        {
            standardResponse.Response.OutputSpeech = BuildOutputSpeech("ResumeBriefSsml", "ResumeBrief", locale);
        }

        return Task.FromResult(standardResponse);
    }

    private SkillResponse PlaySong(BaseItem song, Entities.User user, SessionInfo session, Context context, string locale)
    {
        // JF-440: the ONE single-song play shape (adds the stale-continuation clear
        // the other sites already had).
        return BuildSingleSongResponse(song, user, session, context, locale);
    }

    private SkillResponse PlayAlbum(BaseItem album, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale, Context? context)
    {
        IReadOnlyList<BaseItem> albumItems = _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = album.Id,
            // MediaTypes (not IncludeItemTypes=Audio): PlayBook disambiguation reuses the
            // MediaTypeAlbum label, so this path also receives AudioBook items whose chapter
            // children are BaseItemKind.AudioBook — IncludeItemTypes=Audio would drop them.
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            OrderBy = QueueContinuationFetcher.AlbumTrackOrder,
        });

        // JF-361: single-file audiobooks (AudioBook items that ARE the audio track, with no
        // child chapters) arrive here via PlayBook disambiguation's MediaTypeAlbum label.
        // PlayAlbum finds no children and would say "NoSongsInAlbum" — but the item itself is
        // playable audio. Fall back to treating the item as its own single track, matching
        // PlayBookIntentHandler's single-file logic.
        if (albumItems.Count == 0 && album is MediaBrowser.Controller.Entities.IHasMediaSources)
        {
            albumItems = new List<BaseItem> { album };
        }

        if (albumItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album.Name));
        }

        List<QueueItem> queueItems = albumItems.Select(i => new QueueItem { Id = i.Id }).ToList();
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = albumItems[0];
        string itemId = albumItems[0].Id.ToString();
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, albumItems[0], user, context);
    }

    /// <summary>
    /// Play an AudioBook item after disambiguation confirmation. Mirrors PlayBookIntentHandler's
    /// single-match logic: resolve tracks, check resume, route to VideoApp (NativeControlsForBooks)
    /// or AudioPlayer.
    /// </summary>
    private SkillResponse PlayBook(BaseItem book, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale, Context context)
    {
        // Resolve tracks (same logic as PlayBookIntentHandler)
        // Resolve tracks (same logic as PlayBookIntentHandler). Use GetItemList (not GetItemsResult)
        // to avoid the EF Core Count() NRE on certain query combinations.
        IReadOnlyList<BaseItem> bookTrackList = _libraryManager.GetItemList(new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = book.Id,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            Limit = ProgressiveQueueConstants.GetInitialFetchSize()
        });

        List<BaseItem> trackItems;
        if (bookTrackList.Count == 0)
        {
            if (book.MediaType == MediaType.Audio)
            {
                trackItems = new List<BaseItem> { book };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NoContentInBook", locale, book.Name));
            }
        }
        else
        {
            trackItems = bookTrackList.ToList();
        }

        session.NowPlayingQueue = trackItems.Select(t => new QueueItem { Id = t.Id }).ToList();
        session.FullNowPlayingItem = trackItems[0];

        string itemId = trackItems[0].Id.ToString();

        // NativeControlsForBooks → VideoApp HLS concat (seek bar)
        if (Plugin.Instance?.Configuration?.NativeControlsForBooks == true)
        {
            SkillResponse response = BuildVideoAppAudioResponse(itemId, trackItems[0], user, locale, context);
            // Fresh-start audiobook via VideoApp: announce the book title.
            response.Response.OutputSpeech = BuildNowPlayingSpeech(book.Name, locale, GetAnnounceNowPlaying(user));
            return response;
        }

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, trackItems[0], user, context);
    }

    private SkillResponse PlayArtist(BaseItem artist, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, SessionInfo session, string locale, Context? context)
    {
        var artistQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
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
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = artistItems[0];
        string itemId = artistItems[0].Id.ToString();
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, artistItems[0], user, context);
    }

    private SkillResponse PlayVideo(BaseItem video, Entities.User user, SessionInfo session, string locale, Context context)
    {
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = video.Id } };
        session.FullNowPlayingItem = video;

        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        return BuildVideoAppLaunchResponse(
            context,
            locale,
            GetVideoAppLaunchUrl(video, user),
            video.Name,
            BuildNowPlayingSpeech(video.Name, locale, GetAnnounceNowPlaying(user)));
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
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = playlistItems[0];
        string itemId = playlistItems[0].Id.ToString();
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, playlistItems[0], user, context);
    }
}
