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

#pragma warning disable CA1711
public class PlaybackFinishedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackFinishedEventHandler"/> class.
    /// JF-447: the displacement classification reads the report-ordering state (the
    /// device's latest start), not the device queue, so no queue manager is needed.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlaybackFinishedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
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

        // JF-447: composite sleep-timer tokens parse through the shared codec; the raw
        // new Guid(token) threw FormatException on them, killing this handler before the
        // ordering registration and before the keep-alive ack Amazon requires.
        StreamTokenCodec.TryGetItemId(req.Token, out Guid itemId);

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = itemId,
            PositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks,
        };

        // JF-425/JF-447: register before reporting (a still in-flight playback-start
        // report must not resurrect Playing state after this stop clears it); a
        // displacement finish (the OLD stream ending as a newer play displaces it) is
        // not recorded and its write's clearing of the new track's entry is undone.
        await ReportStopOrderedAsync(
            deviceId, req.Token, playbackStopInfo, "displacement finish cleared the new track's entry").ConfigureAwait(false);

        Logger.LogDebug(
            "PlaybackFinished: saved to server, item={Token}, positionTicks={Ticks}",
            req.Token, playbackStopInfo.PositionTicks);

        // If PlaybackNearlyFinished enqueued a next track, keep the session alive
        // for APL touch events and the upcoming track. PLAYING/BUFFER_UNDERRUN are
        // the two active-playback states, shared with PlayRadio's seed decision via
        // BaseHandler.IsActivelyPlaying (JF-481).
        bool hasQueuedNext = IsActivelyPlaying(context);

        if (!hasQueuedNext)
        {
            Logger.LogInformation("PlaybackFinished: queue exhausted, ending session to dismiss APL screen");
            return BuildEndSessionResponse();
        }

        return BuildKeepAliveResponse();
    }
}
