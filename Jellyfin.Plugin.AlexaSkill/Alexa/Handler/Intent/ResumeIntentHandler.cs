using System;
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
/// Handler for AMAZON.ResumeIntent intents and resume directive.
/// </summary>
public class ResumeIntentHandler : BaseHandler
{
    public ResumeIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "AMAZON.ResumeIntent", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Pause any currently playing media.
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

        if (string.Equals(context.AudioPlayer.PlayerActivity, "PLAYING"))
        {
            return ResponseBuilder.Empty();
        }

        // TODO: Should context or session be preferred?

        string item_id = context.AudioPlayer?.Token ?? session.FullNowPlayingItem.Id.ToString();

        int offset = 0;
        if (context.AudioPlayer != null && context.AudioPlayer.OffsetInMilliseconds > 0)
        {
            offset = (int)context.AudioPlayer.OffsetInMilliseconds;
        }
        else if (session.PlayState != null)
        {
            offset = (int)TimeSpan.FromTicks(session.PlayState?.PositionTicks ?? 0).TotalMilliseconds;
        }

        return ResponseBuilder.AudioPlayerPlay(PlayBehavior.Enqueue, GetStreamUrl(item_id, user), item_id, item_id, offset);
    }
}
