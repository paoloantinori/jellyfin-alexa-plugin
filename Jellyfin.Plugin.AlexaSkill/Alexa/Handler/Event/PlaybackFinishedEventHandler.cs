using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

#pragma warning disable CA1711
public class PlaybackFinishedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly DeviceQueueManager _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFinishedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Device queue manager for the displacement classification.</param>
    public PlaybackFinishedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        DeviceQueueManager queueManager) : base(sessionManager, config, loggerFactory)
    {
        _queueManager = queueManager;
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
        string deviceId = context.System?.Device?.DeviceID ?? string.Empty;

        Logger.LogDebug(
            "PlaybackFinished: item={Token}, offset={OffsetMs}ms, sessionId={SessionId}",
            req.Token, req.OffsetInMilliseconds, session.Id);

        // JF-425 displacement classification (shared with PlaybackStoppedEventHandler):
        // when the queue already moved to a new item, this Finished event is the OLD
        // stream ending as the new play displaces it; its stop must NOT be registered
        // as a superseding correction, or the old item's late start report would replay
        // it and clear the NEW track's now-playing entry.
        bool isDisplacement = PlaybackReportOrdering.IsDisplacementStop(
            _queueManager.GetQueue(deviceId), req.Token, out string? expectedItemId);
        if (isDisplacement)
        {
            Logger.LogDebug(
                "PlaybackFinished: displacement detected, finished item={FinishedToken} but queue expects={QueueToken}; not recording the stop",
                req.Token, expectedItemId);
        }

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(req.Token),
            PositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks,
        };

        // JF-425: register before reporting (a still in-flight playback-start report must
        // not resurrect Playing state after this stop clears it). The next track's
        // PlaybackStarted opens a new generation and clears this correction.
        // Displacement stops do not register (see PlaybackReportOrdering).
        if (!isDisplacement)
        {
            PlaybackReportOrdering.RecordStop(deviceId, playbackStopInfo);
        }

        await SessionManager.OnPlaybackStopped(playbackStopInfo).ConfigureAwait(false);

        Logger.LogDebug(
            "PlaybackFinished: saved to server — item={Token}, positionTicks={Ticks}",
            req.Token, playbackStopInfo.PositionTicks);

        // If PlaybackNearlyFinished enqueued a next track, keep the session alive
        // for APL touch events and the upcoming track.
        bool hasQueuedNext = context.AudioPlayer?.PlayerActivity == "PLAYING"
            || context.AudioPlayer?.PlayerActivity == "BUFFER_UNDERRUN";

        if (!hasQueuedNext)
        {
            Logger.LogInformation("PlaybackFinished: queue exhausted, ending session to dismiss APL screen");
            return BuildEndSessionResponse();
        }

        return BuildKeepAliveResponse();
    }
}
