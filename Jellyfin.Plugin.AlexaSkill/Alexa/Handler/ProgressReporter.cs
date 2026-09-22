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
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// The playback-progress and queue-answer family (JF-315 batch 10, census
/// cluster H, widened by JF-582 to the adjacent-queue-item serve):
/// the JF-522 raw-to-item-absolute position composition
/// (<see cref="ComposeItemAbsolutePosition"/>/<see cref="ComposeEventPositionTicks"/>
/// with the fail-open <see cref="TryGetRuntimeTicksForGuard"/>), the
/// SessionManager progress writers (<see cref="ReportPlaybackProgress"/>,
/// <see cref="ApplyRepeatModeAsync"/>, <see cref="ReportStopOrderedAsync"/>), the
/// post-play behavior resolution (<see cref="GetPostPlayBehavior"/>), and the
/// queue-order mirror (<see cref="MirrorQueueToSession"/>), moved verbatim from
/// BaseHandler, and the JF-574/JF-577 crash-recovery pair
/// (<see cref="TryRehydrateSessionQueueFromDevice"/> with its
/// <see cref="ResolveCurrentItemId"/> companion and the JF-582 combined
/// <see cref="TryRehydrateAndResolveCurrentItemId"/> entry), plus the JF-582
/// shared adjacent-queue-item serve (<see cref="ServeAdjacentQueueItem"/>) the
/// four next/previous entry points route through, and the JF-578 both-stores
/// queue-membership writer (<see cref="EnqueueToBothStores"/> with its combined
/// <see cref="RehydrateAndEnqueueToBothStores"/> entry) the queue-editing
/// intents route through. COMPOSITION, not per-handler injection (the PlaybackLaunchBuilder
/// batch-4 precedent): BaseHandler constructs one instance as the inherited
/// <c>Progress</c> property so the 61 handler ctors stay untouched. The real
/// dependencies are ctor-passed (session manager, config, logger, the Launch
/// collaborator for launch-base reads); the JF-522 per-call idiom is kept where
/// the moved members already used it (<see cref="ComposeEventPositionTicks"/>
/// takes the caller's queue/library managers per call). Stateless beyond those
/// ctor references, so the singleton-handler constraint is preserved.
/// Lives in the Handler namespace (the CrossMediaFallback/AlbumPlayService
/// precedent), not Util/Playback: its only cross-layer static references are
/// Cache/Playback/Locale types plus ONE in-namespace call
/// (<see cref="BaseHandler.GetLocalePublic"/>), so no Util-to-Handler or
/// Playback-to-Handler arrow is introduced.
/// </summary>
public sealed class ProgressReporter
{
    private readonly ISessionManager _sessionManager;
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private readonly PlaybackLaunchBuilder _launch;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressReporter"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager the progress writers report through.</param>
    /// <param name="config">The plugin configuration (post-play behavior resolution).</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="launch">The playback-launch collaborator (active launch-base reads).</param>
    public ProgressReporter(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILogger logger,
        PlaybackLaunchBuilder launch)
    {
        _sessionManager = sessionManager;
        _config = config;
        _logger = logger;
        _launch = launch;
    }

    /// <summary>
    /// JF-522 writer-side provenance, the ONE raw-to-item-absolute conversion shared by
    /// every playback-position writer: a device-reported offset counts the OUTPUT
    /// timeline of the stream that produced it, which for a transcode-routed launch
    /// starts at the stream's launch base, so the item-absolute position is
    /// <c>base + raw</c>, including a raw of 0 (a PlaybackStarted for a transcode
    /// launch carries directive offset 0 while the item genuinely plays from its
    /// base). Base 0 (raw-static launches, plain plays) and absent scopes (pre-deploy
    /// launches, cleared queue files) return the raw ticks unchanged: base 0 IS the
    /// item timeline, and an absent scope has no defensible base to add (persisting
    /// the raw value there is the conservative, pre-JF-522 behavior).
    /// Stale-base guard: when the item runtime is known, a composition STRICTLY past
    /// it is never persisted (the raw offset wins). A legitimate composition can land
    /// ON the runtime (a finish event reports the full remaining stream), but cannot
    /// pass it; past-runtime means the base no longer describes the stream that
    /// produced the offset (a same-item relaunch whose stop arrived late, the sleep
    /// re-issue corner, a promote that never paired).
    /// </summary>
    /// <param name="rawTicks">The device-reported offset in .NET ticks (stream-relative; 0 at a transcode stream's start).</param>
    /// <param name="launchBaseMs">The stream's ACTIVE launch base in milliseconds (0 when none).</param>
    /// <param name="runtimeTicks">The item's runtime in ticks when known, else null (guard skipped).</param>
    /// <param name="logLabel">Caller identity for the composition/guard log lines.</param>
    /// <returns>The item-absolute position in ticks.</returns>
    public long ComposeItemAbsolutePosition(long rawTicks, long launchBaseMs, long? runtimeTicks = null, string logLabel = "PlaybackEvent")
    {
        if (launchBaseMs <= 0)
        {
            return rawTicks;
        }

        long baseTicks = launchBaseMs * TimeSpan.TicksPerMillisecond;
        long composed = rawTicks + baseTicks;
        if (runtimeTicks is > 0 && composed > runtimeTicks.Value)
        {
            _logger.LogInformation(
                "{Label}: composed item-absolute position of {ComposedTicks} ticks (launch base {BaseMs}ms + raw {RawTicks} ticks) passes the item runtime ({RuntimeTicks} ticks); a legitimate composition cannot, so a stale launch scope is in play; persisting the raw offset as the more conservative truth",
                logLabel, composed, launchBaseMs, rawTicks, runtimeTicks.Value);
            return rawTicks;
        }

        _logger.LogDebug(
            "{Label}: persisting item-absolute position {ComposedTicks} ticks (launch base {BaseMs}ms + raw {RawTicks} ticks)",
            logLabel, composed, launchBaseMs, rawTicks);
        return composed;
    }

