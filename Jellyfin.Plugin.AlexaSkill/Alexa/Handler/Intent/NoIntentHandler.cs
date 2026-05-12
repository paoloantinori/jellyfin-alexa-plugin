using System;
using System.Collections.Generic;
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
/// Handles AMAZON.NoIntent during search disambiguation or resume confirmation.
/// For resume: clears resume state and returns a fresh welcome prompt.
/// For disambiguation: advances to the next match or reports no more matches.
/// </summary>
public class NoIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NoIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public NoIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is IntentRequest intentRequest
            && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonNo, StringComparison.Ordinal);
    }

    /// <summary>
    /// Handle without session attributes - no disambiguation or resume in progress.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", GetLocale(request))));
    }

    /// <summary>
    /// Handle with session attributes - resolve resume rejection or advance disambiguation.
    /// Resume rejection takes priority over disambiguation when both are present.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="sessionAttributes">Session attributes from the Alexa request.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // Check for resume rejection first
        if (ResumeHelper.HasResumeState(sessionAttributes))
        {
            return HandleResumeRejection(locale);
        }

        var state = DisambiguationHelper.ReadState(sessionAttributes);
        if (state == null)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("UnexpectedYes", locale)));
        }

        var (matches, index, mediaType) = state.Value;
        int nextIndex = index + 1;

        if (nextIndex >= matches.Count)
        {
            return Task.FromResult(DisambiguationHelper.NoMoreMatches(locale));
        }

        SkillResponse response = DisambiguationHelper.AskNextMatch(matches, nextIndex, mediaType, locale);
        return Task.FromResult(response);
    }

    /// <summary>
    /// Handle resume rejection: clear resume state and return a fresh welcome prompt.
    /// </summary>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A welcome Ask response.</returns>
    private Task<SkillResponse> HandleResumeRejection(string locale)
    {
        string freshStart = ResponseStrings.Get("FreshStart", locale);
        string reprompt = ResponseStrings.Get("WelcomeReprompt", locale);

        return Task.FromResult(ResponseBuilder.Ask(freshStart, new Reprompt(reprompt)));
    }
}
