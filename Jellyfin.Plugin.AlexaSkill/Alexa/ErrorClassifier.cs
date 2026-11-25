using System;
using System.Net.Http;
using Alexa.NET.Request.Type;

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
    /// Classifies an Alexa <c>SystemExceptionRequest.Error.Type</c> value, switched on
    /// the <see cref="ErrorType"/> enum the deserializer produces (JF-930; the
    /// pre-fix shape compared Amazon wire strings against the enum member names the
    /// call site passed via ToString, so every arm was dead and every value classified
    /// SkillError through the discard). See
    /// https://developer.amazon.com/en-US/docs/alexa/custom-skills/request-and-response-json-reference.html#system-exception-request.
    /// The enum is the typed surface: a tolerated unknown wire value arrives bucketed to
    /// <see cref="ErrorType.InternalError"/> (JF-931), so unknowns classify SkillError
    /// by an arm that actually fires.
    /// </summary>
    /// <param name="errorType">The Alexa error type enum member.</param>
    /// <returns>The classified error category.</returns>
    public static ErrorCategory ClassifyAlexaError(ErrorType errorType)
    {
        return errorType switch
        {
            // Transient conditions: the device-connectivity family, the
            // service-availability and throttling members, and the media-internal
            // server/device faults. Retry plausibly helps for each, so these log at
            // Warning instead of the SkillError Critical the dead discard arm
            // produced. These are the existing members of the classes the pre-JF-930
            // TransientBackend intent (DEVICE_NOT_CONNECTED, itself absent from the
            // enum) covered; the media family's unknown and invalid-shape members
            // (MediaErrorUnknown, InvalidMediaRequest) stay in the discard below.
            ErrorType.DeviceCommunicationError
                or ErrorType.EndpointUnreachable
                or ErrorType.BridgeUnreachable
                or ErrorType.EndpointBusy
                or ErrorType.MediaServiceUnavailable
                or ErrorType.InternalServiceError
                or ErrorType.InternalServerError
                or ErrorType.InternalDeviceError
                or ErrorType.RateLimitExceeded => ErrorCategory.TransientBackend,

            // SkillError, the class the pre-JF-930 switch intended for INTERNAL_ERROR
            // and the invalid-shape family, plus every member with no better-fitting
            // category (the device-capability, authorization-credential, and
            // value-range members). InternalError is also the JF-931 tolerance bucket,
            // so tolerated unknowns classify here by an arm that actually fires.
            // ENDPOINT_TIMEOUT and INVALID_REQUEST, the dead switch's other two
            // intents, have no enum members: Timeout and UserError stay unreachable
            // for Alexa errors (the exception path's OperationCanceledException arm
            // remains Timeout's only source), and ErrorClassifierTests sweeps every
            // member to keep that honest.
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
