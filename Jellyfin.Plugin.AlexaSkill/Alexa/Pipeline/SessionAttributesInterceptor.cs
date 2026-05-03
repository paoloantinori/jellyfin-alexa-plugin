using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Request;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Response interceptor that preserves Alexa session attributes across requests.
/// Ensures disambiguation and other session state is carried forward in the response.
/// </summary>
public class SessionAttributesInterceptor : IResponseInterceptor
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionAttributesInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public SessionAttributesInterceptor(ILogger<SessionAttributesInterceptor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (context.Response?.Response == null || context.AlexaSession?.Attributes == null || context.AlexaSession.Attributes.Count == 0)
        {
            return Task.CompletedTask;
        }

        Dictionary<string, object> incomingAttributes = context.AlexaSession.Attributes;
        context.Response.SessionAttributes ??= new Dictionary<string, object>();

        foreach (KeyValuePair<string, object> attr in incomingAttributes)
        {
            if (!context.Response.SessionAttributes.ContainsKey(attr.Key))
            {
                context.Response.SessionAttributes[attr.Key] = attr.Value;
            }
        }

        return Task.CompletedTask;
    }
}
