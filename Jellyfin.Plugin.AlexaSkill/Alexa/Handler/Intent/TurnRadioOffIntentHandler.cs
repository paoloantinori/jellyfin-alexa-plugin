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
/// Handler for TurnRadioOffIntent requests.
/// Disables radio mode so playback stops when the queue runs out.
/// </summary>
public class TurnRadioOffIntentHandler : BaseHandler
{
    public TurnRadioOffIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.TurnRadioOff, StringComparison.Ordinal);
    }

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.RadioModeEnabled, request) is { } disabled)
        {
            return Task.FromResult(disabled);
        }

        string locale = GetLocale(request);
        Logger.LogDebug("TurnRadioOff: entered, userId={UserId}, deviceId={DeviceId}", session.UserId, context.System.Device.DeviceID);
        RadioModeState.Disable(session.UserId, context.System.Device.DeviceID);
        Logger.LogInformation("Radio mode disabled for user {UserId}", session.UserId);
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("RadioModeOff", locale)));
    }
}
