using System;
using System.Net.Http;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Classifies exceptions into <see cref="ErrorCategory"/> values
/// for structured logging and user-facing responses.
/// </summary>
public static class ErrorClassifier
{
    /// <summary>
    /// Classifies a .NET exception into an error category.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <returns>The classified error category.</returns>
    public static ErrorCategory Classify(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return ErrorCategory.Timeout;

            case TimeoutException:
                return ErrorCategory.TransientBackend;

            case HttpRequestException httpEx:
                return ClassifyHttpException(httpEx);

            case ArgumentException:
            case FormatException:
                return ErrorCategory.UserError;

            default:
                return ErrorCategory.SkillError;
        }
    }

    /// <summary>
    /// Classifies an Alexa <c>SystemExceptionRequest.Error.Type</c> value.
    /// See https://developer.amazon.com/en-US/docs/alexa/custom-skills/request-and-response-json-reference.html#system-exception-request.
    /// </summary>
    /// <param name="errorType">The Alexa error type string.</param>
    /// <returns>The classified error category.</returns>
    public static ErrorCategory ClassifyAlexaError(string? errorType)
    {
        return errorType switch
        {
            "INTERNAL_ERROR" => ErrorCategory.SkillError,
            "DEVICE_NOT_CONNECTED" => ErrorCategory.TransientBackend,
            "ENDPOINT_TIMEOUT" => ErrorCategory.Timeout,
            "INVALID_REQUEST" => ErrorCategory.UserError,
            _ => ErrorCategory.SkillError
        };
    }

    private static ErrorCategory ClassifyHttpException(HttpRequestException httpEx)
    {
        if (httpEx.StatusCode is System.Net.HttpStatusCode statusCode)
        {
            if ((int)statusCode >= 500)
            {
                return ErrorCategory.TransientBackend;
            }

            if (statusCode == System.Net.HttpStatusCode.Unauthorized
                || statusCode == System.Net.HttpStatusCode.Forbidden
                || statusCode == System.Net.HttpStatusCode.NotFound)
            {
                return ErrorCategory.PermanentBackend;
            }
        }

        return ErrorCategory.TransientBackend;
    }
}
