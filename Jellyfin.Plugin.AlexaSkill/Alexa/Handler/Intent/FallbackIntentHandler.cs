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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for FallbackIntent requests.
/// </summary>
public class FallbackIntentHandler : BaseHandler
{
    private readonly ILibraryManager? _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FallbackIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="libraryManager">Optional library manager for resume re-prompts (JF-397).</param>
    public FallbackIntentHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory, ILibraryManager? libraryManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        if (request is IntentRequest intentRequest)
        {
            return string.Equals(intentRequest.Intent.Name, IntentNames.AmazonFallback, System.StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Log the occured exception and notify user.
    /// </summary>
    /// <param name="request">The skill intent request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>Notification about an error.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        // For unsupported built-in intents, give a specific message
        if (intentRequest.Intent.Name.StartsWith("AMAZON.", System.StringComparison.Ordinal)
            && !string.Equals(intentRequest.Intent.Name, IntentNames.AmazonFallback, System.StringComparison.Ordinal))
        {
            Logger.LogDebug("FallbackIntent: unsupported built-in intent '{IntentName}', returning UnsupportedIntent", intentRequest.Intent.Name);
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("UnsupportedIntent", locale)));
        }

        Logger.LogDebug("FallbackIntent: unmatched input, returning CouldNotUnderstand");
        return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("CouldNotUnderstand", locale)));
    }

    /// <summary>
    /// State-aware fallback (JF-397): when a conversational flow is active, an
    /// unrecognized utterance re-prompts the flow's current question instead of
    /// answering CouldNotUnderstand as a Tell, which killed the session and forced
    /// the user to restart the whole conversation. Priority mirrors the Yes/No
    /// handlers: resume > pagination > disambiguation.
    /// </summary>
    /// <param name="request">The skill intent request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="sessionAttributes">Session attributes from the Alexa request.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A re-prompt for the active flow, or the standard fallback response.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, Dictionary<string, object>? sessionAttributes, CancellationToken cancellationToken)
    {
        if (sessionAttributes != null)
        {
            string locale = GetLocale(request);

            // Active resume offer: re-ask "resume X?" (needs the item title; without a
            // resolvable item we cannot rebuild the question and fall through cleanly).
            if (ResumeHelper.HasResumeState(sessionAttributes))
            {
                ResumeHelper.ResumeState? resumeState = ResumeHelper.ReadState(sessionAttributes);
                if (resumeState != null
                    && Guid.TryParse(resumeState.ItemId, out Guid itemId)
                    && _libraryManager?.GetItemById(itemId) is { Name: not null } item)
                {
                    Logger.LogDebug("FallbackIntent: active resume offer, re-asking resume prompt");
                    return Task.FromResult(AskLocalized(
                        "ResumePromptSsml", "ResumePrompt", "ResumeReprompt", locale, item.Name));
                }
            }

            // Active pagination: remind the user they can say "show more".
            if (ListPaginationHelper.HasPaginationState(sessionAttributes))
            {
                Logger.LogDebug("FallbackIntent: active pagination, re-prompting show more");
                string prompt = ResponseStrings.Get("ShowMorePrompt", locale);
                return Task.FromResult(ResponseBuilder.Ask(prompt, new Reprompt(prompt)));
            }

            // Active disambiguation: re-ask the CURRENT candidate question.
            if (DisambiguationHelper.HasDisambiguationState(sessionAttributes))
            {
                var state = DisambiguationHelper.ReadState(sessionAttributes);
                if (state.HasValue)
                {
                    var (matches, index, mediaType) = state.Value;
                    if (index >= 0 && index < matches.Count)
                    {
                        Logger.LogDebug("FallbackIntent: active disambiguation, re-asking current match (index={Index})", index);
                        return Task.FromResult(DisambiguationHelper.AskNextMatch(matches, index, mediaType, locale));
                    }
                }
            }
        }

        return HandleAsync(request, context, user, session, cancellationToken);
    }
}
