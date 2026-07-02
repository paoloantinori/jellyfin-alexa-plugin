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

public class ShuffleOnIntentHandler : BaseHandler
{
    private readonly Playback.DeviceQueueManager? _queueManager;

    public ShuffleOnIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonShuffleOn, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enable shuffle for the current device queue. Sets the authoritative
    /// per-device <see cref="Playback.DeviceQueue.PlaybackOrder"/> (the state
    /// <c>ResolveNextItemId</c> reads) AND physically reshuffles the remaining
    /// queue so sequential advancement plays a shuffled order. Also mirrors the
    /// new order into <c>session.NowPlayingQueue</c>.
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

        Logger.LogDebug("ShuffleOn: entered, token={Token}, offset={OffsetMs}ms", currentToken, requestState.OffsetInMilliseconds);

        // Keep Jellyfin's session PlayState in sync (shown in the dashboard UI).
        if (tokenValid)
        {
            await ReportPlaybackProgress(session, currentId, requestState.OffsetInMilliseconds, PlaybackOrder.Shuffle, cancellationToken).ConfigureAwait(false);
        }

        // Authoritative plugin-side state: persisted per-device, read by the resolver.
        // Set even for short queues (where ShuffleRemaining is a no-op) so the flag
        // always reflects the user's intent.
        _queueManager?.SetPlaybackOrder(deviceId, "Shuffle");

        // Physically reorder the remaining queue (current item stays first) so the
        // effect does not depend on the indirect Jellyfin PlayState flag being read.
        if (_queueManager != null && tokenValid)
        {
            _queueManager.ShuffleRemaining(deviceId, currentToken!);
            MirrorQueueToSession(_queueManager.GetOrCreateQueue(deviceId), session);
        }

        return ResponseBuilder.Empty();
    }
}