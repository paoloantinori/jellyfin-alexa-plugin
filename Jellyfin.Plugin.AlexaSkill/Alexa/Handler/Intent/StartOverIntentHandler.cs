using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.StartOverIntent intents and resume directive.
/// </summary>
public class StartOverIntentHandler : BaseHandler
{
    public StartOverIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "AMAZON.StartOverIntent", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Restart any currently playing media.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>Emptry skill response.</returns>
    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        if (session?.FullNowPlayingItem == null)
        {
            return ResponseBuilder.Tell("There is no media currently playing.");
        }

        string item_id = session.FullNowPlayingItem.Id.ToString();
        return ResponseBuilder.AudioPlayerPlay(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, 0);
    }
}


