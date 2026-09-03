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
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFailedEventHandler"/> class.
    /// JF-447: the displacement classification reads the report-ordering state (the
    /// device's latest start), not the device queue, so no queue manager is needed.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlaybackFailedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
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
        await ReportStopOrderedAsync(
            deviceId, req.Token, playbackStopInfo, "displacement failure cleared the new track's entry").ConfigureAwait(false);

        return BuildKeepAliveResponse();
    }
}
