using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayIntent intents and play directive.
/// </summary>
public class PlayIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlayIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public PlayIntentHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.Play, System.StringComparison.Ordinal)) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Play);
    }

    /// <summary>
    /// Play a media.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        Logger.LogDebug("PlayIntent: invoked, nowPlayingItem={HasNowPlayingItem}, queueSize={QueueSize}", session.FullNowPlayingItem != null, session.NowPlayingQueue is { Count: > 0 } ? session.NowPlayingQueue.Count : 0);

        // check if something is currently playing which we can resume
        if (session.FullNowPlayingItem != null)
        {
            string item_id = session.FullNowPlayingItem.Id.ToString();
            Logger.LogDebug("PlayIntent: resuming from FullNowPlayingItem, itemId={ItemId}", item_id);
            return Task.FromResult<SkillResponse>(BuildAudioPlayerResponse(PlayBehavior.Enqueue, GetStreamUrl(item_id, user), item_id, session.FullNowPlayingItem, user, context));
        }
        else if (session.NowPlayingQueue is { Count: > 0 })
        {
            // resume the first item in the queue
            string item_id = session.NowPlayingQueue[0].Id.ToString();
            Logger.LogDebug("PlayIntent: resuming from NowPlayingQueue[0], itemId={ItemId}", item_id);
            return Task.FromResult<SkillResponse>(BuildAudioPlayerResponse(PlayBehavior.Enqueue, GetStreamUrl(item_id, user), item_id, null, user, context));
        }

        Logger.LogDebug("PlayIntent: nothing to play, returning empty response");
        return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
    }
}