using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Response interceptor that records per-intent response time and errors into RequestCounters.
/// </summary>
public class MetricsResponseInterceptor : IResponseInterceptor
{
    private readonly RequestCounters _counters;
    private readonly ILogger _logger;

    public MetricsResponseInterceptor(RequestCounters counters, ILogger<MetricsResponseInterceptor> logger)
    {
        _counters = counters;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        string intent = GetIntentName(context);
        double elapsedMs = (DateTimeOffset.UtcNow - context.StartedAt).TotalMilliseconds;

        _counters.RecordResponseTime(intent, elapsedMs);

        if (context.Response?.Response?.ShouldEndSession == null)
        {
            _logger.LogDebug("Response for {Intent} had no body after {Elapsed:F0}ms", intent, elapsedMs);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Extract the intent name from the request context.
    /// </summary>
    private static string GetIntentName(RequestContext context)
    {
        if (context.SkillRequest is global::Alexa.NET.Request.Type.IntentRequest intentRequest
            && intentRequest.Intent?.Name != null)
        {
            return intentRequest.Intent.Name;
        }

        return context.RequestType;
    }
}
