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
using Jellyfin.Plugin.AlexaSkill.Alexa.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaybackStopped events.
/// Saves the last playback position and item to DeviceQueue for resume-after-pause recovery.
/// Also persists real position to ItemPositionState to bypass Jellyfin's MinAudiobookResume
/// threshold, and overwrites Jellyfin's UserData for cross-client consistency.
/// </summary>
#pragma warning disable CA1711
public class PlaybackStoppedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly DeviceQueueManager _queueManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStoppedEventHandler"/> class.
    /// </summary>
    public PlaybackStoppedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        DeviceQueueManager queueManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager) : base(sessionManager, config, loggerFactory)
    {
        _queueManager = queueManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackStopped;
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;
        string device = context.GetDeviceId();

        long realPositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks;

        Logger.LogInformation(
            "PlaybackStopped: item={Token}, offset={OffsetMs}ms, playerActivity={Activity}",
            req.Token, req.OffsetInMilliseconds, context.AudioPlayer?.PlayerActivity);

        // JF-393 diagnostic interaction logging: report elapsed since playback start and
        // clear the marker (displacement transitions re-record on the next PlaybackStarted).
        if (InteractionDiagnostics.IsEnabled(user, _config))
        {
            string diagDevice = device;
            Logger.LogInformation(
                "[diag] playback stopped: device={DeviceId} item={Token} sincePlaybackStarted={SinceStart}s",
                diagDevice,
                req.Token,
                InteractionDiagnostics.SincePlaybackStarted(diagDevice)?.ToString("F1", CultureInfo.InvariantCulture) ?? "n/a");
            InteractionDiagnostics.RecordPlaybackStopped(diagDevice);
        }

        // Detect displacement events (JF-447: against the device's latest START, not the
        // device queue, because several play paths never populate the queue): when a new
        // AudioPlayer.Play replaces the current track, Alexa sends PlaybackStopped for the
        // OLD item with a near-zero offset from the new track's start. This would overwrite
        // the real saved position of the old item.
        var queue = _queueManager.GetOrCreateQueue(device);
        bool isDisplacement = PlaybackReportOrdering.IsDisplacementStop(device, req.Token);
        if (isDisplacement)
        {
            Logger.LogWarning(
                "PlaybackStopped: displacement detected, stopped item={StoppedToken} but the device's latest start is a different item. " +
                "Saving with offset=0 to avoid overwriting real progress.",
                req.Token);
        }

        long positionTicks = isDisplacement ? 0 : realPositionTicks;

        // JF-447: composite sleep-timer tokens parse through the shared codec; the raw
        // new Guid(token) threw FormatException on them, killing this handler before the
        // ordering registration and before the response Amazon requires.
        StreamTokenCodec.TryGetItemId(req.Token, out Guid stopItemId);

        // JF-447 review hardening (event-order race): the classification above reads the
        // device's LATEST START, which is only written once PlaybackStarted(new) has been
        // PROCESSED; a displacement stop arriving before that start classifies REAL and
        // its near-zero offset would overwrite the old item's saved position (the
        // 4ab4704b class). The device queue pointer is directive-time truth: the play
        // path sets CurrentItemId when it issues the AudioPlayer.Play directive, strictly
        // before the old stream's stop can arrive. When the pointer names a DIFFERENT
        // item than the event token AND the stopped item is still in the queue, the
        // near-zero offset is suspect and the position overwrite below is skipped (the
        // stop report itself still goes out with unchanged semantics). Play paths that
        // never populate the queue (PlaySongIntentHandler) leave the pointer empty or a
        // queue that does not contain the token, so they stay unaffected.
        string cleanItemId = stopItemId != Guid.Empty ? stopItemId.ToString() : req.Token;
        bool queueContradictsEventToken =
            !isDisplacement
            && stopItemId != Guid.Empty
            && !string.IsNullOrEmpty(queue.CurrentItemId)
            && !string.Equals(queue.CurrentItemId, cleanItemId, StringComparison.OrdinalIgnoreCase)
            && queue.ItemIds.Contains(cleanItemId, StringComparer.OrdinalIgnoreCase);
        if (queueContradictsEventToken)
        {
            Logger.LogWarning(
                "PlaybackStopped: queue pointer={CurrentItemId} contradicts stopped item={Token} (item still queued); " +
                "treating offset={OffsetMs}ms as a suspect displacement (Started for the new item not processed yet), skipping the position overwrite",
                queue.CurrentItemId, req.Token, req.OffsetInMilliseconds);
        }

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = stopItemId,
            PositionTicks = positionTicks,
        };

        Logger.LogDebug(
            "PlaybackStopped: saving to server, item={Token}, offsetMs={OffsetMs}, ticks={Ticks}, sessionId={SessionId}, displacement={IsDisplacement}",
            req.Token, req.OffsetInMilliseconds, playbackStopInfo.PositionTicks, session.Id, isDisplacement);

        // JF-425/JF-447: register BEFORE reporting so any still in-flight playback-start
        // report is already superseded; a displacement stop never gains correction duty
        // (folded into RecordStop) and its write's clearing of the new track's entry is
        // undone. The displacement flag is classified EARLY above (zeroed position).
        await ReportStopOrderedAsync(
            device, req.Token, playbackStopInfo, "displacement stop cleared the new track's entry").ConfigureAwait(false);

        Logger.LogInformation(
            "PlaybackStopped: saved to server, item={Token}, position={PositionTicks} ticks",
            req.Token, playbackStopInfo.PositionTicks);

        // Save playback position to DeviceQueue for resume-after-pause recovery.
        if (!isDisplacement && !queueContradictsEventToken && !string.IsNullOrEmpty(req.Token))
        {
            // JF-424.1 observation, covered by the JF-447 trust sweep: the position store
            // stays UNCONDITIONAL (single-item plays have no queue for MoveTo to succeed
            // on, and resume-after-pause depends on it), but when the item IS in the queue
            // the index pointer moves with it instead of desyncing from CurrentItemId.
            // JF-447 composite case: MoveTo and CurrentItemId take the codec-parsed CLEAN
            // id, mirroring UpdateRecoveryPointer's same-id-to-both shape. A raw composite
            // sleep token never matches the queue's bare-GUID ItemIds (MoveTo silently
            // fails) and would park a suffixed pointer that no reader compares equal.
            _queueManager.MoveTo(device, cleanItemId);
            queue.CurrentPositionTicks = realPositionTicks;
            queue.CurrentItemId = cleanItemId;
            Logger.LogDebug(
                "Saved playback position to DeviceQueue: device={DeviceId}, item={ItemId}, offset={OffsetMs}ms",
                device, req.Token, req.OffsetInMilliseconds);
        }

        // Persist real position to ItemPositionState (bypasses Jellyfin's MinAudiobookResume)
        // and overwrite Jellyfin UserData for cross-client sync (web/mobile see correct position).
        if (!isDisplacement && !queueContradictsEventToken && realPositionTicks > 0 && stopItemId != Guid.Empty)
        {
            // 1. Save to plugin's per-item state (normalize key to "N" format to match readers)
            string positionKey = stopItemId.ToString("N");
            queue.ItemPositionState[positionKey] = realPositionTicks;

            // Evict stale entries when the dictionary grows beyond cap
            TrimItemPositionState(queue);

            _queueManager.SchedulePersist(device);
            Logger.LogDebug(
                "Saved to ItemPositionState: item={ItemId}, ticks={Ticks}",
                req.Token, realPositionTicks);

            // 2. Overwrite Jellyfin UserData with real position for cross-client sync
            //    OnPlaybackStopped → UpdatePlayState may have zeroed it (MinAudiobookResume).
            try
            {
                var item = _libraryManager.GetItemById(stopItemId);
                if (item != null)
                {
                    var jellyfinUser = _userManager.GetUserById(user.Id);
                    if (jellyfinUser != null)
                    {
                        var data = _userDataManager.GetUserData(jellyfinUser, item);
                        if (data != null && data.PlaybackPositionTicks == 0 && !data.Played)
                        {
                            data.PlaybackPositionTicks = realPositionTicks;
                            _userDataManager.SaveUserData(jellyfinUser, item, data, UserDataSaveReason.PlaybackProgress, CancellationToken.None);
                            Logger.LogDebug(
                                "Overwrote Jellyfin UserData position for cross-client sync: item={ItemId}, ticks={Ticks}",
                                req.Token, realPositionTicks);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex,
                    "Failed to overwrite UserData for cross-client sync: item={ItemId}",
                    req.Token);
            }
        }

        // End the session to dismiss the APL screen when playback stops
        // (user pause/stop, system stop, or error). Keep alive only for
        // displacement events where a new track is already starting.
        if (isDisplacement)
        {
            return BuildKeepAliveResponse();
        }

        Logger.LogInformation("PlaybackStopped: ending session to dismiss APL screen");
        return BuildEndSessionResponse();
    }

    private const int MaxItemPositionStateEntries = 200;

    /// <summary>
    /// Evicts entries from ItemPositionState that are not in the current queue
    /// when the dictionary exceeds the cap. This prevents unbounded growth.
    /// </summary>
    private static void TrimItemPositionState(DeviceQueue queue)
    {
        if (queue.ItemPositionState.Count <= MaxItemPositionStateEntries)
        {
            return;
        }

        HashSet<string> queuedItems = new(queue.ItemIds, StringComparer.OrdinalIgnoreCase);
        List<string> keysToRemove = new();
        foreach (var kvp in queue.ItemPositionState)
        {
            if (!queuedItems.Contains(kvp.Key))
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        // Remove oldest non-queued entries until under cap
        int toRemove = queue.ItemPositionState.Count - MaxItemPositionStateEntries;
        foreach (string key in keysToRemove.Take(toRemove))
        {
            queue.ItemPositionState.Remove(key);
        }
    }
}
