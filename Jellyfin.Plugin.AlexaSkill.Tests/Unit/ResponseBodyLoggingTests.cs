using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class ResponseBodyLoggingTests
{
    [Fact]
    public void Sanitize_StripsApiAccessToken()
    {
        string json = """{"apiAccessToken":"secret-token-123","response":{}}""";

        string result = ResponseBodyLoggingInterceptor.Sanitize(json);

        Assert.Contains("\"***\"", result);
        Assert.DoesNotContain("secret-token-123", result);
    }

    [Fact]
    public void Sanitize_TruncatesLongTokenValues()
    {
        string json = """{"token":"abcdefghijklmnop","other":"short"}""";

        string result = ResponseBodyLoggingInterceptor.Sanitize(json);

        Assert.Contains("abcdefgh...", result);
        Assert.DoesNotContain("abcdefghijklmnop", result);
    }

    [Fact]
    public void Sanitize_KeepsShortTokenValues()
    {
        string json = """{"token":"short12","other":"val"}""";

        string result = ResponseBodyLoggingInterceptor.Sanitize(json);

        Assert.Contains("short12", result);
    }

    [Fact]
    public void Sanitize_ChainsBothRegexes()
    {
        string json = """{"apiAccessToken":"secret-token-123","token":"abcdefghijklmnop"}""";

        string result = ResponseBodyLoggingInterceptor.Sanitize(json);

        Assert.Contains("\"***\"", result);
        Assert.Contains("abcdefgh...", result);
        Assert.DoesNotContain("secret-token-123", result);
        Assert.DoesNotContain("abcdefghijklmnop", result);
    }

    [Fact]
    public void Sanitize_NoPII_ReturnsUnchanged()
    {
        string json = """{"response":{"outputSpeech":{"text":"Hello"}}}""";

        string result = ResponseBodyLoggingInterceptor.Sanitize(json);

        Assert.Equal(json, result);
    }
}
