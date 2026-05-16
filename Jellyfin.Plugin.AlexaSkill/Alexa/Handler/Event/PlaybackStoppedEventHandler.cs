using System;
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
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaybackStopped events.
/// Saves the last playback position and item to DeviceQueue for resume-after-pause recovery.
/// </summary>
#pragma warning disable CA1711
public class PlaybackStoppedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStoppedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for pause/resume state.</param>
    public PlaybackStoppedEventHandler(
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
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackStopped;
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(req.Token),
            PositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks,
        };
        await SessionManager.OnPlaybackStopped(playbackStopInfo).ConfigureAwait(false);

        // Save playback position to DeviceQueue for resume-after-pause recovery.
        // This allows ResumeIntentHandler to restore the exact position even when
        // the Alexa AudioPlayer context has been cleared (e.g. after a Stop directive).
        if (_queueManager != null && !string.IsNullOrEmpty(req.Token))
        {
            var queue = _queueManager.GetOrCreateQueue(context.System.Device.DeviceID);
            queue.CurrentPositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks;
            queue.CurrentItemId = req.Token;
            Logger.LogDebug(
                "Saved playback position to DeviceQueue: device={DeviceId}, item={ItemId}, offset={OffsetMs}ms",
                context.System.Device.DeviceID, req.Token, req.OffsetInMilliseconds);
        }

        return ResponseBuilder.Empty();
    }
}