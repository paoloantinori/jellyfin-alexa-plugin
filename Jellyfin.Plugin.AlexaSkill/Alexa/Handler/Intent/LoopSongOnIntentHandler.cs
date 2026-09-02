using System;
using System.Threading;
using System.Threading.Tasks;
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

public class LoopSongOnIntentHandler : BaseHandler
{
    public LoopSongOnIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        // Locale aliases (RepeatSingleOn, the repeat-one sibling): see the comment
        // on the loop intent constants in IntentNames.
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null
            && (string.Equals(intentRequest.Intent.Name, IntentNames.LoopSongOn, StringComparison.Ordinal)
                || string.Equals(intentRequest.Intent.Name, IntentNames.RepeatSingleOn, StringComparison.Ordinal));
    }

    /// <summary>
    /// Applies repeat-one to the currently playing item; shared body in
    /// <see cref="BaseHandler.ApplyRepeatModeAsync"/>.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Empty response.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => ApplyRepeatModeAsync(request, context, session, RepeatMode.RepeatOne, "LoopSongOn");
}
