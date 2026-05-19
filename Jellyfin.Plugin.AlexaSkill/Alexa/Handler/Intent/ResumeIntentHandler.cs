using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.ResumeIntent intents and resume directive.
/// Restores playback from the last known position using a three-tier fallback:
/// 1. Alexa AudioPlayer context (most accurate when device retains state)
/// 2. Jellyfin session play state
/// 3. DeviceQueue persisted state (survives device state loss after pause)
/// </summary>
public class ResumeIntentHandler : BaseHandler
{
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResumeIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for pause/resume state.</param>
    public ResumeIntentHandler(
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
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonResume, System.StringComparison.Ordinal)) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Play);
    }

    /// <summary>
    /// Resume paused media playback.
    /// Uses three-tier fallback for position recovery: Alexa context, Jellyfin session, DeviceQueue.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Skill response with AudioPlayer directive, or error message.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (session?.FullNowPlayingItem == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", GetLocale(request))));
        }

        if (string.Equals(context.AudioPlayer.PlayerActivity, "PLAYING", StringComparison.Ordinal))
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        }

        string item_id = context.AudioPlayer?.Token ?? session.FullNowPlayingItem.Id.ToString();

        int offset = 0;

        // Fallback 1: Alexa AudioPlayer context (most accurate when device retains state)
        if (context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0)
        {
            offset = (int)context.AudioPlayer.OffsetInMilliseconds;
        }
        // Fallback 2: Jellyfin session play state
        else if (session.PlayState != null)
        {
            offset = (int)TimeSpan.FromTicks(session.PlayState?.PositionTicks ?? 0).TotalMilliseconds;
        }

        // Fallback 3: DeviceQueue persisted state (survives after AudioPlayer.Stop clears context)
        if (offset == 0 && _queueManager != null)
        {
            var queue = _queueManager.GetOrCreateQueue(context.System.Device.DeviceID);
            if (!string.IsNullOrEmpty(queue.CurrentItemId) && queue.CurrentPositionTicks > 0)
            {
                // Only use DeviceQueue item if it matches the session's now-playing item
                // (or if the AudioPlayer token is empty/missing)
                if (string.IsNullOrEmpty(context.AudioPlayer?.Token) ||
                    string.Equals(context.AudioPlayer.Token, queue.CurrentItemId, StringComparison.Ordinal))
                {
                    item_id = queue.CurrentItemId;
                    offset = (int)TimeSpan.FromTicks(queue.CurrentPositionTicks).TotalMilliseconds;
                    Logger.LogInformation(
                        "ResumeIntent: using DeviceQueue fallback for device {DeviceId}: item={ItemId}, offset={OffsetMs}ms",
                        context.System.Device.DeviceID, item_id, offset);
                }
            }
        }

        return Task.FromResult<SkillResponse>(BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, session.FullNowPlayingItem, user, context, offset));
    }
}
