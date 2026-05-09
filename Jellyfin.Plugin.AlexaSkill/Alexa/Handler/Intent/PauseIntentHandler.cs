using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.PauseIntent, AMAZON.StopIntent and AMAZON.CancelIntent intents and pause directive.
/// </summary>
public class PauseIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PauseIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public PauseIntentHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && ((string.Equals(intentRequest.Intent.Name, IntentNames.AmazonPause, System.StringComparison.Ordinal) ||
            string.Equals(intentRequest.Intent.Name, IntentNames.AmazonStop, System.StringComparison.Ordinal)) ||
            string.Equals(intentRequest.Intent.Name, IntentNames.AmazonCancel, System.StringComparison.Ordinal))) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Pause);
    }

    /// <summary>
    /// Pause or stop currently playing media.
    /// For Stop/Cancel: the device has already stopped audio locally, return an empty response.
    /// For Pause: send an AudioPlayer.Stop directive to stop the stream.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (request is IntentRequest ir &&
            (string.Equals(ir.Intent.Name, IntentNames.AmazonStop, System.StringComparison.Ordinal) ||
             string.Equals(ir.Intent.Name, IntentNames.AmazonCancel, System.StringComparison.Ordinal)))
        {
            return Task.FromResult(ResponseBuilder.Empty());
        }

        return Task.FromResult(ResponseBuilder.AudioPlayerStop());
    }
}
