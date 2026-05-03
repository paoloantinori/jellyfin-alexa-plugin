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
/// Handler for LearnMyVoiceIntent. Links the Alexa speaker's voice profile
/// to the current Jellyfin user account.
/// </summary>
public class LearnMyVoiceIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LearnMyVoiceIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public LearnMyVoiceIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.LearnMyVoice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Link the recognized voice profile to the current user's Jellyfin account.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A skill response confirming the voice link.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        string? personId = context.System?.Person?.PersonId;
        if (string.IsNullOrEmpty(personId))
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("VoiceLearnFailed", locale)));
        }

        if (!string.IsNullOrEmpty(user.AlexaPersonId)
            && string.Equals(user.AlexaPersonId, personId, StringComparison.Ordinal))
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("VoiceAlreadyLinked", locale, user.Username)));
        }

        // Check if this voice profile is already linked to a different user
        Entities.User? existingUser = Plugin.Instance?.Configuration.GetUserByPersonId(personId);
        if (existingUser != null && existingUser.Id != user.Id)
        {
            Logger.LogWarning("Voice profile {PersonId} is already linked to user {OtherUser}, reassigning to {CurrentUser}", personId, existingUser.Username, user.Username);
            existingUser.AlexaPersonId = null;
        }

        user.AlexaPersonId = personId;
        Plugin.Instance?.SaveConfiguration();

        Logger.LogInformation("Linked voice profile {PersonId} to user {Username}", personId, user.Username);

        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("VoiceLearned", locale, user.Username)));
    }
}
