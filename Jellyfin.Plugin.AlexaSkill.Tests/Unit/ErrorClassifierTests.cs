using System;
using System.Net.Http;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class ErrorClassifierTests
{
    [Fact]
    public void Classify_OperationCanceledException_ReturnsTimeout()
    {
        var ex = new OperationCanceledException();
        Assert.Equal(ErrorCategory.Timeout, ErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_TimeoutException_ReturnsTransientBackend()
    {
        var ex = new TimeoutException();
        Assert.Equal(ErrorCategory.TransientBackend, ErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_ArgumentException_ReturnsUserError()
    {
        Assert.Equal(ErrorCategory.UserError, ErrorClassifier.Classify(new ArgumentException()));
        Assert.Equal(ErrorCategory.UserError, ErrorClassifier.Classify(new ArgumentNullException()));
        Assert.Equal(ErrorCategory.UserError, ErrorClassifier.Classify(new FormatException()));
    }

    [Fact]
    public void Classify_HttpRequestException_5xx_ReturnsTransientBackend()
    {
        var ex = new HttpRequestException("server error", null, System.Net.HttpStatusCode.ServiceUnavailable);
        Assert.Equal(ErrorCategory.TransientBackend, ErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_HttpRequestException_404_ReturnsPermanentBackend()
    {
        var ex = new HttpRequestException("not found", null, System.Net.HttpStatusCode.NotFound);
        Assert.Equal(ErrorCategory.PermanentBackend, ErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_HttpRequestException_401_ReturnsPermanentBackend()
    {
        var ex = new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
        Assert.Equal(ErrorCategory.PermanentBackend, ErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_HttpRequestException_403_ReturnsPermanentBackend()
    {
        var ex = new HttpRequestException("forbidden", null, System.Net.HttpStatusCode.Forbidden);
        Assert.Equal(ErrorCategory.PermanentBackend, ErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_HttpRequestException_NoStatus_ReturnsTransientBackend()
    {
        var ex = new HttpRequestException("connection refused");
        Assert.Equal(ErrorCategory.TransientBackend, ErrorClassifier.Classify(ex));
    }

    [Fact]
    public void Classify_UnknownException_ReturnsSkillError()
    {
        var ex = new InvalidOperationException("something unexpected");
        Assert.Equal(ErrorCategory.SkillError, ErrorClassifier.Classify(ex));
    }

    // The pre-JF-930 classifier compared Amazon wire strings but received the ErrorType
    // enum member names its call site produced, so every arm was dead and every Alexa
    // error classified SkillError through the discard. These theories switch on the
    // enum members the deserializer actually produces; the mapping rationale and
    // provenance live on ClassifyAlexaError's doc and arm comments.

    [Theory]
    [InlineData(ErrorType.DeviceCommunicationError)]
    [InlineData(ErrorType.EndpointUnreachable)]
    [InlineData(ErrorType.BridgeUnreachable)]
    [InlineData(ErrorType.EndpointBusy)]
    [InlineData(ErrorType.MediaServiceUnavailable)]
    [InlineData(ErrorType.InternalServiceError)]
    [InlineData(ErrorType.InternalServerError)]
    [InlineData(ErrorType.InternalDeviceError)]
    [InlineData(ErrorType.RateLimitExceeded)]
    public void ClassifyAlexaError_TransientFamily_ReturnsTransientBackend(ErrorType errorType)
    {
        // Every member here previously fell through the discard arm and logged
        // Critical: they are the existing members of the transient classes the dead
        // switch's TransientBackend intent covered.
        Assert.Equal(ErrorCategory.TransientBackend, ErrorClassifier.ClassifyAlexaError(errorType));
    }

    [Theory]
    [InlineData(ErrorType.InternalError)] // also the JF-931 tolerance bucket
    [InlineData(ErrorType.InvalidResponse)] // skill produced a malformed response
    [InlineData(ErrorType.InvalidDirective)]
    [InlineData(ErrorType.InvalidMediaRequest)]
    [InlineData(ErrorType.MediaErrorUnknown)]
    [InlineData(ErrorType.HardwareMalfunction)]
    [InlineData(ErrorType.InsufficientPermissions)]
    public void ClassifyAlexaError_SkillErrorClass_ReturnsSkillError(ErrorType errorType)
    {
        // SkillError-class members: a fault in the skill's own request/response content,
        // a condition with no better-fitting category, and the tolerated-unknown bucket
        // (an UnknownWireTypeError arrives as InternalError, so unknowns classify
        // SkillError by an arm that actually fires).
        Assert.Equal(ErrorCategory.SkillError, ErrorClassifier.ClassifyAlexaError(errorType));
    }

    [Fact]
    public void ClassifyAlexaError_UndefinedEnumValue_ReturnsSkillError()
    {
        // The SkillError discard stays total: a numeric value outside the pinned enum
        // (only constructible in code, never on the wire, since an unknown wire value
        // rides the JF-931 tolerance bucket to InternalError) still classifies
        // SkillError.
        ErrorType undefined = (ErrorType)999;
        Assert.Equal(ErrorCategory.SkillError, ErrorClassifier.ClassifyAlexaError(undefined));
    }

    [Fact]
    public void ClassifyAlexaError_NoEnumMember_ReachesTimeoutOrUserError()
    {
        // Pins the mapping's documented unreachability: ENDPOINT_TIMEOUT and
        // INVALID_REQUEST, the dead switch's Timeout/UserError intents, have no
        // ErrorType members in the pinned Alexa.NET 1.22.0, so no member maps there.
        // When Amazon adds a timeout-shaped error.type and Alexa.NET grows the enum,
        // this test fails and forces the new member's mapping to be decided
        // deliberately instead of silently riding the SkillError discard.
        foreach (ErrorType member in Enum.GetValues<ErrorType>())
        {
            ErrorCategory category = ErrorClassifier.ClassifyAlexaError(member);
            Assert.True(
                category != ErrorCategory.Timeout && category != ErrorCategory.UserError,
                $"{member} classified {category}; a new Alexa error.type member must be mapped deliberately, not left to the discard");
        }
    }

    [Fact]
    public void ErrorCategoryInfo_LogLevel_MapsCorrectly()
    {
        Assert.Equal(LogLevel.Warning, ErrorCategoryInfo.LogLevel(ErrorCategory.TransientBackend));
        Assert.Equal(LogLevel.Error, ErrorCategoryInfo.LogLevel(ErrorCategory.PermanentBackend));
        Assert.Equal(LogLevel.Information, ErrorCategoryInfo.LogLevel(ErrorCategory.UserError));
        Assert.Equal(LogLevel.Critical, ErrorCategoryInfo.LogLevel(ErrorCategory.SkillError));
        Assert.Equal(LogLevel.Warning, ErrorCategoryInfo.LogLevel(ErrorCategory.Timeout));
    }
}
