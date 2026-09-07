using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SystemExceptionRequest. Uses <see cref="ErrorClassifier"/>
/// to map Alexa error types to structured categories with appropriate
/// log levels and user-facing locale strings.
/// </summary>
public class ExceptionHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExceptionHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public ExceptionHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        return request is SystemExceptionRequest;
    }

    /// <summary>
    /// Log the occured exception with a category-appropriate level. The response is the
    /// empty keep-alive shape, NOT a Tell: Amazon's AudioPlayer event restrictions apply
    /// to System.ExceptionEncountered too ("Your skill can't return a response"), and the
    /// previous outputSpeech Tell was itself an INVALID_RESPONSE (JF-507, live incident
    /// 2026-09-06 17:12:54 corr=e54b0532: "Qualcosa è andato storto" answered the
    /// ExceptionEncountered caused by the PlaybackFailed one).
    /// </summary>
    /// <param name="request">The skill intent request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The empty keep-alive response Amazon requires for event requests.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        SystemExceptionRequest exceptionRequest = (SystemExceptionRequest)request;

        ErrorCategory category = ErrorClassifier.ClassifyAlexaError(exceptionRequest.Error.Type.ToString());
        LogLevel logLevel = ErrorCategoryInfo.LogLevel(category);

        Logger.Log(
            logLevel,
            "Alexa error: {ErrorType} category={Category} - {ErrorMessage} [RequestId={RequestId}, DeviceId={DeviceId}]",
            exceptionRequest.Error.Type,
            category,
            exceptionRequest.Error.Message,
            request.RequestId,
            context.System.Device?.DeviceID);

        return Task.FromResult<SkillResponse>(BuildKeepAliveResponse());
    }
}
