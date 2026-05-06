using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Request interceptor that assigns a correlation ID and records the pipeline start timestamp.
/// </summary>
public class LoggingRequestInterceptor : IRequestInterceptor
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoggingRequestInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public LoggingRequestInterceptor(ILogger<LoggingRequestInterceptor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<bool> ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        context.StartedAt = DateTimeOffset.UtcNow;
        context.CorrelationId = Guid.NewGuid().ToString("N")[..8];

        _logger.LogInformation(
            "Processing {RequestType} intent={Intent} locale={Locale} corr={CorrelationId}",
            context.RequestType,
            context.IntentName,
            context.Locale,
            context.CorrelationId);

        return Task.FromResult(true);
    }
}

/// <summary>
/// Response interceptor that logs response timing with correlation ID.
/// </summary>
public class LoggingResponseInterceptor : IResponseInterceptor
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoggingResponseInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public LoggingResponseInterceptor(ILogger<LoggingResponseInterceptor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        double elapsedMs = (DateTimeOffset.UtcNow - context.StartedAt).TotalMilliseconds;
        _logger.LogInformation(
            "Completed {RequestType} in {Elapsed}ms corr={CorrelationId}",
            context.RequestType,
            elapsedMs.ToString("F1", CultureInfo.InvariantCulture),
            context.CorrelationId);
        return Task.CompletedTask;
    }
}
