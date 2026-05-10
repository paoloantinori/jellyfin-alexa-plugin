using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SessionEndedRequest events.
/// </summary>
#pragma warning disable CA1711
public class SessionEndedRequestHandler : BaseHandler
#pragma warning restore CA1711
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SessionEndedRequestHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public SessionEndedRequestHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is SessionEndedRequest;
    }

    /// <inheritdoc/>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        SessionEndedRequest sessionEnded = (SessionEndedRequest)request;
        if (sessionEnded.Error != null)
        {
            Logger.LogWarning(
                "Session ended with error: {Reason} - {ErrorType}: {ErrorMessage} [RequestId={RequestId}]",
                sessionEnded.Reason,
                sessionEnded.Error.Type,
                sessionEnded.Error.Message,
                request.RequestId);
        }
        else
        {
            Logger.LogInformation(
                "Session ended: {Reason} [RequestId={RequestId}]",
                sessionEnded.Reason,
                request.RequestId);
        }

        return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
    }
}
