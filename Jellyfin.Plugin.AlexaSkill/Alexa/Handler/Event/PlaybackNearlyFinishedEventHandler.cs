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
/// JF-712 derive-then-commit: every SYNTHESIZED-continuation write in this handler
/// (the device pointer via <see cref="UpdateRecoveryPointer"/>, the exhaustion arms'
/// session-queue appends and RadioModeState arming via
/// <see cref="CommitPendingContinuation"/>) commits only AFTER the launch build
/// succeeds, so a refused launch (StreamTokenNotConfiguredException from the
/// builder, translated once by RequestPipeline into the event keep-alive) leaves
/// the pointer naming the finishing item with its position intact and the session
/// queue un-appended. The pre-build queue writes that are NOT synthesized
/// continuation stay pre-build by design, each marked at its site: the entry
/// rehydration mirror, TryFetchContinuationBatch's fetched batches (the derivation
/// input), and the exhaustion QueueContinuationStore.Remove. The build never reads
/// the pointer or the appends (it consumes the resolved source/item locals), and
/// the commit still runs before this method returns, preserving the JF-447
/// directive-time-truth property the stop classifier relies on; the full decision
/// record lives in the backlog task.
/// JF-720: every session-queue APPEND in this handler routes through the
/// SessionQueue append-unseen family (<see cref="SessionQueue.AppendUnseen(SessionInfo, System.Collections.Generic.IEnumerable{MediaBrowser.Model.Session.QueueItem})"/> at
/// the fetch and the commit, <see cref="SessionQueue.UnseenItems"/> at the
/// derive), pinned by the writer-side roster test; this handler never assigns
/// NowPlayingQueue directly.
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
        // shared StreamTokenCodec, the one owner of the suffix format, JF-447; the
        // expired predicate is the ONE definition both event gates read, JF-683
        // review F5)
        string? currentToken = context.AudioPlayer?.Token;
        string deviceId = context.GetDeviceId();
        Logger.LogDebug(
            "PlaybackNearlyFinished: currentToken={Token}, offset={OffsetMs}ms",
            currentToken, context.AudioPlayer?.OffsetInMilliseconds);

        if (StreamTokenCodec.IsSleepExpiredUtc(currentToken, DateTimeOffset.UtcNow))
        {
            Logger.LogInformation("Sleep timer expired, stopping playback");
            return BuildKeepAliveResponse();
        }

        // JF-574 crash-recovery rehydration (rationale in the shared helper's doc,
        // hoisted JF-577): without this bridge a restart-wiped session queue reads
        // as FALSE queue-exhaustion and PostPlay AutoPlay replaces the artist queue
        // with radio tracks (live incident 2026-09-16 07:28, Norah Jones).
        ProgressReporter.TryRehydrateSessionQueueFromDevice(_queueManager, session, context, Logger, "PlaybackNearlyFinished");

        // JF-390 PreEnqueueOnStart (pre-compute): check the cache for a pre-resolved
        // next track (computed by PlaybackStarted when the current track began). On a
        // hit, skip the library lookups entirely and respond instantly. Only applies
        // to sequential playback (no shuffle, no repeat); other modes fall through
        // to the full resolution below. The playback order in this gate is the
        // AUTHORITATIVE one (per-device queue via ProgressReporter.ResolvePlaybackOrder,
        // JF-447 trust sweep), not the session PlayState: a device queue marked Shuffle
        // with a stale session PlayState could otherwise pass the gate and serve a
        // stale-order precomputed entry that the full resolution below would never
        // produce. Repeat mode stays on the session PlayState because the resolution
        // below reads the same source for it.
        var (resolvedOrder, resolvedReshuffled) = ProgressReporter.ResolvePlaybackOrder(session, context, _queueManager);

        // This fetch must run BEFORE the precompute cache-hit early return below:
        // placed after it, every cache-served NearlyFinished skips the fetch and the
        // queue starves at its initial page until the last track (JF-666).
        await TryFetchContinuationBatch(session, context, cancellationToken).ConfigureAwait(false);

        if (_config.PreEnqueueOnStart
            && (session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone) == RepeatMode.RepeatNone
            && resolvedOrder == PlaybackOrder.Default)
        {
            if (NextTrackPrecomputeCache.TryGet(deviceId, currentToken ?? string.Empty,
                    out Guid cachedNextId, out BaseItem? cachedItem, out string? cachedUrl, out int cachedRatePerMille)
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

                    // JF-636: the stored rate rides the launch so the chokepoint records
                    // the launch scope the cached URL actually serves. JF-712: build
                    // BEFORE the pointer write (the class doc's derive-then-commit; a
                    // token-gated cached URL can refuse inside the builder).
                    SkillResponse cachedResponse = Launch.BuildAudioPlayerResponse(PlayBehavior.Enqueue, cachedUrl, cachedNextId.ToString(), cachedItem, user, context, ratePerMille: cachedRatePerMille, queueManager: _queueManager);

                    UpdateRecoveryPointer(deviceId, cachedNextId.ToString(), context, cachedItem.Name);
                    return cachedResponse;
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

        Guid? nextItemId = ResolveNextItemId(session, context);

        Logger.LogDebug(
            "PlaybackNearlyFinished: resolved next item={NextItemId}, loop={LoopMode}, shuffle={Shuffle} (reshuffledQueue={Reshuffled})",
            nextItemId,
            session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            resolvedOrder,
            resolvedReshuffled);

        // ONE finishing-item resolution shared by the JF-670 book gate, both AutoPlay
        // branches, and the JF-636 advance-rate tail: the AudioPlayer token parsed
        // through the shared codec (composite sleep tokens included, JF-447), with the
        // session's now-playing item as fallback when there is no token. Resolved once
        // per event at this single site (the precompute cache-hit path early-returns
        // above; nothing between here and the tail mutates the token or the session
        // item), so the resolution shape cannot drift between copies (the JF-424.1 /
        // JF-447 token-vs-session disagreement class).
        BaseItem? currentItem = StreamTokenCodec.TryGetItemId(context.AudioPlayer?.Token, out Guid currentItemId)
            ? _libraryManager.GetItemById(currentItemId)
            : session.FullNowPlayingItem;

        // PostPlay/radio policy resolved once (the radio branch, the exhaustion
        // block, and the episode advance's gate all consume this one read).
        var postPlayMode = Progress.GetPostPlayBehavior(user);
        bool radioOn = RadioModeState.IsEnabled(session.UserId, deviceId);

        // JF-712: the exhaustion arms below only DERIVE their continuation into this
        // pending population; it commits at the single commit point after the launch
        // build succeeds (the class doc's derive-then-commit).
        PendingContinuation? pending = null;

        // If no next item and radio mode is on, auto-populate similar tracks. The
        // books-never-radio gate (JF-670) lives inside the seeders on the seed
        // source itself, so no branch can bypass it.
        if (nextItemId == null && radioOn)
        {
            pending = await DeriveSimilarTracksPopulation("Radio mode", armRadioMode: false, currentItem, session, cancellationToken).ConfigureAwait(false);
            nextItemId = pending?.FirstNewId;
        }

        if (nextItemId == null)
        {
            // Clean up continuation state when queue is exhausted (JF-712: stays
            // pre-build because deferring it would resurrect a spent continuation
            // on a refused launch and re-fetch an exhausted source forever. NOT a
            // total-invariant claim (JF-712 gate-marker): two pre-existing shapes
            // still drop a LIVE continuation here - a momentarily-absent finishing
            // item (restart-wiped session the rehydration declined) skips the fetch
            // guard while un-fetched batches remain, and an all-dupes fetched batch
            // leaves the store entry alive until this Remove drops it. Both shapes
            // predate JF-712; named here so a future reader does not trust a
            // stronger invariant than the code provides.
            QueueContinuationStore.Remove(session.UserId, deviceId);

            // Music PostPlay populate only runs when radio mode is NOT active: radio
            // mode handles its own continuation above, and plain PostPlay is for
            // single-track playback that reaches queue exhaustion without radio. The
            // episode advance below deliberately ignores radioOn (a leftover radio
            // flag from earlier music must not stop a TV binge).
            // BOUNDED COST (JF-712 gate-marker): under a SUSTAINED refusal the
            // queue never appends, so each Near-finished refire re-runs this
            // derive (a library query plus a shuffle) instead of the once-per-
            // exhaustion the old derive-time append paid. Acceptable while it
            // needs a misconfigured secret and refires are sparse; add a
            // recent-refusal guard here if refire density ever grows.
            if (!radioOn && postPlayMode == PostPlayBehavior.AutoPlay)
            {
                // AutoPlay: find similar tracks and enqueue for gapless transition.
                // PlaybackNearlyFinished can return AudioPlayer.Play but NOT speech,
                // so the music continues seamlessly without announcement.
                pending = await DeriveSimilarTracksPopulation(
                    "PostPlay AutoPlay", armRadioMode: true, currentItem, session, cancellationToken).ConfigureAwait(false);
                nextItemId = pending?.FirstNewId;
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
                pending = await TryAutoAdvanceNextEpisodeAsync(
                    currentItem, session, user, postPlayMode, cancellationToken).ConfigureAwait(false);
                nextItemId = pending?.FirstNewId;
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

        // JF-507: codec-gated audio-launch decision; an EAC3-family video item in the
        // queue routes to the audio-only transcode instead of dying on the raw static
        // bytes (JF-505 does not apply: this launch is audio-shaped). Offset 0: a fresh
        // queue advance always plays from the item start.
        // JF-636: the advance CONTINUES the finishing item's launch-scope rate, so a
        // podcast session started at 1.5x stays at 1.5x on every episode boundary
        // instead of silently falling back to 1x; a rate-1000 (or absent) scope keeps
        // music queues byte-identical to the pre-JF-636 advance. The finishing item
        // reuses the ONE resolution at the top of the method (token-first,
        // composite-sleep safe, session fallback).
        int advanceRatePerMille = currentItem != null
            ? Launch.GetActivePlaybackRate(deviceId, currentItem.Id.ToString(), _queueManager) ?? Util.PlaybackSpeed.NormalPerMille
            : Util.PlaybackSpeed.NormalPerMille;
        AudioLaunchSource source = Launch.ResolveAudioLaunchSource(item, itemId, user, 0, ratePerMille: advanceRatePerMille);

        Logger.LogInformation(
            "Pre-fetching next track for gapless playback: {ItemName} ({ItemId}), loop={LoopMode}, shuffle={Shuffle} (reshuffledQueue={Reshuffled})",
            item.Name,
            itemId,
            session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            resolvedOrder,
            resolvedReshuffled);

        // JF-712: the ONE commit point (the class doc's derive-then-commit). The
        // launch builds first; a refusal throws inside the builder before any of its
        // own records and leaves every handler write unmade. The commit still runs
        // before this method returns, so the JF-447 directive-time truth the stop
        // classifier reads (the pointer names the enqueued item strictly before the
        // displaced stream's stop can arrive) is preserved.
        SkillResponse enqueueResponse = Launch.BuildAudioPlayerResponse(PlayBehavior.Enqueue, source, itemId, item, user, context, queueManager: _queueManager);

        CommitPendingContinuation(pending, session, deviceId);
        UpdateRecoveryPointer(deviceId, itemId, context, itemNameForLog: null);
        return enqueueResponse;
    }

    /// <summary>
    /// Check if the queue is running low and fetch more items from continuation state.
    /// This enables progressive queue building: the initial bulk-play handler fetches
    /// only the first few items, and this method lazily fetches the rest as needed.
    /// Runs under the shared request budget (RetryAsync) because it sits on the
    /// precompute cache-hit fast path: a transiently failing batch query stops
    /// retrying inside the 6s budget instead of burning Alexa's response window
    /// (the JF-358 class), matching the initial fetches' bound.
    /// </summary>
    /// <param name="session">The current Jellyfin session.</param>
    /// <param name="context">The Alexa context for device identification.</param>
    /// <param name="cancellationToken">Cancellation token for the retry budget.</param>
    private async Task TryFetchContinuationBatch(SessionInfo session, Context context, CancellationToken cancellationToken)
    {
        string deviceId = context.GetDeviceId();
        QueueContinuation? continuation = QueueContinuationStore.Get(session.UserId, deviceId);
        if (continuation == null)
        {
            // JF-683: the three skip guards each log their decision and values. The
            // live 2026-09-30 round was misdiagnosed from logs exactly because these
            // returns were silent (a legitimate threshold skip was read as a guard
            // bug); the per-guard lines make the next round readable without guessing.
            Logger.LogDebug(
                "ContinuationFetch: skip, no continuation stored for user={UserId} device={DeviceId} (queueCount={QueueCount})",
                session.UserId, deviceId, session.NowPlayingQueue.Count);
            return;
        }

        // Find current position in the queue
        int currentIndex = FindCurrentQueueIndex(session, context);
        if (currentIndex < 0)
        {
            Logger.LogDebug(
                "ContinuationFetch: skip, current item not found in the session queue (index=-1, nowPlayingItem={NowPlayingItem}, token={Token}, queueCount={QueueCount}, source={SourceType})",
                session.FullNowPlayingItem?.Id, context.AudioPlayer?.Token, session.NowPlayingQueue.Count, continuation.SourceType);
            return;
        }

        // JF-674 queue-identity validation (the mechanism's rationale lives on
        // MintedQueueItemIds). Placed after the current-index guard, the cheaper
        // precondition that already owns the unlocatable-current-item skip (that
        // shape's exhaustion tail drops the entry regardless, the JF-712-documented
        // class), so the discard fires exactly where a fetch would actually have
        // been considered. LogInformation, not Debug: this is a state change (the
        // entry dies here), the JF-683 readable-log discipline.
        if (!continuation.IsForLiveQueue(session))
        {
            Logger.LogInformation(
                "ContinuationFetch: discard, the stored {SourceType} continuation's minted queue is no longer the live queue (minted {MintedCount} items, live queue holds {QueueCount}); device={DeviceId}",
                continuation.SourceType, continuation.MintedQueueItemIds.Count, session.NowPlayingQueue.Count, deviceId);
            QueueContinuationStore.Remove(session.UserId, deviceId);
            return;
        }

        // Only fetch when approaching the end of the current queue. The threshold is
        // read once so the logged value is provably the one the guard compared.
        int threshold = ProgressiveQueueConstants.GetPrefetchThreshold();
        int remaining = session.NowPlayingQueue.Count - currentIndex - 1;
        if (remaining > threshold)
        {
            Logger.LogDebug(
                "ContinuationFetch: skip, {Remaining} items remain over threshold={Threshold} (index={Index} of {QueueCount}, source={SourceType})",
                remaining, threshold, currentIndex, session.NowPlayingQueue.Count, continuation.SourceType);
            return;
        }

        Logger.LogDebug(
            "ContinuationFetch: within prefetch window, fetching next batch (index={Index} of {QueueCount}, remaining={Remaining}, threshold={Threshold}, source={SourceType}, offset={StartIndex}/{Total})",
            currentIndex, session.NowPlayingQueue.Count, remaining, threshold, continuation.SourceType, continuation.StartIndex,
            QueueContinuationFetcher.RenderTotal(continuation.TotalCount)); // JF-753: the ONE renderer

        // JF-327 (the radio-path shape): the request funnel scopes a user CLONE;
        // GetUserById returns the unscoped config instance, so re-apply the device
        // library binding or the batch widens the scope the initial fetch ran under.
        Entities.User? pluginUser = Util.DeviceLibraryBindingResolver.ApplyByDevice(
            session.DeviceId, _config.GetUserById(session.UserId), _config, Logger);

        // Fetch the next batch
        IReadOnlyList<BaseItem> newItems = await RetryAsync(
            () => QueueContinuationFetcher.FetchNextBatch(
                continuation,
                _libraryManager,
                _userManager,
                Logger,
                pluginUser),
            "FetchContinuationBatch",
            cancellationToken).ConfigureAwait(false);

        if (newItems.Count == 0)
        {
            // No more items to fetch, remove continuation state
            QueueContinuationStore.Remove(session.UserId, deviceId);
            return;
        }

        // Append new items to the queue (deduplicating). JF-712: this pre-build
        // append is BY DESIGN (the class doc's exception list): the fetched batches
        // ARE the derivation input (ResolveNextItemId resolves the successor from
        // this queue view, and JF-666 requires the fetch before the precompute
        // early return), and the items belong to the queue the user asked to play,
        // not a synthesized continuation. JF-720: the idiom lives in the ONE
        // SessionQueue.AppendUnseen helper (a roster-pinned site).
        if (continuation.Shuffle)
        {
            newItems = Shuffler.ShuffleCopy(newItems);
        }

        SessionQueue.AppendUnseen(session, newItems);

        // Remove continuation if we've fetched everything
        if (continuation.StartIndex >= continuation.TotalCount)
        {
            QueueContinuationStore.Remove(session.UserId, deviceId);
        }
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
    /// JF-712 placement contract: every call site runs AFTER a successful launch
    /// build (derive-then-commit, the class doc), so a refused launch leaves the
    /// pointer on the finishing item with its position intact instead of a phantom
    /// next-at-0.
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
            queue.SetCurrentItemPointer(itemId);
            if (context.AudioPlayer != null
                && StreamTokenCodec.TryGetItemId(context.AudioPlayer.Token, out Guid finishingItemId)
                && StreamTokenCodec.TryGetItemId(itemId, out Guid nextItemId)
                && finishingItemId == nextItemId)
            {
                // JF-522: the refresh only survives for the SAME item (the wrapped /
                // repeat-one enqueue, where the pointer genuinely names the finishing
                // item): the context offset composes with that stream's launch base
                // and the store's item-absolute contract holds. A DIFFERENT next item
                // would misattribute the finishing item's position onto it, and the
                // resume tail now MINTS persisted values instead of dropping them, so
                // the unknown position resets to 0 (the next stop writes the real one;
                // a crash-resume in between starts the item from its beginning).
                queue.CurrentPositionTicks = Progress.ComposeEventPositionTicks(
                    deviceId, finishingItemId, context.AudioPlayer.OffsetInMilliseconds,
                    "PlaybackNearlyFinished", _queueManager, _libraryManager);
            }
            else
            {
                queue.CurrentPositionTicks = 0;
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
        // queue (see ProgressReporter.ResolvePlaybackOrder, the shared home this
        // resolver's decision and PlaybackFinishedEventHandler's keep-alive arm both
        // read). When reshuffledQueue is true the queue order IS the shuffle order,
        // so we advance sequentially (each track once); the random-pick fallback
        // below only runs for shuffle requested WITHOUT a physical reshuffle.
        var (playbackOrder, reshuffledQueue) = ProgressReporter.ResolvePlaybackOrder(session, context, _queueManager);

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
        // is honored (each track once) rather than re-randomized every step. The
        // admission guard is the ONE shared predicate (JF-691: the Finished
        // keep-alive arm evaluates the same one, so the two cannot drift).
        if (ProgressReporter.ShuffleRandomPickApplies(playbackOrder, reshuffledQueue, session.NowPlayingQueue.Count))
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
    /// Find similar tracks to the finishing item and DERIVE the continuation
    /// population for them (JF-712 derive-then-commit, the class doc): neither the
    /// session-queue append nor the RadioModeState arming happens here; both commit
    /// via <see cref="CommitPendingContinuation"/> only after the launch build
    /// succeeds, so a refused launch neither appends unplayed tracks nor arms radio
    /// continuation (RadioModeState.Enable is the worst phantom of the family: an
    /// armed-but-dead radio mode drives later continuation decisions). Shared by the
    /// radio arm and the PostPlay AutoPlay arm. The finishing item is the caller's
    /// ONE resolution (token-first with the session fallback); a non-Audio item
    /// no-ops via the cast.
    /// </summary>
    /// <param name="source">The arm's name (log label): "Radio mode" or "PostPlay AutoPlay".</param>
    /// <param name="armRadioMode">Whether the commit must arm RadioModeState (the PostPlay arm).</param>
    /// <param name="currentItem">The finishing item (seed source).</param>
    /// <param name="session">The current Jellyfin session (seed dedup scope + user).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pending population, or null when nothing new was found.</returns>
    private async Task<PendingContinuation?> DeriveSimilarTracksPopulation(
        string source,
        bool armRadioMode,
        BaseItem? currentItem,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        // JF-670 books never radio: the gate lives HERE, at the seed source (the
        // Audio cast is what makes books seedable; AudioBook derives from Audio on
        // both shipping refs, so without this gate a single-file book seeds genre
        // radio exactly like a song), not at the call sites, so no branch can
        // bypass it. Detection reads the finished item's own book shape, the
        // mechanism that survives the LAST continuation batch: the store entry is
        // long gone at true end-of-book, but the item still names its book.
        var currentAudio = currentItem as MediaBrowser.Controller.Entities.Audio.Audio;
        bool bookShaped = AudiobookItems.IsAudioBookOrChapter(currentItem, _libraryManager);
        Logger.LogDebug(
            "{Source} seed gate on '{FinishingItem}': {GateOutcome}",
            source,
            currentItem?.Name ?? "<unknown>",
            bookShaped ? "book-shaped, radio suppressed" : currentAudio == null ? "not an Audio item, nothing to seed from" : "seed allowed");
        if (currentAudio == null || bookShaped)
        {
            return null;
        }

        JellyfinUser? jellyfinUser = ResolveJellyfinUser(session);
        if (jellyfinUser == null)
        {
            return null;
        }

        // JF-327: re-fetch must not drop the device-library scope (the funnel scoped
        // a clone; GetUserById returns the unscoped config instance).
        Entities.User? pluginUser = Util.DeviceLibraryBindingResolver.ApplyByDevice(
            session.DeviceId, _config.GetUserById(session.UserId), _config, Logger);
        IReadOnlyList<BaseItem> similar = await Radio.FindRadioTracksAsync(currentAudio, jellyfinUser, pluginUser!, _libraryManager, cancellationToken).ConfigureAwait(false);

        if (similar.Count == 0)
        {
            Logger.LogInformation("{Source}: no similar tracks found for {ItemName}", source, currentAudio.Name);
            return null;
        }

        List<BaseItem> shuffled = Shuffler.ShuffleAndCap(similar, 15);

        // JF-720: the derive half of the append-unseen idiom
        // (SessionQueue.UnseenItems, pure read; a roster-pinned site); the append
        // itself waits for the commit.
        List<QueueItem> appended = SessionQueue.UnseenItems(session, shuffled);

        return appended.Count == 0
            ? null
            : new PendingContinuation(source, appended, armRadioMode);
    }

    /// <summary>
    /// JF-324 episode auto-advance: resolve the next episode of the finishing
    /// episode's series with ONE direct ordered-unplayed query scoped to the series
    /// (AncestorIds + IsPlayed=false, season-then-episode order) and derive the
    /// first candidate outside the skip-set (current + queued ids) as a pending
    /// single-item session-queue append for a gapless AudioPlayer transition
    /// (JF-712: the append commits only after the launch build succeeds).
    /// Movies and every non-Episode
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
    /// <returns>The pending population for the next episode's item ID (appended to the queue at commit), or null when the branch does not apply.</returns>
    private async Task<PendingContinuation?> TryAutoAdvanceNextEpisodeAsync(
        BaseItem? currentItem,
        SessionInfo session,
        Entities.User user,
        PostPlayBehavior postPlayMode,
        CancellationToken cancellationToken)
    {
        // Config gates first, before any library work (gate-before-query): the
        // PostPlay mode (passed in, the caller's ONE resolution), the
        // VideoPlaybackEnabled feature flag, and the VideosEnabled content gate
        // (JF-466 hard-zero contract) all skip the branch without touching the
        // library, mirroring the intent path. An event response cannot speak, so
        // a disabled configuration just skips the advance.
        if (postPlayMode != PostPlayBehavior.AutoPlay)
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

        // JF-712: the append is derived, not committed here (the class doc); the
        // NEXT advance still sees the successor in place on the success path, while
        // a refused launch leaves the queue without an episode that never played.
        Logger.LogInformation(
            "Episode auto-advance: resolved next episode '{NextEpisodeName}' after '{EpisodeName}' (gapless, no announcement)",
            next.Name,
            episode.Name);
        return new PendingContinuation(
            "Episode auto-advance",
            new List<QueueItem> { new() { Id = next.Id } },
            ArmRadioMode: false);
    }

    /// <summary>
    /// JF-712: the ONE commit point of the exhaustion arms' derived continuation,
    /// called only after the launch build succeeded (a refused launch throws before
    /// this runs; the policy and its exceptions live on the class doc). The append
    /// (JF-720: SessionQueue.AppendUnseen, whose derive half produced the pending
    /// population) RE-RUNS the membership dedup against the queue as it stands at
    /// commit time: Amazon multi-fires NearlyFinished as concurrent requests, and a
    /// sibling fire may have committed its own derived population inside this
    /// request's derive-to-commit window (which spans the whole launch build, far
    /// wider than the old write-at-derive window, whose whole-list REPLACE made the
    /// same race a benign last-write-wins); a blind AddRange would double-append.
    /// </summary>
    /// <param name="pending">The derived population to commit, or null (the plain queue-advance arms derive nothing).</param>
    /// <param name="session">The session whose queue the population appends to.</param>
    /// <param name="deviceId">The device the radio arming keys on.</param>
    private void CommitPendingContinuation(PendingContinuation? pending, SessionInfo session, string deviceId)
    {
        if (pending == null)
        {
            return;
        }

        // JF-720: the COMMIT half of the append-unseen idiom
        // (SessionQueue.AppendUnseen, a roster-pinned site); the re-run's
        // rationale lives on this method's doc, the race note in the helper's.
        int appendedCount = SessionQueue.AppendUnseen(session, pending.AppendedItems).Count;

        if (pending.ArmRadioMode)
        {
            RadioModeState.Enable(session.UserId, deviceId);
        }

        Logger.LogInformation(
            "{Source}: committed continuation after successful launch (appended {Count} session-queue items, radioArmed={RadioArmed})",
            pending.Source, appendedCount, pending.ArmRadioMode);
    }

    /// <summary>
    /// JF-712 derive-then-commit: an exhaustion arm's DERIVED but uncommitted
    /// continuation population: the session-queue items to append (deduplicated at
    /// derivation time, never empty), whether radio mode must be armed once the
    /// stream really launches, and the arm's name for the commit log. Committing is
    /// owned by <see cref="CommitPendingContinuation"/> (after a successful launch
    /// build); nothing in this type mutates any store at derivation time.
    /// </summary>
    /// <param name="Source">The deriving arm's name (log label).</param>
    /// <param name="AppendedItems">The session-queue items to append on commit (never empty).</param>
    /// <param name="ArmRadioMode">Whether commit must arm RadioModeState (the PostPlay arm).</param>
    private sealed record PendingContinuation(string Source, List<QueueItem> AppendedItems, bool ArmRadioMode)
    {
        /// <summary>
        /// Gets the first appended item's id: the item the launch builds. Structural
        /// guard (the JF-710 idiom): the derive arms must return NULL for an empty
        /// population, so an empty list here is a construction-site bug and fails
        /// with the contract named, not a distant index error.
        /// </summary>
        public Guid FirstNewId => AppendedItems.Count > 0
            ? AppendedItems[0].Id
            : throw new InvalidOperationException("PendingContinuation was constructed with an empty append list; a derive arm must return null instead (JF-712)");
    }
}
