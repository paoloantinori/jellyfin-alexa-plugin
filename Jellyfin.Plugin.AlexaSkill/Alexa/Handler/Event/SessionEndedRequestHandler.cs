using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
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
        // DescribeReason renders a tolerated unknown reason (JF-957) as the raw wire
        // value with its fallback bucket, and a known reason as the member name
        // (byte-identical to Reason.ToString()), so the raw value Amazon actually sent
        // survives in these lines instead of only the Error bucket. The Error.Type arg
        // gets the same treatment through DescribeWireType: a session ended with an
        // unknown error.type (JF-931) would otherwise print InternalError here while
        // the PlaybackFailed sibling line renders the raw value. This is the JF-930
        // shared-formatter fold arriving early for THIS line; the remaining
        // ExceptionHandler adoption stays JF-930's.
        if (sessionEnded.Error != null)
        {
            Logger.LogWarning(
                "Session ended with error: {Reason} - {ErrorType}: {ErrorMessage} [RequestId={RequestId}]",
                UnknownWireTypeReason.DescribeReason(sessionEnded),
                UnknownWireTypeError.DescribeWireType(sessionEnded.Error),
                sessionEnded.Error.Message,
                request.RequestId);
        }
        else
        {
            Logger.LogInformation(
                "Session ended: {Reason} [RequestId={RequestId}]",
                UnknownWireTypeReason.DescribeReason(sessionEnded),
                request.RequestId);
        }

        return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
    }
}
