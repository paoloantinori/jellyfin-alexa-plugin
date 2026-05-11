using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// Response interceptor that logs the serialized response body at DEBUG level
/// with PII sanitization (strips API access tokens and truncates user/stream tokens).
/// </summary>
public partial class ResponseBodyLoggingInterceptor : IResponseInterceptor
{
    private readonly ILogger _logger;

    public ResponseBodyLoggingInterceptor(ILogger<ResponseBodyLoggingInterceptor> logger)
    {
        _logger = logger;
    }

    public Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (!_logger.IsEnabled(LogLevel.Debug) || context.Response == null)
        {
            return Task.CompletedTask;
        }

        string json = JsonSerializer.Serialize(context.Response);
        string sanitized = Sanitize(json);

        _logger.LogDebug(
            "Response body corr={CorrelationId}: {ResponseBody}",
            context.CorrelationId,
            sanitized);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Strip known PII fields from the JSON response body.
    /// </summary>
    /// <param name="json">The JSON string to sanitize.</param>
    /// <returns>The sanitized JSON string.</returns>
    internal static string Sanitize(string json)
    {
        // Strip apiAccessToken values (appear in session attributes and context)
        string result = ApiAccessTokenRegex().Replace(json, "$1\"***\"");

        // Truncate long GUID-style tokens (stream tokens, user IDs in URLs)
        result = LongTokenRegex().Replace(result, match =>
        {
            string value = match.Groups[2].Value;
            if (value.Length > 12)
            {
                return $"{match.Groups[1].Value}\"{value[..8]}...\"";
            }

            return match.Value;
        });

        return result;
    }

    [GeneratedRegex(@"(""apiAccessToken""\s*:\s*"")([^""]+)("")", RegexOptions.IgnoreCase)]
    private static partial Regex ApiAccessTokenRegex();

    [GeneratedRegex(@"(""token""\s*:\s*"")([^""]+)("")", RegexOptions.IgnoreCase)]
    private static partial Regex LongTokenRegex();
}