    /// <summary>
    /// JF-522: the ONE event-writer entry point for converting a device-reported raw
    /// offset into the item-absolute position - read the stream's ACTIVE launch base,
    /// look the item runtime up (fail-open, only when a base exists) for the
    /// stale-base guard, and compose. Every playback-position writer calls this
    /// instead of hand-assembling the triplet, so the "guard only when a base exists"
    /// policy and the null-to-0 collapse live in one place. An unparseable item id
    /// (<see cref="Guid.Empty"/>) keeps the raw ticks (no scope can be recorded for
    /// it). Callers without a library manager (the mode-change progress reports) skip
    /// the runtime guard - their positions are transient PlayState writes.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (launch-scope key).</param>
    /// <param name="itemId">The codec-parsed item id the event's token names.</param>
    /// <param name="rawOffsetMs">The device-reported offset in milliseconds.</param>
    /// <param name="logLabel">Caller identity for the composition/guard log lines.</param>
    /// <param name="queueManager">The caller's queue manager, or null to use <c>Plugin.Instance</c>'s.</param>
    /// <param name="libraryManager">Optional library manager for the runtime guard; null skips it.</param>
    /// <returns>The item-absolute position in ticks.</returns>
    public long ComposeEventPositionTicks(
        string? deviceId,
        Guid itemId,
        long rawOffsetMs,
        string logLabel,
        DeviceQueueManager? queueManager = null,
        ILibraryManager? libraryManager = null)
    {
        long launchBaseMs = itemId != Guid.Empty
            ? _launch.GetActiveLaunchBaseMs(deviceId, itemId.ToString(), queueManager) ?? 0
            : 0;
        long? runtimeTicks = launchBaseMs > 0 ? TryGetRuntimeTicksForGuard(libraryManager, itemId) : null;
        return ComposeItemAbsolutePosition(
            TimeSpan.FromMilliseconds(rawOffsetMs).Ticks, launchBaseMs, runtimeTicks, logLabel);
    }

    /// <summary>
    /// Fail-open runtime lookup feeding <see cref="ComposeItemAbsolutePosition"/>'s
    /// stale-base guard (JF-522): the guard is an advisory bound, so a library-manager
    /// failure must not kill an event handler before the keep-alive ack Amazon requires
    /// - it reads null (guard skipped) and logs.
    /// </summary>
    /// <param name="libraryManager">The library manager (null reads null).</param>
    /// <param name="itemId">The item whose runtime to read.</param>
    /// <returns>The item runtime in ticks, or null when unknown.</returns>
    public long? TryGetRuntimeTicksForGuard(ILibraryManager? libraryManager, Guid itemId)
    {
        if (libraryManager == null || itemId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return libraryManager.GetItemById(itemId)?.RunTimeTicks;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playback composition guard: runtime lookup failed for item {ItemId}; guard skipped", itemId);
            return null;
        }
    }

