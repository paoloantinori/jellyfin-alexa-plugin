using System;
using System.Net.Http;
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

    [Theory]
    [InlineData("INTERNAL_ERROR", ErrorCategory.SkillError)]
    [InlineData("DEVICE_NOT_CONNECTED", ErrorCategory.TransientBackend)]
    [InlineData("ENDPOINT_TIMEOUT", ErrorCategory.Timeout)]
    [InlineData("INVALID_REQUEST", ErrorCategory.UserError)]
    [InlineData("UNKNOWN_TYPE", ErrorCategory.SkillError)]
    [InlineData(null, ErrorCategory.SkillError)]
    public void ClassifyAlexaError_MapsCorrectly(string? errorType, ErrorCategory expected)
    {
        Assert.Equal(expected, ErrorClassifier.ClassifyAlexaError(errorType));
    }

    [Fact]
    public void ErrorCategoryInfo_LocaleKey_ReturnsExpectedKeys()
    {
        Assert.Equal("ServerUnavailable", ErrorCategoryInfo.LocaleKey(ErrorCategory.TransientBackend));
        Assert.Equal("ErrorPermanentBackend", ErrorCategoryInfo.LocaleKey(ErrorCategory.PermanentBackend));
        Assert.Equal("ErrorUserError", ErrorCategoryInfo.LocaleKey(ErrorCategory.UserError));
        Assert.Equal("ErrorSkillError", ErrorCategoryInfo.LocaleKey(ErrorCategory.SkillError));
        Assert.Equal("ErrorTimeout", ErrorCategoryInfo.LocaleKey(ErrorCategory.Timeout));
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
