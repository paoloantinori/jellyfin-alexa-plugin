using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
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
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFailedEventHandler"/> class.
    /// JF-447: the displacement classification reads the report-ordering state (the
    /// device's latest start), not the device queue. The optional queue manager
    /// (JF-655) carries the active-audio flag clear below; DI injects the singleton.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager holding the JF-655 active-audio flag.</param>
    public PlaybackFailedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
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
        string deviceId = context.GetDeviceId();

        Logger.LogError(
            "Playback failed for item {ItemId} at offset {OffsetMs}ms [RequestId={RequestId}, DeviceId={DeviceId}]",
            req.Token,
            req.OffsetInMilliseconds,
            request.RequestId,
            deviceId);

        // JF-655: a failed stream is not an active one; the re-launch gates'
        // event-owned signal clears (a retry launch's PlaybackStarted re-sets it).
        // A displacement failure (a newer stream already started) keeps the flag,
        // the same exemption the Stopped handler owns (code-review finding).
        if (!PlaybackReportOrdering.IsDisplacementStop(deviceId, req.Token))
        {
            (_queueManager ?? Plugin.Instance?.DeviceQueueManager)?.MarkAudioPlaybackStopped(deviceId);
        }

        // Sleep-timer streams carry composite tokens ("{guid}|sleep:{ticks}", minted by
        // SleepTimerIntentHandler); the shared StreamTokenCodec is the one owner of the
        // format (JF-447). Anything unparseable yields Guid.Empty so the event handler
        // still completes and returns the ack Amazon requires.
        StreamTokenCodec.TryGetItemId(req.Token, out Guid itemId);

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            Failed = true,
        };

        // JF-425/JF-447: register before reporting so a still in-flight playback-start
        // report cannot resurrect Playing state after this failure clears it; a
        // displacement failure (the OLD stream failing as a newer play displaces it)
        // is not recorded and its write's clearing of the new track's entry is undone.
        await Progress.ReportStopOrderedAsync(
            deviceId, req.Token, playbackStopInfo, "displacement failure cleared the new track's entry").ConfigureAwait(false);

        return BuildKeepAliveResponse();
    }
}
