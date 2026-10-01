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
        // the session ends and the screen dismisses exactly as before. JF-691 adds
        // two more: the unreshuffled-shuffle mode (NearlyFinished's resolver
        // random-picks a next at EVERY position with count > 1, the last one
        // included, so playback continues where this queue-shape read alone saw
        // exhaustion) and the enqueue-record veto below, which turns the arms from
        // "the queue LOOKS continued" into "a stream was actually enqueued after
        // this token".
        int finishedIndex = -1;
        bool vetoedByEnqueueRecord = false;
        if (!hasQueuedNext)
        {
            bool sleepExpired = StreamTokenCodec.IsSleepExpiredUtc(req.Token, DateTimeOffset.UtcNow);
            if (!sleepExpired)
            {
                finishedIndex = SessionQueue.IndexOfQueueItem(session, itemId);
                bool loops = (session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone) != RepeatMode.RepeatNone;

                // JF-691 (a): the mode-aware arm. NearlyFinished's random-pick
                // admission guard is ONE shared predicate (ShuffleRandomPickApplies,
                // evaluated by both handlers over the shared ResolvePlaybackOrder
                // resolution), so this arm can only fire where the resolver itself
                // would have produced a next. Default order and a reshuffled queue
                // advance sequentially: their last position is true exhaustion and
                // the arm deliberately does not fire there.
                var (resolvedOrder, resolvedReshuffled) = ProgressReporter.ResolvePlaybackOrder(session, context, _queueManager);
                bool shuffleRandomNext = ProgressReporter.ShuffleRandomPickApplies(resolvedOrder, resolvedReshuffled, session.NowPlayingQueue.Count)
                    && finishedIndex >= 0;

                if ((finishedIndex >= 0 && finishedIndex + 1 < session.NowPlayingQueue.Count) || (loops && finishedIndex >= 0) || shuffleRandomNext)
                {
                    // JF-691 (b): shape is not the enqueue; the veto asks what the
                    // device was actually told (rationale on the helper).
                    if (EnqueuedForThisBoundary(deviceId, req.Token))
                    {
                        hasQueuedNext = true;
                        Logger.LogDebug(
                            "PlaybackFinished: playerActivity={Activity} (inter-track gap) but playback continues (index={Index} of {QueueCount}, loops={Loops}, shuffleRandom={ShuffleRandom}); keeping the session alive",
                            context.AudioPlayer?.PlayerActivity, finishedIndex, session.NowPlayingQueue.Count, loops, shuffleRandomNext);
                    }
                    else
                    {
                        vetoedByEnqueueRecord = true;
                    }
                }
            }
            else
            {
                // JF-683 review F4: the carve-out must be diagnosable on its own - the
                // exhausted line below would otherwise report finishedIndex=-1 against
                // a populated queue with no reason anywhere.
                Logger.LogDebug(
                    "PlaybackFinished: sleep timer expired (the token's deadline has passed, NearlyFinished enqueued nothing); ending the session despite the queue view");
            }
        }

        if (!hasQueuedNext)
        {
            // The JF-691 record veto is a DIFFERENT ending reason than exhaustion and
            // gets its own Info line (the JF-683 review-F4 lesson: the generic line
            // would report a mid-queue finishedIndex with the true reason visible
            // only at Debug, the exact misdiagnosis trap that carve-out was added
            // to prevent).
            if (vetoedByEnqueueRecord)
            {
                Logger.LogInformation(
                    "PlaybackFinished: nothing was enqueued for this boundary (the enqueue record names an older token), ending session to dismiss APL screen (queueCount={QueueCount}, finishedIndex={Index}, playerActivity={Activity})",
                    session.NowPlayingQueue.Count, finishedIndex, context.AudioPlayer?.PlayerActivity);
            }
            else
            {
                Logger.LogInformation(
                    "PlaybackFinished: queue exhausted, ending session to dismiss APL screen (queueCount={QueueCount}, finishedIndex={Index}, playerActivity={Activity})",
                    session.NowPlayingQueue.Count, finishedIndex, context.AudioPlayer?.PlayerActivity);
            }

            return BuildEndSessionResponse();
        }

        return BuildKeepAliveResponse();
    }

    /// <summary>
    /// JF-691 (b): the veto that turns the queue-shape arms from "the queue LOOKS
    /// continued" into "a stream was actually enqueued after this token". The
    /// record is the durable twin of the directive's ExpectedPreviousToken, written
    /// by the ONE BuildAudioPlayerResponse chokepoint for every Enqueue directive
    /// the device was told about (see
    /// <see cref="Playback.DeviceQueue.LastEnqueueAfterToken"/>). The
    /// deleted-successor shape is the exact miss it closes: NearlyFinished resolved
    /// a next the library could not serve and returned Empty, so the record still
    /// names the PREVIOUS boundary (or is absent on the first one) while the queue
    /// shape still shows the successor. Conservative bounds: no injected manager or
    /// no queue for the device keeps the pure heuristic (nothing authoritative to
    /// consult; the arms alone run, the pre-JF-691 behavior). The read uses the
    /// INJECTED manager only, with no Plugin.Instance fallback: in production DI
    /// injects the same singleton the chokepoint wrote, and the handler-level tests
    /// stay hermetic. DISPLACED FINISHES (code-review round, deliberate): the OLD
    /// stream's late Finished also ends the session when the record names a newer
    /// boundary, where the pre-JF-691 heuristic kept it alive; the difference is
    /// inert because the replacing play itself already carried
    /// <c>ShouldEndSession=true</c> (the skill session ended at that play), and an
    /// enqueue-shaped replacement IS recorded with the displaced token as its
    /// after-token, so the veto passes there. No displacement machinery is
    /// consulted on purpose: the JF-655 exemption serves the active-audio flag, a
    /// different contract.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID (the device-store key).</param>
    /// <param name="finishedToken">The finished stream's token from the event.</param>
    /// <returns>True when the record names this boundary (or cannot be consulted).</returns>
    private bool EnqueuedForThisBoundary(string deviceId, string finishedToken)
    {
        if (_queueManager == null)
        {
            return true;
        }

        DeviceQueue? queue = _queueManager.GetQueue(deviceId);
        if (queue == null)
        {
            return true;
        }

        if (string.Equals(queue.LastEnqueueAfterToken, finishedToken, StringComparison.Ordinal))
        {
            return true;
        }

        Logger.LogDebug(
            "PlaybackFinished: queue shape says playback continues, but the last enqueue this device was sent followed token {RecordedToken} (next item {RecordedNext}), not the finished {FinishedToken}; ending the session (nothing was enqueued for this boundary, the JF-691 deleted-successor shape)",
            queue.LastEnqueueAfterToken, queue.LastEnqueueNextItemId, finishedToken);
        return false;
    }
}
