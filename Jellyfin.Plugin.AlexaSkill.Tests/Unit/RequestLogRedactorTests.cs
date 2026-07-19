using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class RequestLogRedactorTests
{
    [Fact]
    public void Redact_MasksAccessToken_ApiAccessToken_AndUserId()
    {
        // JF-312: debug logging of the Alexa request body must not expose the access token,
        // apiAccessToken, or Amazon userId.
        string body = "{\"context\":{\"System\":{\"apiAccessToken\":\"secret-api-token\",\"user\":{\"userId\":\"amzn1.user.123\",\"accessToken\":\"lwa-secret\"}}},\"request\":{\"type\":\"IntentRequest\"}}";

        string redacted = RequestLogRedactor.Redact(body);

        Assert.DoesNotContain("secret-api-token", redacted);
        Assert.DoesNotContain("lwa-secret", redacted);
        Assert.DoesNotContain("amzn1.user.123", redacted);
        Assert.Contains("[REDACTED]", redacted);
        // Non-sensitive fields are preserved.
        Assert.Contains("IntentRequest", redacted);
    }

    [Fact]
    public void Redact_LeavesBodyWithoutSensitiveFields_Unchanged()
    {
        string body = "{\"request\":{\"type\":\"LaunchRequest\"}}";

        Assert.Equal(body, RequestLogRedactor.Redact(body));
    }
}
