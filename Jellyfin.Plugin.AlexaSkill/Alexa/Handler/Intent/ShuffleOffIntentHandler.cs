using System;
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
    public ShuffleOffIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "AMAZON.ShuffleOffIntent");
    }

    /// <summary>
    /// Set the currently started media as playing.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>Empty response.</returns>
    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        PlaybackState requestState = context.AudioPlayer;

        long positionTicks = TimeSpan.FromMilliseconds(requestState.OffsetInMilliseconds).Ticks;
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(requestState.Token),
            RepeatMode = session.PlayState.RepeatMode,
            PositionTicks = positionTicks,
            PlaybackOrder = PlaybackOrder.Default,
        };

        SessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);

        return ResponseBuilder.Empty();
    }
}