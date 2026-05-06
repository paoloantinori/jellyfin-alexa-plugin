using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Request interceptor that checks the circuit breaker before allowing handler execution.
/// When the Jellyfin backend is confirmed down, short-circuits with a "server unavailable" response
/// instead of wasting the 8-second Alexa timeout on doomed retries.
/// </summary>
public class CircuitBreakerInterceptor : IRequestInterceptor
{
    private readonly CircuitBreaker _circuitBreaker;
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CircuitBreakerInterceptor"/> class.
    /// </summary>
    /// <param name="circuitBreaker">The circuit breaker instance.</param>
    /// <param name="config">The plugin configuration (for server URL).</param>
    /// <param name="logger">Logger for circuit breaker events.</param>
    public CircuitBreakerInterceptor(CircuitBreaker circuitBreaker, PluginConfiguration config, ILogger<CircuitBreakerInterceptor> logger)
    {
        _circuitBreaker = circuitBreaker;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<bool> ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        // Don't circuit-break system requests (exceptions, launch requests without backend calls)
        if (context.SkillRequest is SystemExceptionRequest or LaunchRequest)
        {
            return Task.FromResult(true);
        }

        string serverUrl = _config.ServerAddress;

        if (!_circuitBreaker.IsRequestAllowed(serverUrl))
        {
            _logger.LogWarning("Circuit breaker OPEN for {ServerUrl} — short-circuiting {RequestType}", serverUrl, context.RequestType);
            string locale = BaseHandler.GetLocalePublic(context.SkillRequest);
            context.Response = ResponseBuilder.Tell(ResponseStrings.Get("ServerUnavailable", locale));
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}
