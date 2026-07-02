using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

public class ShuffleOffIntentHandler : BaseHandler
{
    private readonly Playback.DeviceQueueManager? _queueManager;

    public ShuffleOffIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        Playback.DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonShuffleOff, StringComparison.Ordinal);
    }

    /// <summary>
    /// Disable shuffle for the current device queue. Clears the authoritative
    /// per-device <see cref="Playback.DeviceQueue.PlaybackOrder"/>, restores the
    /// original (pre-shuffle) queue order captured by
    /// <see cref="Playback.DeviceQueueManager.ShuffleRemaining"/>, and mirrors it
    /// into <c>session.NowPlayingQueue</c>.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Empty response.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        PlaybackState requestState = context.AudioPlayer;
        string deviceId = context.System.Device.DeviceID;
        string? currentToken = requestState.Token;
        Guid currentId = Guid.Empty;
        bool tokenValid = currentToken != null && Guid.TryParse(currentToken, out currentId);

        Logger.LogDebug("ShuffleOff: entered, token={Token}, offset={OffsetMs}ms", currentToken, requestState.OffsetInMilliseconds);

        // Keep Jellyfin's session PlayState in sync (shown in the dashboard UI).
        if (tokenValid)
        {
            await ReportPlaybackProgress(session, currentId, requestState.OffsetInMilliseconds, PlaybackOrder.Default, cancellationToken).ConfigureAwait(false);
        }

        // Authoritative plugin-side state: clear flag and restore original order.
        _queueManager?.SetPlaybackOrder(deviceId, "Default");
        if (_queueManager != null)
        {
            _queueManager.RestoreOrder(deviceId);
            if (tokenValid)
            {
                // RestoreOrder reverts ItemIds to the original sequence, which can
                // move the currently-playing item to a different index. Re-sync
                // CurrentIndex so persisted state (and PlaybackStoppedEventHandler's
                // displacement heuristic) still matches what's actually playing.
                _queueManager.MoveTo(deviceId, currentToken!);
            }

            MirrorQueueToSession(_queueManager.GetOrCreateQueue(deviceId), session);
        }

        return ResponseBuilder.Empty();
    }
}