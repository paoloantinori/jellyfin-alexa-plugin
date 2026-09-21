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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayNextIntent requests.
/// Inserts a song immediately after the currently playing track, in BOTH stores
/// since JF-578 (the persisted device queue and the session queue, through the
/// shared queue-membership writer on <see cref="ProgressReporter"/>).
/// </summary>
public class PlayNextIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ISongNgramIndex? _songNgramIndex;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayNextIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="songNgramIndex">Optional in-memory song index (warming gate proxy for the cold song query).</param>
    /// <param name="queueManager">Optional per-device queue manager (the JF-578 both-stores queue writer).</param>
    public PlayNextIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        ISongNgramIndex? songNgramIndex = null,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _artistIndex = artistIndex;
        _songNgramIndex = songNgramIndex;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayNext, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.QueueManagementEnabled, request) is { } disabled)
        {
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? songQuery = intentRequest.Intent.Slots?["song"]?.Value;
        string? musicianQuery = intentRequest.Intent.Slots?["musician"]?.Value;

        Logger.LogDebug("PlayNext: entered, song={SongQuery}, musician={MusicianQuery}", songQuery, musicianQuery);

        // JF-550 (dead-mic sweep; JF-549 class).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "PlayNext") is { } elicitCancel)
        {
            return elicitCancel;
        }

        if (string.IsNullOrWhiteSpace(songQuery))
        {
            return BuildDialogElicitResponse("DidNotCatchQueueItem", locale, "song", IntentNames.PlayNext, Util.ElicitSlots.For(IntentNames.PlayNext));
        }

        // Per-path routing (gate = GuardIndexReady): the song gate first (the
        // unbounded Audio SearchTerm scan), then the artist gate when a musician
        // is named (targeted path).
        GuardIndexReady(_songNgramIndex);
        if (!string.IsNullOrWhiteSpace(musicianQuery))
        {
            GuardIndexReady(_artistIndex);
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistIds = new();
        string? matchedArtistName = null;
        if (!string.IsNullOrWhiteSpace(musicianQuery))
        {
            IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
                musicianQuery, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtistsForPlayNext", ct),
                locale, cancellationToken).ConfigureAwait(false);

            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByArtist", locale, musicianQuery));
            }

            matchedArtistName = artists[0].Name;
            foreach (BaseItem artist in artists)
            {
                artistIds.Add(artist.Id);
            }
        }

        var songSearchQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = songQuery,
            ArtistIds = artistIds.ToArray(),
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(songSearchQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> songs = await RetryAsync(
            () => _libraryManager.GetItemList(songSearchQuery),
            "GetSongsForPlayNext",
            cancellationToken).ConfigureAwait(false);

        if (songs.Count == 0)
        {
            return !string.IsNullOrWhiteSpace(musicianQuery)
                ? ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByNameAndArtist", locale, songQuery, matchedArtistName!))
                : ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, songQuery));
        }

        if (songs.Count > 1)
        {
            BaseItem? songMatch = null;
            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                songQuery,
                songs,
                s => s.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeSong,
                locale,
                best =>
                {
                    songMatch = best;
                    return Task.FromResult<SkillResponse>(null!);
                },
                user: user).ConfigureAwait(false);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                songs = new List<BaseItem> { songMatch! };
            }
            else
            {
                var matches = songs.Take(3).Select(s => (s.Id, s.Name, (string?)Launch.GetImageUrl(s.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeSong, locale, context);
            }
        }

        // JF-578: the same both-stores writer as AddToQueue, AfterCurrent
        // placement: rehydrate a restart-wiped session queue first (the JF-577
        // guard, rationale on the shared helper), resolve the current item
        // (now-playing item first; the coherent playing token on the rehydrated
        // shape), then insert right behind it in BOTH stores (the session-side
        // fallback shapes: front when nothing is current or it is not queued,
        // the former InsertAfterCurrent).
        BaseItem song = songs[0];
        Guid? currentItemId = ProgressReporter.RehydrateAndEnqueueToBothStores(
            _queueManager, session, context, song.Id, DeviceQueueManager.QueueInsertPlacement.AfterCurrent, Logger, "PlayNext");

        // JF-424.1: the insertion displaced the item that follows the current one, so
        // any pre-computed next-track entry for this device is stale by definition.
        NextTrackPrecomputeCache.Invalidate(context.System.Device.DeviceID);

        // If nothing is genuinely playing (no now-playing item and no coherent
        // playing token), start playback; on the rehydrated shape there IS a
        // current item, so the insert lands behind the live stream instead of a
        // ReplaceAll launching the inserted song over it.
        if (currentItemId == null)
        {
            session.FullNowPlayingItem = song;
            string itemId = song.Id.ToString();
            return Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, song, user, context);
        }

        Logger.LogInformation("PlayNext: {SongName} queued to play next", song.Name);
        return ResponseBuilder.Tell(ResponseStrings.Get("PlayNextConfirmed", locale, song.Name));
    }
}
