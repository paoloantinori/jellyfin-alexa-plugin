using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class RequestLogRedactorTests
{
    [Fact]
    public void Redact_MasksAccessTokens_ConsentToken_AndUserId()
    {
        // JF-312: debug logging of the Alexa request body must not expose the access token,
        // apiAccessToken, consentToken, or Amazon userId.
        string body = "{\"context\":{\"System\":{\"apiAccessToken\":\"secret-api-token\",\"permissions\":{\"consentToken\":\"consent-secret\"},\"user\":{\"userId\":\"amzn1.user.123\",\"accessToken\":\"lwa-secret\"}}},\"request\":{\"type\":\"IntentRequest\"}}";

        string redacted = RequestLogRedactor.Redact(body);

        Assert.DoesNotContain("secret-api-token", redacted);
        Assert.DoesNotContain("lwa-secret", redacted);
        Assert.DoesNotContain("consent-secret", redacted);
        Assert.DoesNotContain("amzn1.user.123", redacted);
        Assert.Contains("[REDACTED]", redacted);
        // Non-sensitive fields are preserved.
        Assert.Contains("IntentRequest", redacted);
    }

    [Fact]
    public void Redact_HandlesEscapedQuoteInValue()
    {
        // A value with an escaped quote must be fully redacted -- the value pattern consumes
        // JSON escapes so it can't truncate at the backslash-quote and leak the tail.
        string body = @"{""accessToken"":""abc\""def""}";

        string redacted = RequestLogRedactor.Redact(body);

        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("def", redacted);
    }

    [Fact]
    public void Redact_LeavesBodyWithoutSensitiveFields_Unchanged()
    {
        string body = "{\"request\":{\"type\":\"LaunchRequest\"}}";

        Assert.Equal(body, RequestLogRedactor.Redact(body));
    }

    [Fact]
    public void RedactUrl_MasksApiKey()
    {
        // JF-312: stream URLs carry the Jellyfin token as an api_key query param -- mask it.
        string url = "https://jellyfin.example.com/Audio/abc/stream?static=true&api_key=da5a12a496f74e9ea546c2db8393aad2";

        string redacted = RequestLogRedactor.RedactUrl(url);

        Assert.DoesNotContain("da5a12a496f74e9ea546c2db8393aad2", redacted);
        Assert.Contains("api_key=[REDACTED]", redacted);
        Assert.Contains("/Audio/abc/stream", redacted);
    }

    [Fact]
    public void RedactUrl_LeavesUrlWithoutApiKey_Unchanged()
    {
        string url = "https://jellyfin.example.com/Videos/abc/master.m3u8";

        Assert.Equal(url, RequestLogRedactor.RedactUrl(url));
    }
}
