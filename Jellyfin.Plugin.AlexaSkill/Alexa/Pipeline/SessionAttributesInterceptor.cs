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

        // JF-387: do not carry session attributes onto a response that ends the session.
        // They are semantically dead (the session dies and Amazon drops them), and they
        // made the play response from an interactive FindSong flow differ from the
        // byte-equivalent one-shot play response (which carries no attributes because its
        // session was new). "alexa stop" after the interactive-path play was claimed by
        // the device instead of routing PauseIntent to the skill, while the one-shot path
        // worked; removing the one observable difference between the two responses.
        // Multi-turn responses (shouldEndSession=false/null, e.g. elicitations) still
        // preserve attributes, which is the interceptor's purpose.
        if (context.Response.Response.ShouldEndSession == true)
        {
            // Defensive: never let the removal marker reach Amazon even if a future
            // call site marks a session-ending response (all current sites are Asks).
            context.Response.SessionAttributes?.Remove(SessionAttributeRemoval.MarkerKey);
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

        // JF-394: honor the removal marker. Keys listed under __remove_attributes are
        // stripped from the merged output (and the marker itself never reaches Alexa),
        // so a concluded flow (e.g. resume declined) does not leave stale state riding
        // along for a later stray "yes" to act on.
        if (context.Response.SessionAttributes.TryGetValue(SessionAttributeRemoval.MarkerKey, out object? marker)
            && marker is IEnumerable<object> removals)
        {
            foreach (object? key in removals)
            {
                if (key is string keyName)
                {
                    context.Response.SessionAttributes.Remove(keyName);
                }
            }

            context.Response.SessionAttributes.Remove(SessionAttributeRemoval.MarkerKey);
        }

        return Task.CompletedTask;
    }
}
