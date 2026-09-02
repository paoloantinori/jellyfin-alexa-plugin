using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaybackFinished events.
/// </summary>
#pragma warning disable CA1711
public class PlaybackFailedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly DeviceQueueManager _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFailedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Device queue manager for the displacement classification.</param>
    public PlaybackFailedEventHandler(
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
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackFailed;
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;
        string deviceId = context.System?.Device?.DeviceID ?? string.Empty;

        Logger.LogError(
            "Playback failed for item {ItemId} at offset {OffsetMs}ms [RequestId={RequestId}, DeviceId={DeviceId}]",
            req.Token,
            req.OffsetInMilliseconds,
            request.RequestId,
            deviceId);

        // Sleep-timer streams carry composite tokens ("{guid}|sleep:{ticks}", minted by
        // SleepTimerIntentHandler and parsed the same way by
        // PlaybackNearlyFinishedEventHandler). new Guid(token) throws FormatException on
        // them, which aborted this handler before the ordering registration below and
        // before the keep-alive ack Amazon requires on AudioPlayer events.
        Guid itemId = ParseItemId(req.Token);

        // JF-425 displacement classification (shared with PlaybackStoppedEventHandler):
        // when the queue already moved to a new item, this failure belongs to the OLD
        // stream a new play displaced; its stop must NOT be registered as a superseding
        // correction, or the old item's late start report would replay it and clear the
        // NEW track's now-playing entry.
        bool isDisplacement = PlaybackReportOrdering.IsDisplacementStop(
            _queueManager.GetQueue(deviceId), req.Token, out string? expectedItemId);
        if (isDisplacement)
        {
            Logger.LogDebug(
                "PlaybackFailed: displacement detected, failed item={FailedToken} but queue expects={QueueToken}; not recording the stop",
                req.Token, expectedItemId);
        }

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            Failed = true,
        };

        // JF-425: register before reporting so a still in-flight playback-start report
        // cannot resurrect Playing state after this failure clears it. Displacement
        // stops do not register (see PlaybackReportOrdering).
        if (!isDisplacement)
        {
            PlaybackReportOrdering.RecordStop(deviceId, playbackStopInfo);
        }

        await SessionManager.OnPlaybackStopped(playbackStopInfo).ConfigureAwait(false);

        return BuildKeepAliveResponse();
    }

    /// <summary>
    /// Parses the item ID from an AudioPlayer stream token. Composite sleep-timer
    /// tokens ("{guid}|sleep:{ticks}") carry the item ID before the '|' suffix;
    /// anything unparseable yields <see cref="Guid.Empty"/> so the event handler still
    /// completes and returns the ack Amazon requires.
    /// </summary>
    /// <param name="token">The raw stream token from the event.</param>
    /// <returns>The item ID embedded in the token, or <see cref="Guid.Empty"/>.</returns>
    private static Guid ParseItemId(string token)
    {
        int pipeIndex = token.IndexOf('|');
        string itemIdText = pipeIndex >= 0 ? token[..pipeIndex] : token;
        return Guid.TryParse(itemIdText, out Guid itemId) ? itemId : Guid.Empty;
    }
}