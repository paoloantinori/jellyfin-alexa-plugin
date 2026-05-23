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

        Logger.LogInformation(
            "PlaybackStopped: item={Token}, offset={OffsetMs}ms, playerActivity={Activity}",
            req.Token, req.OffsetInMilliseconds, context.AudioPlayer?.PlayerActivity);

        // Detect displacement events: when a new AudioPlayer.Play replaces the current track,
        // Alexa sends PlaybackStopped for the OLD item with a near-zero offset from the new
        // track's start. This would overwrite the real saved position of the old item.
        // Guard: if the token doesn't match the current queue's playing item, this is a
        // displacement event — still report the stop but with position 0 to avoid corrupting
        // the real progress.
        bool isDisplacement = false;
        if (_queueManager != null)
        {
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
        }

        long positionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks;
        if (isDisplacement)
        {
            positionTicks = 0;
        }

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