    /// <summary>
    /// Rebuilds <paramref name="session"/>'s <c>NowPlayingQueue</c> from a
    /// <see cref="Playback.DeviceQueue"/>'s current (possibly reshuffled) item
    /// order, preserving <c>PlaylistItemId</c> and other metadata on items that
    /// already exist. Used by the shuffle handlers so that
    /// <c>PlaybackNearlyFinishedEventHandler.ResolveNextItemId</c> advances
    /// through the shuffled order rather than the original one; the
    /// AlbumPlayService playlist flow calls it too (the shuffled-playlist branch,
    /// the caller it gained in JF-315 batch 8), which is why the member is a
    /// public static on this class rather than private to the shuffle handlers.
    /// </summary>
    /// <param name="queue">The device queue whose item order to mirror.</param>
    /// <param name="session">The Jellyfin session whose NowPlayingQueue to rebuild.</param>
    public static void MirrorQueueToSession(Playback.DeviceQueue queue, SessionInfo session)
    {
        if (queue.ItemIds.Count == 0)
        {
            return;
        }

        // Index existing queue items by Id (first occurrence wins) so metadata
        // (e.g. PlaylistItemId) is retained. Playlists may contain duplicate
        // tracks, so ToDictionary would throw; use TryAdd instead.
        var existing = new Dictionary<Guid, QueueItem>();
        foreach (QueueItem q in session.NowPlayingQueue)
        {
            existing.TryAdd(q.Id, q);
        }

        var deviceIds = new HashSet<Guid>();
        var rebuilt = new List<QueueItem>(queue.ItemIds.Count);
        foreach (string id in queue.ItemIds)
        {
            if (Guid.TryParse(id, out Guid guid))
            {
                deviceIds.Add(guid);
                rebuilt.Add(existing.TryGetValue(guid, out QueueItem? qi)
                    ? qi
                    : new QueueItem { Id = guid });
            }
        }

        // Preserve any session items not represented in the device queue (e.g.
        // progressive-continuation tracks) so the playable queue never shrinks.
        foreach (QueueItem qi in session.NowPlayingQueue)
        {
            if (!deviceIds.Contains(qi.Id))
            {
                rebuilt.Add(qi);
            }
        }

        session.NowPlayingQueue = rebuilt;
    }

    /// <summary>
    /// Companion to <see cref="TryRehydrateSessionQueueFromDevice"/>: resolves the
    /// current item for a queue-reading consumer. "Current" is the session's
    /// now-playing item; on the rehydrated (wiped) shape that item is null until
    /// the next server report, so the coherent token the guard just validated
    /// stands in for it. Second-adopter window (review finding, JF-577): another
    /// consumer (ListQueue, PlaybackNearlyFinished) may have rehydrated this
    /// session's queue earlier in the same wiped session, making leg 1 of the
    /// guard decline here while the now-playing item is STILL null; a token that
    /// is a MEMBER of the now-populated queue proves the same coherence the guard
    /// validates, so it stands in too. A token outside the queue changes nothing:
    /// non-rehydrated requests keep today's semantics exactly.
    /// </summary>
    /// <param name="session">The Jellyfin session (now-playing item first).</param>
    /// <param name="context">The Alexa context (stream token fallback).</param>
    /// <param name="rehydrated">Whether the guard rehydrated this session.</param>
    /// <returns>The current item id, or null when nothing is playing.</returns>
    public static Guid? ResolveCurrentItemId(SessionInfo session, Context context, bool rehydrated)
    {
        if (session.FullNowPlayingItem != null)
        {
            return session.FullNowPlayingItem.Id;
        }

        if (!StreamTokenCodec.TryGetItemId(context.AudioPlayer?.Token, out Guid tokenItemId))
        {
            return null;
        }

        return rehydrated || session.NowPlayingQueue.Any(q => q.Id == tokenItemId)
            ? tokenItemId
            : null;
    }

    /// <summary>
    /// JF-582: the combined rehydrate-and-resolve entry for the adjacent-item
    /// consumers. The guard and its current-item companion are an unsplittable
    /// pair there (the companion's token stand-in is only valid for a queue the
    /// guard just validated as coherent), and the rehydrated boolean the guard
    /// returns has no other consumer at those sites, so this helper couples the
    /// two calls and their hand-off cannot be dropped by a future call site.
    /// Guard-only consumers (ListQueue, PlaybackNearlyFinished) keep calling the
    /// two members directly.
    /// </summary>
    /// <param name="queueManager">The caller's per-device queue manager, or null (no rehydration).</param>
    /// <param name="session">The Jellyfin session whose queue to rehydrate and whose now-playing item to resolve.</param>
    /// <param name="context">The Alexa context (device id and current stream token).</param>
    /// <param name="logger">The caller's logger for the rehydration decision lines.</param>
    /// <param name="logLabel">Caller identity prefixing the log lines.</param>
    /// <returns>The current item id, or null when nothing is playing.</returns>
    private static Guid? TryRehydrateAndResolveCurrentItemId(
        DeviceQueueManager? queueManager,
        SessionInfo session,
        Context context,
        ILogger logger,
        string logLabel)
    {
        bool rehydrated = TryRehydrateSessionQueueFromDevice(queueManager, session, context, logger, logLabel);
        return ResolveCurrentItemId(session, context, rehydrated);
    }

