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
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaybackNearlyFinished events.
/// Pre-fetches and enqueues the next stream URL for gapless playback transitions.
/// Supports loop modes (RepeatOne replays the same track, RepeatAll wraps around),
/// shuffle order, and radio mode auto-population when the queue runs out.
/// JF-324: when the finishing item is an Episode played through AudioPlayer, it also
/// auto-advances to the series' next episode (the only episode auto-advance path;
/// VideoApp playback emits no events).
/// </summary>
#pragma warning disable CA1711
public class PlaybackNearlyFinishedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Bound on the episode auto-advance's unplayed-episodes query: one screen of
    /// candidates in season-then-episode order is far more than a single gapless
    /// advance needs, and the bound keeps the query cost flat on huge series.
    /// </summary>
    private const int EpisodeCandidateQueryLimit = 32;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackNearlyFinishedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlaybackNearlyFinishedEventHandler(
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
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackNearlyFinished;
    }

    /// <summary>
    /// Pre-fetch the next item in the queue and enqueue it for gapless playback.
    /// Handles loop modes (RepeatOne, RepeatAll), shuffle, radio mode auto-population,
    /// and progressive queue continuation for large libraries.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A task representing the async operation.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        // Check for sleep timer deadline encoded in the current token (parsed by the
        // shared StreamTokenCodec, the one owner of the suffix format, JF-447)
        string? currentToken = context.AudioPlayer?.Token;
        string deviceId = context.GetDeviceId();
        Logger.LogDebug(
            "PlaybackNearlyFinished: currentToken={Token}, offset={OffsetMs}ms",
            currentToken, context.AudioPlayer?.OffsetInMilliseconds);

        if (StreamTokenCodec.TryGetSleepDeadlineUtcTicks(currentToken, out long deadlineTicks)
            && DateTimeOffset.UtcNow.UtcTicks >= deadlineTicks)
        {
            Logger.LogInformation("Sleep timer expired, stopping playback");
            return BuildKeepAliveResponse();
        }

        // JF-390 PreEnqueueOnStart (pre-compute): check the cache for a pre-resolved
        // next track (computed by PlaybackStarted when the current track began). On a
        // hit, skip the library lookups entirely and respond instantly. Only applies
        // to sequential playback (no shuffle, no repeat); other modes fall through
        // to the full resolution below. The playback order in this gate is the
        // AUTHORITATIVE one (per-device queue via ResolvePlaybackOrder, JF-447 trust
        // sweep), not the session PlayState: a device queue marked Shuffle with a stale
        // session PlayState could otherwise pass the gate and serve a stale-order
        // precomputed entry that the full resolution below would never produce. Repeat
        // mode stays on the session PlayState because the resolution below reads the
        // same source for it.
        var (resolvedOrder, resolvedReshuffled) = ResolvePlaybackOrder(session, context);

        if (_config.PreEnqueueOnStart
            && (session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone) == RepeatMode.RepeatNone
            && resolvedOrder == PlaybackOrder.Default)
        {
            if (NextTrackPrecomputeCache.TryGet(deviceId, currentToken ?? string.Empty,
                    out Guid cachedNextId, out BaseItem? cachedItem, out string? cachedUrl)
                && cachedItem != null && cachedUrl != null)
            {
                // JF-424.1: the cache key (device) and its validation token (the bare
                // item GUID) identify an ITEM, not a playback session, so an entry can
                // outlive the queue it was computed from when the queue is cleared or
                // changed mid-track. Serve only when the cached item is STILL the
                // sequential successor of the current item in the live session queue,
                // i.e. exactly what ResolveNextItemId below would produce; otherwise
                // fall through to full resolution (which also handles PostPlay when the
                // queue no longer has a successor).
                if (CachedNextStillFollowsCurrent(session, context, cachedNextId))
                {
                    Logger.LogInformation(
                        "PlaybackNearlyFinished: cache hit, responding instantly with pre-computed next='{NextItem}' (no library lookups)",
                        cachedItem.Name);

                    UpdateRecoveryPointer(deviceId, cachedNextId.ToString(), context, cachedItem.Name);

                    return BuildAudioPlayerResponse(PlayBehavior.Enqueue, cachedUrl, cachedNextId.ToString(), cachedItem, user, context);
                }

                Logger.LogInformation(
                    "PlaybackNearlyFinished: pre-computed next='{NextItem}' no longer follows the current item in the live queue (cleared or changed since PlaybackStarted); falling through to full resolution",
                    cachedItem.Name);
            }
            else
            {
                Logger.LogDebug("PlaybackNearlyFinished: PreEnqueueOnStart on but no cache hit, falling through to full resolution");
            }
        }

        // Progressive queue building: fetch more items if we're approaching the end
        TryFetchContinuationBatch(session, context);

        Guid? nextItemId = ResolveNextItemId(session, context);

        Logger.LogDebug(
            "PlaybackNearlyFinished: resolved next item={NextItemId}, loop={LoopMode}, shuffle={Shuffle} (reshuffledQueue={Reshuffled})",
            nextItemId,
            session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            resolvedOrder,
            resolvedReshuffled);

        // If no next item and radio mode is on, auto-populate similar tracks
        if (nextItemId == null && RadioModeState.IsEnabled(session.UserId, deviceId))
        {
            nextItemId = await AutoPopulateRadioTracks(session, cancellationToken).ConfigureAwait(false);
        }

        if (nextItemId == null)
        {
            // Clean up continuation state when queue is exhausted
            QueueContinuationStore.Remove(session.UserId, deviceId);

            var postPlayMode = GetPostPlayBehavior(user);

            // Music PostPlay populate only runs when radio mode is NOT active: radio
            // mode handles its own continuation above, and plain PostPlay is for
            // single-track playback that reaches queue exhaustion without radio. The
            // episode advance below deliberately ignores radioActive (a leftover radio
            // flag from earlier music must not stop a TV binge).
            bool radioActive = RadioModeState.IsEnabled(session.UserId, deviceId);

            // ONE current-item resolution shared by both AutoPlay branches below
            // (music populate and episode advance): the AudioPlayer token parsed
            // through the shared codec (composite sleep tokens included, JF-447),
            // with the session's now-playing item as fallback when there is no token.
            BaseItem? currentItem = StreamTokenCodec.TryGetItemId(context.AudioPlayer?.Token, out Guid currentItemId)
                ? _libraryManager.GetItemById(currentItemId)
                : session.FullNowPlayingItem;

            if (!radioActive && postPlayMode == PostPlayBehavior.AutoPlay)
            {
                // AutoPlay: find similar tracks and enqueue for gapless transition.
                // PlaybackNearlyFinished can return AudioPlayer.Play but NOT speech,
                // so the music continues seamlessly without announcement.
                nextItemId = await AutoPopulatePostPlayTracks(
                    currentItem, session, user, context, cancellationToken).ConfigureAwait(false);
            }

            // JF-324 episode auto-advance (AudioPlayer-routed TV): when the finishing
            // item is an Episode (the JF-507 audio-shaped launch of an EAC3 episode,
            // or any video item launched audio-shaped), resolve the next episode of
            // its series and enqueue it gaplessly. VideoApp playback emits NO events
            // at all (see CLAUDE.md "Stop / Session Routing During Playback"), so
            // this is the only path where episode auto-advance can exist. Deliberately
            // NOT gated on radioActive: radio mode is a MUSIC continuation state that
            // only TurnRadioOff ever clears, its populate above no-ops for a non-Audio
            // item, and a leftover radio flag from earlier music must not stop a TV
            // binge. The branch itself never touches RadioModeState.
            if (nextItemId == null)
            {
                nextItemId = await TryAutoAdvanceNextEpisodeAsync(
                    currentItem, session, user, cancellationToken).ConfigureAwait(false);
            }

            if (nextItemId == null)
            {
                Logger.LogDebug("No next item in queue, playback will end after current track");
                return ResponseBuilder.Empty();
            }

            // PostPlay AutoPlay found a next item (music radio tracks or the next
            // episode); fall through to enqueue below
        }

        // Pre-fetch the next item from the library to resolve metadata eagerly
        BaseItem? item = _libraryManager.GetItemById((Guid)nextItemId);
        if (item == null)
        {
            Logger.LogWarning("Next queue item {ItemId} not found in library", nextItemId);
            return ResponseBuilder.Empty();
        }

        string itemId = item.Id.ToString();

        // Update the device queue pointer for crash recovery (guarded, JF-424.1).
        UpdateRecoveryPointer(deviceId, itemId, context, itemNameForLog: null);

        // JF-507: codec-gated audio-launch decision; an EAC3-family video item in the
        // queue routes to the audio-only transcode instead of dying on the raw static
        // bytes (JF-505 does not apply: this launch is audio-shaped). Offset 0: a fresh
        // queue advance always plays from the item start.
        AudioLaunchSource source = ResolveAudioLaunchSource(item, itemId, user, 0, context?.System?.Device?.DeviceID);
        string audioUrl = source.Url;

        Logger.LogInformation(
            "Pre-fetching next track for gapless playback: {ItemName} ({ItemId}), loop={LoopMode}, shuffle={Shuffle} (reshuffledQueue={Reshuffled})",
            item.Name,
            itemId,
            session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            resolvedOrder,
            resolvedReshuffled);

        return BuildAudioPlayerResponse(PlayBehavior.Enqueue, audioUrl, itemId, item, user, context);
    }

    /// <summary>
    /// Check if the queue is running low and fetch more items from continuation state.
    /// This enables progressive queue building: the initial bulk-play handler fetches
    /// only the first few items, and this method lazily fetches the rest as needed.
    /// </summary>
    /// <param name="session">The current Jellyfin session.</param>
    /// <param name="context">The Alexa context for device identification.</param>
    private void TryFetchContinuationBatch(SessionInfo session, Context context)
    {
        string deviceId = context.GetDeviceId();
        QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, deviceId);
        if (continuation == null)
        {
            return;
        }

        // Find current position in the queue
        int currentIndex = FindCurrentQueueIndex(session, context);
        if (currentIndex < 0)
        {
            return;
        }

        // Only fetch when approaching the end of the current queue
        int remaining = session.NowPlayingQueue.Count - currentIndex - 1;
        if (remaining > ProgressiveQueueConstants.GetPrefetchThreshold())
        {
            return;
        }

        // Fetch the next batch
        IReadOnlyList<BaseItem> newItems = QueueContinuationFetcher.FetchNextBatch(
            continuation,
            _libraryManager,
            _userManager,
            Logger);

        if (newItems.Count == 0)
        {
            // No more items to fetch, remove continuation state
            QueueContinuationStore.Remove(session.UserId, deviceId);
            return;
        }

        // Append new items to the queue (deduplicating)
        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var seen = SessionQueue.IdSet(session);

        if (continuation.Shuffle)
        {
            newItems = ShuffleCopy(newItems);
        }

        foreach (BaseItem item in newItems)
        {
            if (seen.Add(item.Id))
            {
                queue.Add(new QueueItem { Id = item.Id });
            }
        }

        session.NowPlayingQueue = queue;

        // Remove continuation if we've fetched everything
        if (continuation.StartIndex >= continuation.TotalCount)
        {
            QueueContinuationStore.Remove(session.UserId, deviceId);
        }
    }

    /// <summary>
    /// Resolves the effective playback order and whether the queue was physically
    /// reshuffled by <c>ShuffleOnIntentHandler</c>. The authoritative source is the
    /// persisted per-device queue (written by the shuffle handlers); the Jellyfin
    /// session PlayState is only used as a fallback when no device queue exists.
    /// Shared by <see cref="ResolveNextItemId"/> (decision) and the debug/Info log
    /// lines (so they report the value actually used, not the secondary PlayState).
    /// </summary>
    /// <param name="session">The current Jellyfin session (fallback source).</param>
    /// <param name="context">The Alexa context for device identification.</param>
    /// <returns>The resolved playback order and whether the queue was reshuffled.</returns>
    private (PlaybackOrder Order, bool Reshuffled) ResolvePlaybackOrder(SessionInfo session, Context context)
    {
        Playback.DeviceQueue? deviceQueue = _queueManager?.GetQueue(context.GetDeviceId());
        if (deviceQueue == null)
        {
            return (session.PlayState?.PlaybackOrder ?? PlaybackOrder.Default, false);
        }

        PlaybackOrder order = string.Equals(deviceQueue.PlaybackOrder, "Shuffle", StringComparison.Ordinal)
            ? PlaybackOrder.Shuffle
            : PlaybackOrder.Default;
        return (order, deviceQueue.OriginalItemIds != null);
    }

    /// <summary>
    /// Finds the currently playing item's position in the session queue, or -1 when
    /// it cannot be located. Resolution order (the session's now-playing item first,
    /// then the AudioPlayer token parsed through the shared stream-token codec) is
    /// shared by the queue-continuation fetch and <see cref="ResolveNextItemId"/>,
    /// so both agree on "current item". The JF-424.1 precompute-cache validation
    /// deliberately resolves
    /// TOKEN-FIRST instead (see <see cref="CachedNextStillFollowsCurrent"/>) to match
    /// the store side.
    /// </summary>
    /// <param name="session">The current Jellyfin session with play state.</param>
    /// <param name="context">The Alexa context for current token.</param>
    /// <returns>The zero-based queue index of the current item, or -1 if absent.</returns>
    private int FindCurrentQueueIndex(SessionInfo session, Context context)
    {
        Guid? currentItemId = session.FullNowPlayingItem?.Id;
        if (currentItemId == null
            && StreamTokenCodec.TryGetItemId(context.AudioPlayer?.Token, out Guid parsedToken))
        {
            // Shared codec: composite sleep tokens ("{guid}|sleep:{ticks}") resolve too; a raw
            // Guid.TryParse fails them and makes a live queue look exhausted.
            currentItemId = parsedToken;
        }

        return currentItemId == null ? -1 : SessionQueue.IndexOfQueueItem(session, currentItemId.Value);
    }

    /// <summary>
    /// JF-424.1: a precompute cache hit may only be served when the cached next item
    /// is STILL the sequential successor of the current item in the live session queue.
    /// The precompute key (device) and its validation token (the bare item GUID)
    /// identify an item, not a playback session, so an entry can outlive the queue it
    /// was computed from (cleared or changed mid-track). This re-check keeps the
    /// served item identical to what <see cref="ResolveNextItemId"/> would produce
    /// WHEN THE SESSION'S NOW-PLAYING ITEM AGREES WITH THE TOKEN: this validation
    /// resolves the current item TOKEN-FIRST (matching the store side, see below),
    /// while ResolveNextItemId resolves FullNowPlayingItem-first
    /// (<see cref="FindCurrentQueueIndex"/>); the two disagree exactly when a start
    /// report failed and the session still holds the previous queue item, at in-memory
    /// cost only.
    /// JF-447 (review follow-up): the current item resolves TOKEN-FIRST, matching the
    /// STORE side (<see cref="PlaybackStartedEventHandler.TryPrecomputeNext"/> resolves
    /// the token alone). The FullNowPlayingItem-first order used by
    /// <see cref="FindCurrentQueueIndex"/> made the validation disagree with the store
    /// when a start report failed and the session still held the PREVIOUS queue item:
    /// the validation computed that item's successor (the playing track itself),
    /// rejected the cached entry, and the fall-through enqueued the playing track after
    /// itself (the JF-409 self-reenqueue class).
    /// </summary>
    /// <param name="session">The current Jellyfin session with play state.</param>
    /// <param name="context">The Alexa context for current token.</param>
    /// <param name="cachedNextId">The next item ID taken from the precompute cache.</param>
    /// <returns>True when the cached item is still the current item's queue successor.</returns>
    private bool CachedNextStillFollowsCurrent(SessionInfo session, Context context, Guid cachedNextId)
    {
        // A matched cache entry implies the token parsed at store time, and TryGet
        // matched the tokens by string equality, so the token parses here too
        // (composite sleep tokens included, via the shared codec).
        if (!StreamTokenCodec.TryGetItemId(context.AudioPlayer?.Token, out Guid currentItemId))
        {
            return false;
        }

        int currentIndex = SessionQueue.IndexOfQueueItem(session, currentItemId);
        return currentIndex >= 0
            && currentIndex + 1 < session.NowPlayingQueue.Count
            && session.NowPlayingQueue[currentIndex + 1].Id == cachedNextId;
    }

    /// <summary>
    /// Updates the device queue's crash-recovery pointer, shared by the precompute
    /// cache-hit branch and the full-resolution branch. JF-424.1: the pointer moves
    /// only when <see cref="DeviceQueueManager.MoveTo"/> actually moved; the item can
    /// be absent from the device queue when the session queue was built by a play path
    /// that does not mirror it, and a failed move must not leave CurrentItemId dangling
    /// at an item the queue does not contain. The position is also refreshed for
    /// resume-after-pause accuracy (NearlyFinished fires periodically).
    /// </summary>
    /// <param name="deviceId">The device whose queue pointer to update.</param>
    /// <param name="itemId">The item that is now current.</param>
    /// <param name="context">The Alexa context for the current playback offset.</param>
    /// <param name="itemNameForLog">Optional item name for the failure debug log.</param>
    private void UpdateRecoveryPointer(string deviceId, string itemId, Context context, string? itemNameForLog)
    {
        if (_queueManager == null)
        {
            return;
        }

        if (_queueManager.MoveTo(deviceId, itemId))
        {
            var queue = _queueManager.GetOrCreateQueue(deviceId);
            queue.CurrentItemId = itemId;
            if (context.AudioPlayer != null)
            {
                queue.CurrentPositionTicks = TimeSpan.FromMilliseconds(context.AudioPlayer.OffsetInMilliseconds).Ticks;
            }
        }
        else
        {
            Logger.LogDebug(
                "PlaybackNearlyFinished: item '{Item}' not in the device queue; pointer left untouched",
                itemNameForLog ?? itemId);
        }
    }

    /// <summary>
    /// Resolve the next item ID based on the current position in the queue,
    /// taking loop and shuffle modes into account.
    /// </summary>
    /// <param name="session">The current Jellyfin session with play state.</param>
    /// <param name="context">The Alexa context for current token.</param>
    /// <returns>The next item ID, or null if playback should end.</returns>
    private Guid? ResolveNextItemId(SessionInfo session, Context context)
    {
        RepeatMode repeatMode = session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone;

        // playbackOrder + reshuffledQueue come from the authoritative per-device
        // queue (see ResolvePlaybackOrder). When reshuffledQueue is true the queue
        // order IS the shuffle order, so we advance sequentially (each track once);
        // the random-pick fallback below only runs for shuffle requested WITHOUT a
        // physical reshuffle.
        var (playbackOrder, reshuffledQueue) = ResolvePlaybackOrder(session, context);

        if (session.NowPlayingQueue.Count == 0)
        {
            return null;
        }

        // Find current position in the queue
        int currentIndex = FindCurrentQueueIndex(session, context);
        if (currentIndex < 0)
        {
            return null;
        }

        // RepeatOne: replay the same track
        if (repeatMode == RepeatMode.RepeatOne)
        {
            return session.NowPlayingQueue[currentIndex].Id;
        }

        // Shuffle mode (only when the queue was NOT physically reshuffled): pick a
        // random next track from the queue, avoiding immediate repeat. A reshuffled
        // queue falls through to sequential advance so its carefully shuffled order
        // is honored (each track once) rather than re-randomized every step.
        if (playbackOrder == PlaybackOrder.Shuffle && !reshuffledQueue && session.NowPlayingQueue.Count > 1)
        {
            int nextIndex;
            if (session.NowPlayingQueue.Count == 2)
            {
                nextIndex = currentIndex == 0 ? 1 : 0;
            }
            else
            {
                do
                {
                    nextIndex = Random.Shared.Next(session.NowPlayingQueue.Count);
                }
                while (nextIndex == currentIndex);
            }

            return session.NowPlayingQueue[nextIndex].Id;
        }

        // Sequential: advance to next item
        int nextPos = currentIndex + 1;

        // RepeatAll: wrap around to the beginning when reaching the end
        if (nextPos >= session.NowPlayingQueue.Count)
        {
            if (repeatMode == RepeatMode.RepeatAll)
            {
                nextPos = 0;
            }
            else
            {
                return null;
            }
        }

        return session.NowPlayingQueue[nextPos].Id;
    }

    /// <summary>
    /// Resolves the session's Jellyfin user via <see cref="IUserManager"/>, shared by
    /// the radio, PostPlay, and episode-advance branches (each caller keeps its own
    /// null guard: the branch cannot run without a user).
    /// </summary>
    /// <param name="session">The session whose user to resolve.</param>
    /// <returns>The Jellyfin user, or null when the manager has no such user.</returns>
    private JellyfinUser? ResolveJellyfinUser(SessionInfo session)
        => _userManager.GetUserById(session.UserId);

    /// <summary>
    /// Find similar tracks to the current item and append them to the queue.
    /// Returns the first new track ID, or null if no tracks found.
    /// </summary>
    private async Task<Guid?> AutoPopulateRadioTracks(SessionInfo session, CancellationToken cancellationToken)
    {
        var currentAudio = session.FullNowPlayingItem as MediaBrowser.Controller.Entities.Audio.Audio;
        if (currentAudio == null)
        {
            return null;
        }

        JellyfinUser? jellyfinUser = ResolveJellyfinUser(session);
        if (jellyfinUser == null)
        {
            return null;
        }

        Entities.User? pluginUser = _config.GetUserById(session.UserId);
        IReadOnlyList<BaseItem> similar = await FindRadioTracksAsync(currentAudio, jellyfinUser, pluginUser!, _libraryManager, cancellationToken).ConfigureAwait(false);

        if (similar.Count == 0)
        {
            Logger.LogInformation("Radio mode: no similar tracks found, ending radio");
            return null;
        }

        List<BaseItem> shuffled = ShuffleAndCap(similar, 15);

        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var seen = SessionQueue.IdSet(session);
        Guid? firstNewId = null;
        int addedCount = 0;

        foreach (BaseItem track in shuffled)
        {
            if (seen.Add(track.Id))
            {
                queue.Add(new QueueItem { Id = track.Id });
                firstNewId ??= track.Id;
                addedCount++;
            }
        }

        if (firstNewId != null)
        {
            session.NowPlayingQueue = queue;
            Logger.LogInformation("Radio mode: added {Count} similar tracks", addedCount);
        }

        return firstNewId;
    }

    /// <summary>
    /// Find similar tracks to the specified item and append them to the queue
    /// for PostPlay AutoPlay gapless transition. Enables RadioModeState for
    /// subsequent continuation via the existing AutoPopulateRadioTracks flow.
    /// The current item is resolved ONCE by the caller (HandleAsync's shared
    /// resolution, token-first with the session fallback) and passed in; a
    /// non-Audio item no-ops via the cast.
    /// </summary>
    private async Task<Guid?> AutoPopulatePostPlayTracks(
        BaseItem? currentItem,
        SessionInfo session,
        Entities.User user,
        Context context,
        CancellationToken cancellationToken)
    {
        var currentAudio = currentItem as MediaBrowser.Controller.Entities.Audio.Audio;
        if (currentAudio == null)
        {
            return null;
        }

        JellyfinUser? jellyfinUser = ResolveJellyfinUser(session);
        if (jellyfinUser == null)
        {
            return null;
        }

        Entities.User? pluginUser = _config.GetUserById(session.UserId);
        IReadOnlyList<BaseItem> similar = await FindRadioTracksAsync(
            currentAudio, jellyfinUser, pluginUser!, _libraryManager, cancellationToken).ConfigureAwait(false);

        if (similar.Count == 0)
        {
            Logger.LogInformation("PostPlay AutoPlay: no similar tracks found for {ItemName}", currentAudio.Name);
            return null;
        }

        List<BaseItem> shuffled = ShuffleAndCap(similar, 15);

        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var seen = SessionQueue.IdSet(session);
        Guid? firstNewId = null;
        int addedCount = 0;

        foreach (BaseItem track in shuffled)
        {
            if (seen.Add(track.Id))
            {
                queue.Add(new QueueItem { Id = track.Id });
                firstNewId ??= track.Id;
                addedCount++;
            }
        }

        if (firstNewId != null)
        {
            session.NowPlayingQueue = queue;
            RadioModeState.Enable(session.UserId, context.GetDeviceId());
            Logger.LogInformation("PostPlay AutoPlay: added {Count} similar tracks, radio mode enabled", addedCount);
        }

        return firstNewId;
    }

    /// <summary>
    /// JF-324 episode auto-advance: resolve the next episode of the finishing
    /// episode's series with ONE direct ordered-unplayed query scoped to the series
    /// (AncestorIds + IsPlayed=false, season-then-episode order) and append the
    /// first candidate outside the skip-set (current + queued ids) to the session
    /// queue for a gapless AudioPlayer transition. Movies and every non-Episode
    /// item return null (a movie has no natural next; music keeps the radio/PostPlay
    /// paths above). End of series returns null so the caller falls through to
    /// today's end-of-queue behavior, and the intent path's "latest episode"
    /// fallback is deliberately NOT applied here: re-serving an already-watched
    /// episode right after it finished would loop the binge.
    /// WHY a direct query and not NextUp (C1): Jellyfin's SeriesId-scoped GetNextUp
    /// returns AT MOST ONE episode (the server resolves a single
    /// presentationUniqueKey, runs one next-up pass whose episode query takes
    /// FirstOrDefault, and Limit only truncates afterwards; see
    /// Emby.Server.Implementations/TV/TVSeriesManager.cs in 10.11), and on this
    /// event path that single answer IS the finishing episode (its stop is reported
    /// only on PlaybackStopped, which has not fired yet, so it is still the first
    /// unwatched). The skip-set filters it out, so a NextUp-based advance would
    /// never fire in production. The direct query handles everything NextUp
    /// could not here: the finishing episode itself (still unplayed, first
    /// candidate, skipped), cross-season succession (season-then-episode order),
    /// and partially-watched series (first unplayed in order after the skipped
    /// ones). Gating mirrors PlayNextEpisodeIntentHandler: the PostPlay mode, the
    /// VideoPlaybackEnabled feature flag, and the VideosEnabled content gate
    /// (FilterByContentAccess hard-zero, JF-466) all skip the branch before any
    /// library query, and the series is authorized with the same content-gated,
    /// library-filtered series query shape the intent path resolves names through
    /// (ApplyLibraryFilter TopParentIds), scoped by id instead of name. No
    /// announcement: the enqueue is gapless, exactly like the music radio path.
    /// </summary>
    /// <param name="currentItem">The finishing item, resolved once by the caller (token-first, session fallback).</param>
    /// <param name="session">The current Jellyfin session (user + now-playing queue).</param>
    /// <param name="user">The plugin user (PostPlay mode + library scope).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The next episode's item ID (appended to the queue), or null when the branch does not apply.</returns>
    private async Task<Guid?> TryAutoAdvanceNextEpisodeAsync(
        BaseItem? currentItem,
        SessionInfo session,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        // Config gates first, before any library work (gate-before-query): the
        // PostPlay mode, the VideoPlaybackEnabled feature flag, and the VideosEnabled
        // content gate (JF-466 hard-zero contract) all skip the branch without
        // touching the library, mirroring the intent path. An event response cannot
        // speak, so a disabled configuration just skips the advance.
        if (GetPostPlayBehavior(user) != PostPlayBehavior.AutoPlay)
        {
            return null;
        }

        if (Plugin.Instance?.Configuration is { } liveConfig && !liveConfig.VideoPlaybackEnabled)
        {
            Logger.LogInformation("Episode auto-advance: video playback disabled via configuration, skipping");
            return null;
        }

        if (FilterByContentAccess(new[] { BaseItemKind.Episode }).Length == 0)
        {
            Logger.LogInformation("Episode auto-advance: episodes disabled via configuration, skipping");
            return null;
        }

        if (currentItem is not Episode episode)
        {
            return null;
        }

        Guid seriesId = episode.SeriesId;
        if (seriesId == Guid.Empty)
        {
            Logger.LogDebug("Episode auto-advance: episode '{EpisodeName}' has no series, skipping", episode.Name);
            return null;
        }

        JellyfinUser? jellyfinUser = ResolveJellyfinUser(session);
        if (jellyfinUser == null)
        {
            return null;
        }

        // Per-user library authorization (the ApplyLibraryFilter equivalent of the
        // intent path's series resolution): the series must survive the same
        // content-gated, library-scoped query, scoped by id instead of search term.
        var accessQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            ItemIds = new[] { seriesId },
            IncludeItemTypes = new[] { BaseItemKind.Series },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(accessQuery, user, _libraryManager, Logger);
        IReadOnlyList<BaseItem> authorized = await RetryAsync(
            () => _libraryManager.GetItemList(accessQuery),
            "SeriesAccessCheck",
            cancellationToken).ConfigureAwait(false);
        if (authorized.Count == 0)
        {
            Logger.LogInformation(
                "Episode auto-advance: series of '{EpisodeName}' is outside the user's allowed libraries, skipping",
                episode.Name);
            return null;
        }

        // Direct unplayed-episodes query (C1, see the doc comment): ordered
        // season-then-episode so cross-season succession and partially-watched
        // series both resolve to the first unplayed episode after the skip-set.
        // IsPlayed=false includes the in-progress/resumable episode (the 10.11
        // server filter reads only UserData.Played, never the position), so the
        // finishing episode itself is the query's first candidate and is skipped;
        // the queue may still hold earlier episodes whose stops were never reported
        // to Jellyfin (the JF-409 self-reenqueue class). The first candidate
        // outside that skip-set is the true next. TRADE-OFF vs the intent path's
        // NextUp (review JF-324 F2): with an unplayed WATCH-HOLE below the
        // finishing episode (out-of-order watching), the ordered query surfaces
        // the older episode first and the advance jumps BACK to it (from offset
        // 0) instead of forward; accepted as 'next unplayed in order' semantics.
        var candidatesQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            AncestorIds = new[] { seriesId },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsPlayed = false,
            IsVirtualItem = false,
            // Season-0 specials sort before every real season and would win the
            // advance; the server's own next-up excludes them the same way
            // (TVSeriesManager: ParentIndexNumberNotEquals = 0), review JF-324 F1.
            ParentIndexNumberNotEquals = 0,
            OrderBy = new[] { (ItemSortBy.ParentIndexNumber, SortOrder.Ascending), (ItemSortBy.IndexNumber, SortOrder.Ascending) },
            Limit = EpisodeCandidateQueryLimit,
            DtoOptions = new DtoOptions(true)
        };
        Logger.LogDebug(
            "Episode auto-advance: querying unplayed episodes of seriesId={SeriesId} (limit={Limit})",
            seriesId, EpisodeCandidateQueryLimit);
        IReadOnlyList<BaseItem> candidates = await RetryAsync(
            () => _libraryManager.GetItemList(candidatesQuery),
            "GetNextEpisodes",
            cancellationToken).ConfigureAwait(false);

        var skip = SessionQueue.IdSet(session);
        skip.Add(episode.Id);
        BaseItem? next = candidates.FirstOrDefault(c => !skip.Contains(c.Id));
        if (next == null)
        {
            Logger.LogInformation(
                "Episode auto-advance: no next episode after '{EpisodeName}' (end of series or all candidates already queued)",
                episode.Name);
            return null;
        }

        // Append to the session queue so the NEXT advance (and the queue-position
        // machinery above) sees the finishing item's successor in place.
        var queue = new List<QueueItem>(session.NowPlayingQueue) { new QueueItem { Id = next.Id } };
        session.NowPlayingQueue = queue;

        Logger.LogInformation(
            "Episode auto-advance: enqueuing next episode '{NextEpisodeName}' after '{EpisodeName}' (gapless, no announcement)",
            next.Name,
            episode.Name);
        return next.Id;
    }
}
