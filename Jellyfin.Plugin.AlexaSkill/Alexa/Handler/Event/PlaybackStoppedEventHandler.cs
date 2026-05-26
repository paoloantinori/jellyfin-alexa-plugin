using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
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

        long realPositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks;

        Logger.LogInformation(
            "PlaybackStopped: item={Token}, offset={OffsetMs}ms, playerActivity={Activity}",
            req.Token, req.OffsetInMilliseconds, context.AudioPlayer?.PlayerActivity);

        // Detect displacement events: when a new AudioPlayer.Play replaces the current track,
        // Alexa sends PlaybackStopped for the OLD item with a near-zero offset from the new
        // track's start. This would overwrite the real saved position of the old item.
        bool isDisplacement = false;
        var queue = _queueManager.GetOrCreateQueue(context.System.Device.DeviceID);
        string? expectedItemId = null;
        if (queue.CurrentIndex >= 0 && queue.CurrentIndex < queue.ItemIds.Count)
        {
            expectedItemId = queue.ItemIds[queue.CurrentIndex];
        }

        if (!string.IsNullOrEmpty(expectedItemId) &&
            !string.Equals(expectedItemId, req.Token, StringComparison.OrdinalIgnoreCase))
        {
            isDisplacement = true;
            Logger.LogWarning(
                "PlaybackStopped: displacement detected — stopped item={StoppedToken} but queue expects={QueueToken}. " +
                "Saving with offset=0 to avoid overwriting real progress.",
                req.Token, expectedItemId);
        }

        long positionTicks = isDisplacement ? 0 : realPositionTicks;

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(req.Token),
            PositionTicks = positionTicks,
        };

        Logger.LogDebug(
            "PlaybackStopped: saving to server — item={Token}, offsetMs={OffsetMs}, ticks={Ticks}, sessionId={SessionId}, displacement={IsDisplacement}",
            req.Token, req.OffsetInMilliseconds, playbackStopInfo.PositionTicks, session.Id, isDisplacement);

        await SessionManager.OnPlaybackStopped(playbackStopInfo).ConfigureAwait(false);

        Logger.LogInformation(
            "PlaybackStopped: saved to server — item={Token}, position={PositionTicks} ticks",
            req.Token, playbackStopInfo.PositionTicks);

        // Save playback position to DeviceQueue for resume-after-pause recovery.
        if (!isDisplacement && !string.IsNullOrEmpty(req.Token))
        {
            queue.CurrentPositionTicks = realPositionTicks;
            queue.CurrentItemId = req.Token;
            Logger.LogDebug(
                "Saved playback position to DeviceQueue: device={DeviceId}, item={ItemId}, offset={OffsetMs}ms",
                context.System.Device.DeviceID, req.Token, req.OffsetInMilliseconds);
        }

        // Persist real position to ItemPositionState (bypasses Jellyfin's MinAudiobookResume)
        // and overwrite Jellyfin UserData for cross-client sync (web/mobile see correct position).
        if (!isDisplacement && realPositionTicks > 0 && Guid.TryParse(req.Token, out Guid itemIdGuid))
        {
            // 1. Save to plugin's per-item state (normalize key to "N" format to match readers)
            string positionKey = itemIdGuid.ToString("N");
            queue.ItemPositionState[positionKey] = realPositionTicks;

            // Evict stale entries when the dictionary grows beyond cap
            TrimItemPositionState(queue);

            _queueManager.SchedulePersist(context.System.Device.DeviceID);
            Logger.LogDebug(
                "Saved to ItemPositionState: item={ItemId}, ticks={Ticks}",
                req.Token, realPositionTicks);

            // 2. Overwrite Jellyfin UserData with real position for cross-client sync
            //    OnPlaybackStopped → UpdatePlayState may have zeroed it (MinAudiobookResume).
            try
            {
                var item = _libraryManager.GetItemById(itemIdGuid);
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
