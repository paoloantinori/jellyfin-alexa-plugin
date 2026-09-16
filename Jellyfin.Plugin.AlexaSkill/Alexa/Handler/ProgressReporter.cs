using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// The playback-progress reporting family (JF-315 batch 10, census cluster H):
/// the JF-522 raw-to-item-absolute position composition
/// (<see cref="ComposeItemAbsolutePosition"/>/<see cref="ComposeEventPositionTicks"/>
/// with the fail-open <see cref="TryGetRuntimeTicksForGuard"/>), the
/// SessionManager progress writers (<see cref="ReportPlaybackProgress"/>,
/// <see cref="ApplyRepeatModeAsync"/>, <see cref="ReportStopOrderedAsync"/>), the
/// post-play behavior resolution (<see cref="GetPostPlayBehavior"/>), and the
/// queue-order mirror (<see cref="MirrorQueueToSession"/>), moved verbatim from
/// BaseHandler. COMPOSITION, not per-handler injection (the PlaybackLaunchBuilder
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
    /// <returns>An empty response, or the no-media tell when nothing is playing.</returns>
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

        return ResponseBuilder.Empty();
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
