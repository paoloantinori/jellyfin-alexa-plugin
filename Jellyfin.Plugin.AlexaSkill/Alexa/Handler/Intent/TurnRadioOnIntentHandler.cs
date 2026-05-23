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
/// Handler for TurnRadioOnIntent requests.
/// Enables radio mode so similar tracks are auto-enqueued when the queue runs out.
/// </summary>
public class TurnRadioOnIntentHandler : BaseHandler
{
    public TurnRadioOnIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.TurnRadioOn, StringComparison.Ordinal);
    }

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.RadioModeEnabled, request) is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        string locale = GetLocale(request);
        Logger.LogDebug("TurnRadioOn: entered, userId={UserId}, deviceId={DeviceId}", session.UserId, context.System.Device.DeviceID);
        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);
        Logger.LogInformation("Radio mode enabled for user {UserId}", session.UserId);
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("RadioModeOn", locale)));
    }
}
