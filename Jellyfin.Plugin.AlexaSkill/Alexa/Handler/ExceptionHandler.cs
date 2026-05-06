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
    /// Log the occured exception and notify user with a category-appropriate message.
    /// </summary>
    /// <param name="request">The skill intent request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>Notification about an error.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        SystemExceptionRequest exceptionRequest = (SystemExceptionRequest)request;
        string locale = GetLocale(request);

        ErrorCategory category = ErrorClassifier.ClassifyAlexaError(exceptionRequest.Error.Type.ToString());
        string localeKey = ErrorCategoryInfo.LocaleKey(category);
        LogLevel logLevel = ErrorCategoryInfo.LogLevel(category);

        Logger.Log(logLevel,
            "Alexa error: {ErrorType} category={Category} - {ErrorMessage} [RequestId={RequestId}, DeviceId={DeviceId}]",
            exceptionRequest.Error.Type,
            category,
            exceptionRequest.Error.Message,
            request.RequestId,
            context.System.Device?.DeviceID);

        return ResponseBuilder.Tell(ResponseStrings.Get(localeKey, locale));
    }
}
