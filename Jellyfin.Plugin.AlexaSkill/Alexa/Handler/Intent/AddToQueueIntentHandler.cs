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
/// Handler for AddToQueueIntent requests.
/// Appends a song to the end of the playback queue, in BOTH stores since JF-578
/// (the persisted device queue and the session queue, through the shared
/// queue-membership writer on <see cref="ProgressReporter"/>).
/// </summary>
public class AddToQueueIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ISongNgramIndex? _songNgramIndex;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddToQueueIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="songNgramIndex">Optional in-memory song index (warming gate proxy for the cold song query).</param>
    /// <param name="queueManager">Optional per-device queue manager (the JF-578 both-stores queue writer).</param>
    public AddToQueueIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AddToQueue, StringComparison.Ordinal);
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
        // JF-659: the ER canonical feeds the artist search; musicianQuery keeps
        // driving the not-found speech (the JF-642 F-1 lesson; SlotValueHelper
        // owns the full contract).
        string? canonicalMusician = Util.SlotValueHelper.GetCanonicalValue(intentRequest, "musician");

        Logger.LogDebug("AddToQueue: entered, song={SongQuery}, musician={MusicianQuery}", songQuery, musicianQuery);

        // JF-550 (dead-mic sweep; JF-549 class).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "AddToQueue") is { } elicitCancel)
        {
            return elicitCancel;
        }

        if (string.IsNullOrWhiteSpace(songQuery))
        {
            return BuildDialogElicitResponse("DidNotCatchQueueItem", locale, "song", IntentNames.AddToQueue, Util.ElicitSlots.For(IntentNames.AddToQueue));
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
            // JF-715: the gate-consume composite shared by the three song+musician
            // sites (PlaySong + this twin pair; full contract on
            // TryArbitrateOrSearchAsync, which also owns the constraint probe and
            // its normalization since the fold). Site-specific facts kept HERE:
            // the ask leg's accepted JF-690 shift is queue-shaped (the confirm
            // plays the artist INSTEAD OF queueing; the queue operation is lost
            // with the ambiguity, the disambiguation prompt takes over the turn),
            // and this handler has no PlaySong-style play-the-artist bypass, so
            // the generic-word title miss is pre-existing either way.
            var (gateTerminal, gateArtistIds, gateArtistName) = await MultiValueErDisambiguation.TryArbitrateOrSearchAsync(
                intentRequest, user, _artistIndex, _libraryManager, Logger, locale,
                songQuery,
                canonicalMusician ?? musicianQuery,
                musicianQuery,
                "GetArtistsForQueue",
                cancellationToken).ConfigureAwait(false);
            if (gateTerminal != null)
            {
                return gateTerminal;
            }

            artistIds = gateArtistIds;
            matchedArtistName = gateArtistName;
        }

        // JF-645 (the JF-643 pattern): the song title feeds the SearchTerm index
        // and the fuzzy disambiguation below, both Latin-script, so a katakana
        // title slot is romanized once here. The gate composite above received
        // the RAW slot (its constraint probe owns its own normalization); the
        // not-found speech speaks the romanized form, the accepted JF-643 speech
        // trade (a kana slot's pre-change outcome was total failure).
        // CONVENTION SPLIT (JF-645 gate-marker): the queue paths speak the
        // ROMANIZED form on miss (PlaySong's JF-643 trade), while the genre/series
        // sites wired in the same change keep the RAW kana slot for speech; both
        // conventions are documented at their sites - apply the matching one at the
        // next site rather than inventing a third.
        songQuery = Util.KatakanaRomanizer.Romanize(songQuery);

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
            "GetSongsForQueue",
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
            // JF-776: scoring through the romaji reading (ScoringName), speech
            // keeps the display name (the JF-755 speechSelector seam).
            var (missOutcome, missResponse) = await HandleFuzzyMiss(
                songQuery,
                songs,
                s => Util.KeywordMatcher.ScoringName(s.Name),
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeSong,
                locale,
                best =>
                {
                    songMatch = best;
                    return Task.FromResult<SkillResponse>(null!);
                },
                user: user,
                speechSelector: s => s.Name).ConfigureAwait(false);

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

        // JF-578: the add lands in BOTH queue stores. The shared writer first
        // repairs a restart-wiped session queue from the coherent persisted
        // device queue (the JF-577 guard, rationale on the shared helper), so on
        // the wiped shape the add extends the surviving device queue instead of
        // replacing its membership basis with a one-item session list, and it
        // survives the next restart. The resolved current item (now-playing item
        // first; the coherent playing token on the rehydrated shape) drives the
        // start-playback branch below.
        BaseItem song = songs[0];
        Guid? currentItemId = ProgressReporter.RehydrateAndEnqueueToBothStores(
            _queueManager, session, context, song.Id, DeviceQueueManager.QueueInsertPlacement.End, Logger, "AddToQueue");

        // If nothing is genuinely playing (no now-playing item and no coherent
        // playing token), start playback; on the rehydrated shape there IS a
        // current item, so the add lands behind the live stream instead of a
        // ReplaceAll launching the added song over it.
        if (currentItemId == null)
        {
            session.FullNowPlayingItem = song;
            string itemId = song.Id.ToString();
            return Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, song, user, context);
        }

        Logger.LogInformation("AddToQueue: added {SongName} to queue", song.Name);
        return ResponseBuilder.Tell(ResponseStrings.Get("AddedToQueue", locale, song.Name));
    }
}
