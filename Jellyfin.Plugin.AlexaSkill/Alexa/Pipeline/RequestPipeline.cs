using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Microsoft.Extensions.Logging;
using AlexaSession = global::Alexa.NET.Request.Session;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Orchestrates request/response interceptors around handler execution.
/// </summary>
public class RequestPipeline
{
    private readonly IReadOnlyList<IRequestInterceptor> _requestInterceptors;
    private readonly IReadOnlyList<IResponseInterceptor> _responseInterceptors;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestPipeline"/> class.
    /// </summary>
    /// <param name="requestInterceptors">Interceptors to run before the handler.</param>
    /// <param name="responseInterceptors">Interceptors to run after the handler.</param>
    /// <param name="logger">Logger for pipeline diagnostics.</param>
    public RequestPipeline(
        IEnumerable<IRequestInterceptor> requestInterceptors,
        IEnumerable<IResponseInterceptor> responseInterceptors,
        ILogger<RequestPipeline> logger)
    {
        _requestInterceptors = requestInterceptors.ToList().AsReadOnly();
        _responseInterceptors = responseInterceptors.ToList().AsReadOnly();
        _logger = logger;
    }

    /// <summary>
    /// Execute the full pipeline: request interceptors -> handler -> response interceptors.
    /// </summary>
    /// <param name="handler">The handler to execute.</param>
    /// <param name="skillRequest">The skill request.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="alexaSession">The Alexa session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The skill response.</returns>
    public async Task<SkillResponse> ExecuteAsync(
        BaseHandler handler,
        Request skillRequest,
        Context context,
        AlexaSession? alexaSession,
        CancellationToken cancellationToken)
    {
        var requestContext = new RequestContext(skillRequest, context, alexaSession, handler);

        foreach (IRequestInterceptor interceptor in _requestInterceptors)
        {
            bool shouldContinue = await interceptor.ProcessAsync(requestContext, cancellationToken).ConfigureAwait(false);
            if (!shouldContinue)
            {
                _logger.LogDebug("Request interceptor {Interceptor} short-circuited pipeline", interceptor.GetType().Name);
                return requestContext.Response ?? ResponseBuilder.Empty();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        requestContext.Response = await handler.HandleRequestAsync(skillRequest, context, alexaSession, cancellationToken).ConfigureAwait(false);

        // Response interceptors run in reverse registration order (stack unwinding)
        for (int i = _responseInterceptors.Count - 1; i >= 0; i--)
        {
            try
            {
                await _responseInterceptors[i].ProcessAsync(requestContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Response interceptor {Interceptor} failed", _responseInterceptors[i].GetType().Name);
            }
        }

        return requestContext.Response ?? ResponseBuilder.Empty();
    }
}