    /// <summary>
    /// JF-582: the ONE adjacent-queue-item serve, shared by the four
    /// next/previous entry points (NextIntentHandler, PreviousIntentHandler, and
    /// the APL NowPlaying taps), which were shape-identical after the JF-577/579
    /// adoptions but had already drifted in three dimensions on the APL side (no
    /// JF-564 medium refusal, no JF-507 codec gate, no logging). Uniform gates, in
    /// order: the JF-564 VideoApp-medium refusal (<see cref="PlaybackLaunchBuilder.ResolvePlayingMedium"/>
    /// + <see cref="PlaybackLaunchBuilder.BuildVideoAppTransportRefusal"/>), the
    /// JF-577 rehydration guard + current-item resolution (through the combined
    /// <see cref="TryRehydrateAndResolveCurrentItemId"/>), the
    /// <see cref="SessionQueue.IndexOfQueueItem"/> scan with the per-direction
    /// edge bound, and the JF-507 codec-gated launch via
    /// <see cref="PlaybackLaunchBuilder.ResolveAudioLaunchSource"/> (an
    /// EAC3-family video successor routes to the audio-only transcode instead of
    /// the raw static bytes). The APL taps are customer-initiated requests while
    /// this skill was the most recently playing audio, so their
    /// <c>context.AudioPlayer</c> carries the playing token the guard reads.
    /// Response shapes: the refusal Tell, or the silent <c>Empty</c>, or the
    /// AudioPlayer.Play build (<c>ShouldEndSession=true</c> per JF-299, owned by
    /// the launch builder).
    /// </summary>
    /// <param name="queueManager">The caller's per-device queue manager (the JF-564 ledger and the rehydration source); null falls back to <c>Plugin.Instance</c>'s for the medium read and skips rehydration.</param>
    /// <param name="libraryManager">The library manager, to resolve the ledger item and the adjacent item.</param>
    /// <param name="session">The Jellyfin session (queue + now-playing).</param>
    /// <param name="context">The Alexa context (device id, AudioPlayer token).</param>
    /// <param name="user">The plugin user (static stream URL).</param>
    /// <param name="locale">The request locale, for the transport-refusal strings.</param>
    /// <param name="direction">Which adjacent item to serve.</param>
    /// <param name="logLabel">Caller identity prefixing the log lines.</param>
    /// <param name="tapOrigin">True when the caller is a screen TAP (APL UserEvent):
    /// the Video/LiveTv refusal arms answer with a silent Empty instead of the
    /// voice-phrased Tell, whose "use the touchscreen" wording tells a touch user to
    /// do what they just did (review finding, JF-582).</param>
    /// <returns>The refusal Tell (Empty on a tap origin), an <c>Empty</c> response (no adjacent item), or the AudioPlayer.Play response.</returns>
    internal SkillResponse ServeAdjacentQueueItem(
        DeviceQueueManager? queueManager,
        ILibraryManager libraryManager,
        SessionInfo session,
        Context context,
        Entities.User user,
        string locale,
        AdjacentQueueDirection direction,
        string logLabel,
        bool tapOrigin = false)
    {
        bool forward = direction == AdjacentQueueDirection.Next;
        string directionWord = forward ? "next" : "previous";
        _logger.LogDebug(
            "{Label}: entered, queueSize={QueueSize}, nowPlaying={NowPlayingId}",
            logLabel, session.NowPlayingQueue.Count, session.FullNowPlayingItem?.Id);

        // JF-564: during a VideoApp-family medium the queue logic below must not run
        // (the shared refusal helper owns the rationale); an empty ledger (Unknown)
        // keeps the music semantics unchanged.
        PlaybackLaunchBuilder.PlayingMedium medium = _launch.ResolvePlayingMedium(context, libraryManager, queueManager);
        if (PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(medium, locale) is { } refusal)
        {
            _logger.LogDebug("{Label}: {Medium} playing, answered by the transport refusal (silent={Silent})", logLabel, medium, tapOrigin);
            return tapOrigin ? ResponseBuilder.Empty() : refusal;
        }

        // JF-577: repair a restart/re-registration-wiped session queue before the
        // queue read (rationale on the shared helper): a coherent persisted device
        // queue turns the false "no more tracks" below into the real queue.
        Guid? currentItemId = TryRehydrateAndResolveCurrentItemId(queueManager, session, context, _logger, logLabel);

        // check if we have any media in the queue and the is currently something playing
        if (session.NowPlayingQueue.Count == 0 || currentItemId == null)
        {
            _logger.LogDebug("{Label}: empty queue or no now-playing item, returning Empty", logLabel);
            return ResponseBuilder.Empty();
        }

        // get the adjacent item in the queue, skipping the respective queue edge
        int idx = SessionQueue.IndexOfQueueItem(session, currentItemId.Value);
        bool hasAdjacent = forward
            ? idx >= 0 && idx < session.NowPlayingQueue.Count - 1
            : idx > 0;
        if (!hasAdjacent)
        {
            _logger.LogDebug(
                "{Label}: already at {QueueEdge} item in queue, returning Empty",
                logLabel, forward ? "last" : "first");
            return ResponseBuilder.Empty();
        }

        Guid adjacentItemId = session.NowPlayingQueue[forward ? idx + 1 : idx - 1].Id;
        string itemId = adjacentItemId.ToString();
        BaseItem? adjacentItem = libraryManager.GetItemById(adjacentItemId);
        if (adjacentItem == null)
        {
            _logger.LogDebug(
                "{Label}: {Direction} item {ItemId} not found in library, returning Empty",
                logLabel, directionWord, adjacentItemId);
            return ResponseBuilder.Empty();
        }

        session.FullNowPlayingItem = adjacentItem;

        _logger.LogDebug(
            "{Label}: playing {Direction} item '{ItemName}' ({ItemId})",
            logLabel, directionWord, adjacentItem.Name, adjacentItemId);

        // JF-507: codec-gated audio-launch decision; an EAC3-family video item in
        // the queue routes to the audio-only transcode instead of dying on the raw
        // static bytes (JF-505 does not apply: this launch is audio-shaped).
        AudioLaunchSource source = _launch.ResolveAudioLaunchSource(adjacentItem, itemId, user, 0);
        return _launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source, itemId, adjacentItem, user, context);
    }

    /// <summary>
    /// Which adjacent queue item a next/previous entry point serves (JF-582).
    /// </summary>
    internal enum AdjacentQueueDirection
    {
        /// <summary>The item after the current one; the queue's LAST item is the edge.</summary>
        Next,

        /// <summary>The item before the current one; the queue's FIRST item is the edge.</summary>
        Previous
    }

    /// <summary>
    /// JF-574 (hoisted JF-577): rehydrates <paramref name="session"/>'s
    /// <c>NowPlayingQueue</c> from the persisted per-device queue when a restart or a
    /// mid-playback session re-registration wiped the in-memory queue (the
    /// crash-recovery path; the device queue file is the only queue store that survives
    /// a restart). Mirrors through <see cref="MirrorQueueToSession"/> so the queue's
    /// current order (a physical shuffle included) is what the session carries, and the
    /// caller's queue read proceeds unchanged on a queue that reflects reality (for
    /// <c>PlaybackNearlyFinishedEventHandler</c> that includes TRUE exhaustion and
    /// PostPlay). Called at entry by every session-queue consumer that can instead
    /// answer from the surviving queue (Next/Previous/ListQueue).
    /// COHERENCE GUARD, both legs required: (1) the session queue must be EMPTY - a
    /// non-empty session queue belongs to a live playback (a fresh single-song play
    /// sets its own one-item queue), so an older persisted queue must never extend
    /// it; (2) the persisted queue must CONTAIN the item the device is actually
    /// playing (the AudioPlayer token parsed through the shared codec, so composite
    /// sleep tokens resolve too): a queue whose members do not include the playing
    /// item describes a different, older playback session, and rehydrating from it
    /// would hijack the current playback. Membership, not the persisted
    /// <c>CurrentIndex</c>/<c>CurrentItemId</c> pointer, is the coherence signal
    /// because those pointers can lag the token across a restart, while a stream
    /// playing an item that is a member of the persisted queue proves that queue is
    /// the one this playback was enqueued from.
    /// </summary>
    /// <param name="queueManager">The caller's per-device queue manager, or null (no rehydration).</param>
    /// <param name="session">The Jellyfin session whose queue to rehydrate.</param>
    /// <param name="context">The Alexa context (device id and current stream token).</param>
    /// <param name="logger">The caller's logger for the rehydration decision lines.</param>
    /// <param name="logLabel">Caller identity prefixing the log lines.</param>
    /// <returns>True when the session queue was rehydrated from the device queue.</returns>
    public static bool TryRehydrateSessionQueueFromDevice(
        DeviceQueueManager? queueManager,
        SessionInfo session,
        Context context,
        ILogger logger,
        string logLabel)
    {
        string? currentToken = context.AudioPlayer?.Token;
        if (queueManager == null
            || session.NowPlayingQueue.Count != 0
            || !StreamTokenCodec.TryGetItemId(currentToken, out Guid currentItemId))
        {
            return false;
        }

        Playback.DeviceQueue? deviceQueue = queueManager.GetQueue(context.GetDeviceId());
        if (deviceQueue == null || deviceQueue.ItemIds.Count == 0)
        {
            return false;
        }

        // Snapshot the member list: GetQueue returns the live instance and SetQueue
        // can replace it concurrently (a voice play racing this request), so mirror
        // from an immutable copy.
        List<string> queuedIds = deviceQueue.ItemIds.ToList();
        foreach (string queuedId in queuedIds)
        {
            if (Guid.TryParse(queuedId, out Guid parsed) && parsed == currentItemId)
            {
                MirrorQueueToSession(deviceQueue, session);
                logger.LogInformation(
                    "{Label}: session queue empty (restart or re-registration wiped it) but the persisted device queue coherently contains the playing item; rehydrated {Count} items from the device queue",
                    logLabel, session.NowPlayingQueue.Count);
                return true;
            }
        }

        logger.LogDebug(
            "{Label}: session queue empty and a persisted device queue exists, but it does not contain the playing item {ItemId} (stale queue from an older playback); not rehydrating",
            logLabel, currentItemId);
        return false;
    }

    /// <summary>
    /// JF-578: the both-stores queue-membership writer the queue-editing intents
    /// (AddToQueue, PlayNext) route through, next to the mirror it composes with
    /// (the JF-574 layering: the Playback store owns the durable mutation, this
    /// layer owns the session view).
    /// COHERENCE CONTRACT: the add lands in BOTH stores in one call. The durable
    /// device store takes the insert (<see cref="DeviceQueueManager.Enqueue"/>:
    /// ItemIds insertion, CurrentIndex bookkeeping, debounced persist; the only
    /// queue store that survives a restart), and the session queue is brought to
    /// the same view. The session leg MIRRORS from the mutated device queue
    /// (<see cref="MirrorQueueToSession"/>; the device order is authoritative,
    /// JF-447) when both stores were populated before the add, so the session's
    /// physical order (including any shuffle) and the persisted order cannot drift
    /// apart across the write; session-only items (progressive continuation
    /// tracks) ride the tail exactly as on every other mirror, which is also the
    /// established consequence of the shuffle handlers' mirror. In the degenerate
    /// shapes the session leg mutates the session queue DIRECTLY instead (the
    /// pre-JF-578 session-only behavior): a missing or empty device store cannot
    /// be an order authority (the mirror would move the added item in front of
    /// the session's own queue), and an EMPTY session queue must not be filled by
    /// the mirror (that is rehydration, a decision the guard in
    /// <see cref="RehydrateAndEnqueueToBothStores"/> has already made, and
    /// possibly declined, on coherence grounds; the add then seeds the session
    /// queue alone while the device store takes the insert for the next restart).
    /// </summary>
    /// <param name="queueManager">The caller's per-device queue manager, or null
    /// (session-only write: today's behavior, the handlers' optional dependency).</param>
    /// <param name="session">The Jellyfin session whose NowPlayingQueue to write.</param>
    /// <param name="deviceId">The Alexa device ID (the device-store key).</param>
    /// <param name="itemId">The item to enqueue.</param>
    /// <param name="placement">End (the AddToQueue ask) or AfterCurrent (PlayNext).</param>
    /// <param name="currentItemId">The caller's resolved current item (positions the
    /// AfterCurrent insert and the direct session leg's fallback).</param>
    private static void EnqueueToBothStores(
        DeviceQueueManager? queueManager,
        SessionInfo session,
        string deviceId,
        Guid itemId,
        DeviceQueueManager.QueueInsertPlacement placement,
        Guid? currentItemId)
    {
        // Read the pre-state before the write: a post-write store holding exactly
        // one item cannot distinguish a seed from a one-item queue.
        bool deviceHadQueue = queueManager?.GetQueue(deviceId) is { ItemIds.Count: > 0 };
        bool sessionHasQueue = session.NowPlayingQueue.Count > 0;

        queueManager?.Enqueue(deviceId, itemId, placement, currentItemId);

        // Re-fetch for the mirror rather than reusing the pre-write reference:
        // Enqueue mutates in place, but a concurrent SetQueue may have swapped the
        // instance (the guard's snapshot idiom). A concurrent Clear can also have
        // removed it entirely; the session-only fallback then keeps the write at
        // today's pre-JF-578 semantics instead of dereferencing a corpse.
        // Coherence gate (review BLOCKER, JF-578): mirror ONLY when the mutated
        // device queue still describes THIS playback, i.e. it contains the resolved
        // current item. A stale persisted queue that does not (a leftover album
        // queue behind a fresh single-song play, which never calls SetQueue) would
        // otherwise be mirrored over the live session queue, parking the playing
        // song last and ending the play - the exact hijack the JF-574 guard exists
        // to prevent. The direct session insert is the fallback that already exists
        // for exactly this shape.
        if (deviceHadQueue
            && sessionHasQueue
            && currentItemId != null
            && queueManager!.GetQueue(deviceId) is { } mutatedQueue
            && mutatedQueue.ItemIds.Contains(currentItemId.Value.ToString()))
        {
            MirrorQueueToSession(mutatedQueue, session);
            return;
        }

        InsertIntoSessionQueue(session, itemId, placement, currentItemId);
    }

    /// <summary>
    /// JF-578: the queue-editing intents' ONE entry point, coupling the writer's
    /// unsplittable prelude (the JF-582 combined rehydrate-and-resolve lesson):
    /// repair a restart-wiped session queue first (the JF-574/JF-577 guard,
    /// rationale on <see cref="TryRehydrateSessionQueueFromDevice"/>), resolve the
    /// current item (now-playing item first; the coherent token stands in on the
    /// wiped shape, see <see cref="ResolveCurrentItemId"/>), THEN enqueue into
    /// both stores with that resolution positioning the insert. The resolved
    /// current item is returned because the callers branch on it: null means
    /// genuinely nothing is playing (no now-playing item and no coherent token),
    /// the only shape where the start-playback ReplaceAll launch is correct; on
    /// the rehydrated shape there IS a current item (the playing token), so the
    /// add lands behind the live stream instead of replacing it.
    /// </summary>
    /// <param name="queueManager">The caller's per-device queue manager, or null (session-only write).</param>
    /// <param name="session">The Jellyfin session whose queue to repair and write.</param>
    /// <param name="context">The Alexa context (device id and current stream token).</param>
    /// <param name="itemId">The item to enqueue.</param>
    /// <param name="placement">End (the AddToQueue ask) or AfterCurrent (PlayNext).</param>
    /// <param name="logger">The caller's logger for the rehydration decision lines.</param>
    /// <param name="logLabel">Caller identity prefixing the log lines.</param>
    /// <returns>The resolved current item id, or null when nothing is playing.</returns>
    internal static Guid? RehydrateAndEnqueueToBothStores(
        DeviceQueueManager? queueManager,
        SessionInfo session,
        Context context,
        Guid itemId,
        DeviceQueueManager.QueueInsertPlacement placement,
        ILogger logger,
        string logLabel)
    {
        bool rehydrated = TryRehydrateSessionQueueFromDevice(queueManager, session, context, logger, logLabel);
        Guid? currentItemId = ResolveCurrentItemId(session, context, rehydrated);
        EnqueueToBothStores(queueManager, session, context.GetDeviceId(), itemId, placement, currentItemId);
        return currentItemId;
    }

    /// <summary>
    /// The session-leg fallback of <see cref="EnqueueToBothStores"/> for the
    /// degenerate shapes (no populated device queue to mirror from, or an empty
    /// session queue the guard declined to rehydrate): the pre-JF-578 session-only
    /// mutation, kept verbatim so those shapes behave exactly as before. End
    /// appends; AfterCurrent inserts behind the resolved current item, or at the
    /// front when there is none or it is not queued (the former
    /// <c>PlayNextIntentHandler.InsertAfterCurrent</c> shape).
    /// </summary>
    private static void InsertIntoSessionQueue(SessionInfo session, Guid itemId, DeviceQueueManager.QueueInsertPlacement placement, Guid? currentItemId)
    {
        var queue = new List<QueueItem>(session.NowPlayingQueue);
        var entry = new QueueItem { Id = itemId };
        // The SAME three-way policy as the device leg, resolved through
        // DeviceQueueManager.ResolveInsertPosition so the two legs cannot drift
        // (review finding R1).
        int insertIndex = DeviceQueueManager.ResolveInsertPosition(
            placement,
            queue.Count,
            currentItemId != null ? SessionQueue.IndexOfQueueItem(session, currentItemId.Value) : -1);
        queue.Insert(insertIndex, entry);

        session.NowPlayingQueue = queue;
    }

    /// <summary>
    /// Reports playback progress to Jellyfin so the session PlayState (and the
    /// dashboard UI) stays in sync with the plugin's view. Shared by the shuffle
    /// handlers, which differ only in the <paramref name="order"/> they report.
    /// </summary>
    /// <param name="session">The Jellyfin session to report on.</param>
    /// <param name="deviceId">The Alexa device ID (launch-scope key). NOT the session's
    /// own DeviceId, which is the constant "AlexaDevice" the controller authenticates
    /// under and never keys a launch scope (review JF-522).</param>
    /// <param name="itemId">The currently-playing item ID.</param>
    /// <param name="offsetMs">The current playback offset in milliseconds.</param>
    /// <param name="order">The playback order to report (Shuffle or Default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the async progress report.</returns>
    public async Task ReportPlaybackProgress(SessionInfo session, string deviceId, Guid itemId, long offsetMs, PlaybackOrder order, CancellationToken cancellationToken)
    {
        // JF-522: PlayState positions are item-absolute; the context-derived offset the
        // shuffle handlers pass composes with the playing stream's launch base (0 for
        // every non-transcode-routed queue, where this is a numeric no-op).
        long positionTicks = ComposeEventPositionTicks(deviceId, itemId, offsetMs, "PlaybackProgress");
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            RepeatMode = session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone,
            PositionTicks = positionTicks,
            PlaybackOrder = order,
        };

        await _sessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared body of the loop-mode intents (LoopOn/LoopOff/LoopSongOn, JF-450):
    /// attaches the given repeat mode to the currently playing item via the
    /// session manager's progress reporting. The intent can arrive from
    /// an open session with nothing playing: there is no item to attach the mode
    /// to, so the localized no-media tell is returned instead of throwing.
    /// </summary>
    /// <param name="request">The skill request (locale source for the no-media tell).</param>
    /// <param name="context">The context of the skill intent request (AudioPlayer token).</param>
    /// <param name="session">The session instance to report progress on.</param>
    /// <param name="mode">The repeat mode to apply.</param>
    /// <param name="label">Log label identifying the calling intent.</param>
    /// <returns>The spoken repeat-mode confirmation Tell, or the no-media tell when nothing is playing.</returns>
    public async Task<SkillResponse> ApplyRepeatModeAsync(Request request, Context context, SessionInfo session, RepeatMode mode, string label)
    {
        PlaybackState? requestState = context.AudioPlayer;

        _logger.LogDebug("{Label}: entered, token={Token}, offset={OffsetMs}ms", label, requestState?.Token, requestState?.OffsetInMilliseconds);

        // The intent can arrive from an open session with nothing playing: there is
        // no item to attach the repeat mode to. Composite sleep tokens
        // ("{guid}|sleep:{ticks}") must resolve too; raw Guid.TryParse fails them.
        if (requestState?.Token == null || !StreamTokenCodec.TryGetItemId(requestState.Token, out Guid itemId))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", BaseHandler.GetLocalePublic(request)));
        }

        long positionTicks = ComposeEventPositionTicks(
            context?.System?.Device?.DeviceID, itemId, requestState.OffsetInMilliseconds, "LoopMode");
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            PlaybackOrder = session.PlayState.PlaybackOrder,
            PositionTicks = positionTicks,
            RepeatMode = mode,
        };

        await _sessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);

        // Confirm aloud (live incident 2026-09-22): the repeat mode landed server-side
        // but the response was speech-less, which reads on-device as "the command did
        // nothing" (battery test 5: LoopAllOn answered with a bare session end twice).
        string confirmKey = mode switch
        {
            RepeatMode.RepeatAll => "RepeatAllEnabled",
            RepeatMode.RepeatOne => "RepeatSongEnabled",
            _ => "RepeatDisabled",
        };
        return ResponseBuilder.Tell(ResponseStrings.Get(confirmKey, BaseHandler.GetLocalePublic(request)));
    }

    /// <summary>
    /// JF-425/JF-447: the ONE stop-report sequence shared by the stop-shaped event
    /// handlers (PlaybackStopped/Finished/Failed). Registers the stop for correction
    /// duty (the displacement classification is folded into RecordStop, so a null
    /// registration means the stop displaces an already-replaced stream and must not
    /// correct anything), reports it to the server with the registration completed in a
    /// finally (a correcting start report waits for it instead of firing a concurrent
    /// duplicate), and restores the new track's session entry when the stop was a
    /// displacement (its own server-side write cleared the entry the new track owns).
    /// Callers that need the displacement flag BEFORE building the stop info (Stopped
    /// zeroes the saved position) classify early and may pass their own reason.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (per-device ordering key).</param>
    /// <param name="rawToken">The event's raw stream token, for the displacement classification.</param>
    /// <param name="stopInfo">The stop report to send (replayed verbatim as the correction).</param>
    /// <param name="displacementRestoreReason">Reason stamped into the classification and restore logs.</param>
    /// <returns>A task representing the report and, for a displacement, the restore.</returns>
    public async Task ReportStopOrderedAsync(string deviceId, string? rawToken, PlaybackStopInfo stopInfo, string displacementRestoreReason)
    {
        Playback.PlaybackReportOrdering.StopRegistration? registration =
            Playback.PlaybackReportOrdering.RecordStop(deviceId, rawToken, stopInfo);

        bool isDisplacement = registration == null;
        if (isDisplacement)
        {
            _logger.LogDebug(
                "{Reason}: displacement detected, item={Token} but the device's latest start is a different item; not recording the stop",
                displacementRestoreReason, rawToken);
        }

        try
        {
            await _sessionManager.OnPlaybackStopped(stopInfo).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            // JF-477: the session no longer exists in the SessionManager (it was removed
            // while our cached live reference kept pointing at it). Drop the device's
            // cached entries so the next request refetches (and Jellyfin re-registers)
            // instead of reusing the corpse, then preserve today's propagation.
            SessionReferenceCache.InvalidateDevice(deviceId);
            throw;
        }
        finally
        {
            registration?.MarkReportCompleted();
        }

        if (isDisplacement)
        {
            await Playback.PlaybackReportOrdering.RestoreCurrentStartAsync(
                _sessionManager, deviceId, _logger, displacementRestoreReason).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the effective post-play behavior for a user, falling back to the global default.
    /// Per-user setting (when explicitly set, i.e. non-null) takes precedence.
    /// </summary>
    /// <param name="user">The plugin user (per-user override), or null for the global default.</param>
    /// <returns>The effective post-play behavior.</returns>
    public PostPlayBehavior GetPostPlayBehavior(Entities.User? user)
    {
        if (user?.PostPlayBehavior is { } userBehavior)
        {
            _logger.LogDebug("PostPlayBehavior: user={UserId} mode={Mode} source=PerUser", user.Id, userBehavior);
            return userBehavior;
        }

        _logger.LogDebug("PostPlayBehavior: user={UserId} mode={Mode} source=GlobalDefault", user?.Id, _config.DefaultPostPlayBehavior);
        return _config.DefaultPostPlayBehavior;
    }
}
