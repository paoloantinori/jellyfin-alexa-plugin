using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

#pragma warning disable CA1711
public class PlaybackFinishedEventHandler : BaseHandler
#pragma warning restore CA1711
{
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

        Logger.LogDebug(
            "PlaybackFinished: item={Token}, offset={OffsetMs}ms, sessionId={SessionId}",
            req.Token, req.OffsetInMilliseconds, session.Id);

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(req.Token),
            PositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks,
        };
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
