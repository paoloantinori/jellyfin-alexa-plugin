using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

#pragma warning disable CA1711
public class PlaybackFinishedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly DeviceQueueManager? _queueManager;
    private readonly ILibraryManager? _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFinishedEventHandler"/> class.
    /// JF-447: the displacement classification reads the report-ordering state (the
    /// device's latest start), not the device queue. The optional JF-522 dependencies
    /// (launch-scope base read + runtime guard) fall back to <c>Plugin.Instance</c> /
    /// no guard when not injected.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager holding the JF-522 launch-scope store.</param>
    /// <param name="libraryManager">Optional library manager for the JF-522 runtime guard.</param>
    public PlaybackFinishedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null,
        ILibraryManager? libraryManager = null) : base(sessionManager, config, loggerFactory)
    {
        _queueManager = queueManager;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackFinished;
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;
        string deviceId = context.GetDeviceId();

        Logger.LogDebug(
            "PlaybackFinished: item={Token}, offset={OffsetMs}ms, sessionId={SessionId}",
            req.Token, req.OffsetInMilliseconds, session.Id);

        // JF-655: a finished stream is not an active one, so the re-launch gates'
        // event-owned signal clears; when NearlyFinished enqueued a next track, that
        // stream's own PlaybackStarted re-sets the flag moments later. A
        // displacement finish (a newer stream already started) keeps the flag, the
        // same exemption the Stopped handler owns (code-review finding).
        if (!PlaybackReportOrdering.IsDisplacementStop(deviceId, req.Token))
        {
            (_queueManager ?? Plugin.Instance?.DeviceQueueManager)?.MarkAudioPlaybackStopped(deviceId);
        }

        // JF-447: composite sleep-timer tokens parse through the shared codec; the raw
        // new Guid(token) threw FormatException on them, killing this handler before the
        // ordering registration and before the keep-alive ack Amazon requires.
        StreamTokenCodec.TryGetItemId(req.Token, out Guid itemId);

        // JF-522: report the item-absolute end position (launch base + raw offset) so
        // the server PlayState carries the same timeline every other store does. At a
        // natural finish the composition lands exactly on the item runtime, which the
        // writer-side stale-base guard allows (only strictly-past-runtime is rejected).
        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            PositionTicks = Progress.ComposeEventPositionTicks(
                deviceId, itemId, req.OffsetInMilliseconds, "PlaybackFinished", _queueManager, _libraryManager),
        };

        // JF-425/JF-447: register before reporting (a still in-flight playback-start
        // report must not resurrect Playing state after this stop clears it); a
        // displacement finish (the OLD stream ending as a newer play displaces it) is
        // not recorded and its write's clearing of the new track's entry is undone.
        await Progress.ReportStopOrderedAsync(
            deviceId, req.Token, playbackStopInfo, "displacement finish cleared the new track's entry").ConfigureAwait(false);

        Logger.LogDebug(
            "PlaybackFinished: saved to server, item={Token}, positionTicks={Ticks}",
            req.Token, playbackStopInfo.PositionTicks);

        // JF-574 bridge, same call NearlyFinished makes at entry (review round, JF-683):
        // a restart-wiped session queue would read as exhaustion here too (the
        // successor scan below answers -1 on an empty queue while the device still
        // plays the queue NearlyFinished enqueued pre-restart). Coherence-checked by the
        // shared helper (the finished token must be a member); no-op without a queue
        // manager or on a populated queue.
        ProgressReporter.TryRehydrateSessionQueueFromDevice(_queueManager, session, context, Logger, "PlaybackFinished");

        // If PlaybackNearlyFinished enqueued a next track, keep the session alive
        // for APL touch events and the upcoming track. PLAYING/BUFFER_UNDERRUN are
        // the two active-playback states, shared with PlayRadio's seed decision via
        // PlaybackLaunchBuilder.IsActivelyPlaying (JF-481).
        bool hasQueuedNext = PlaybackLaunchBuilder.IsActivelyPlaying(context);

        // JF-683: the playerActivity read alone loses the inter-track race. Live
        // 2026-09-30 (Norah Jones round): every PlaybackFinished arrived with
        // playerActivity=FINISHED during the ~1s gap before the ALREADY-ENQUEUED
        // next stream started, so the handler logged "queue exhausted" and ended the
        // session at every track boundary (three times by track 4; the user's later
        // pause then arrived sessionNew=true because of it). The session queue is
        // the plugin's own view of what is still queued: when the finished item has
        // a successor there, a next stream exists (the enqueue NearlyFinished
        // performed, or precompute will serve it) and the session must survive the
        // gap. Resolved from the event's own token (the finished stream, composite
        // sleep tokens included; an unparseable token is never queued, so the scan
        // answers -1 and the old activity-only behavior holds).
        // Two deliberate refinements (review round): a loop mode (RepeatOne /
        // RepeatAll) enqueues a NON-adjacent successor at the last queue position
        // (the same item / a wrap to index 0), so loop mode counts as queued-next
        // regardless of position; and an EXPIRED sleep timer is the one shape where
        // NearlyFinished deliberately enqueued nothing (its own gate at entry), so
        // the session ends and the screen dismisses exactly as before.
        int finishedIndex = -1;
        if (!hasQueuedNext)
        {
            bool sleepExpired = StreamTokenCodec.TryGetSleepDeadlineUtcTicks(req.Token, out long deadlineTicks)
                && DateTimeOffset.UtcNow.UtcTicks >= deadlineTicks;
            if (!sleepExpired)
            {
                finishedIndex = SessionQueue.IndexOfQueueItem(session, itemId);
                bool loops = (session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone) != RepeatMode.RepeatNone;
                if ((finishedIndex >= 0 && finishedIndex + 1 < session.NowPlayingQueue.Count) || (loops && finishedIndex >= 0))
                {
                    hasQueuedNext = true;
                    Logger.LogDebug(
                        "PlaybackFinished: playerActivity={Activity} (inter-track gap) but playback continues (index={Index} of {QueueCount}, loops={Loops}); keeping the session alive",
                        context.AudioPlayer?.PlayerActivity, finishedIndex, session.NowPlayingQueue.Count, loops);
                }
            }
        }

        if (!hasQueuedNext)
        {
            Logger.LogInformation(
                "PlaybackFinished: queue exhausted, ending session to dismiss APL screen (queueCount={QueueCount}, finishedIndex={Index}, playerActivity={Activity})",
                session.NowPlayingQueue.Count, finishedIndex, context.AudioPlayer?.PlayerActivity);
            return BuildEndSessionResponse();
        }

        return BuildKeepAliveResponse();
    }
}
