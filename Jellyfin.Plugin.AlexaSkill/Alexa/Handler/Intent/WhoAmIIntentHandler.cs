using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for WhoAmIIntent. Tells the user which Jellyfin account is active,
/// based on voice recognition or the linked account.
/// </summary>
public class WhoAmIIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WhoAmIIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public WhoAmIIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.WhoAmI, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tell the user which Jellyfin account is currently active based on voice recognition.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A skill response with the user's identity or unrecognized message.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        string? personId = context.System?.Person?.PersonId;
        bool voiceRecognized = !string.IsNullOrEmpty(personId)
            && !string.IsNullOrEmpty(user.AlexaPersonId)
            && string.Equals(user.AlexaPersonId, personId, StringComparison.Ordinal);

        if (voiceRecognized)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("WhoAmI", locale, user.Username)));
        }

        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("WhoAmIUnknown", locale)));
    }
}
