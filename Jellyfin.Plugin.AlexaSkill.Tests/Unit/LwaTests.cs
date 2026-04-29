using Jellyfin.Plugin.AlexaSkill.Lwa;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class DeviceAuthorizationRequestTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var request = new DeviceAuthorizationRequest(
            "user-code", "device-code", "https://example.com", 12345L, 5);

        Assert.Equal("user-code", request.UserCode);
        Assert.Equal("device-code", request.DeviceCode);
        Assert.Equal("https://example.com", request.VerificationUri);
        Assert.Equal(12345L, request.ExpireTimestamp);
        Assert.Equal(5, request.Interval);
    }
}

public class DeviceTokenTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var token = new DeviceToken("access-token", "refresh-token", "Bearer", 12345L);

        Assert.Equal("access-token", token.AccessToken);
        Assert.Equal("refresh-token", token.RefreshToken);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(12345L, token.ExpireTimestamp);
    }
}

public class ScopeTests
{
    [Theory]
    [InlineData(Scope.SkillsRead, "alexa::ask:skills:read")]
    [InlineData(Scope.SkillsReadWrite, "alexa::ask:skills:readwrite")]
    [InlineData(Scope.ModelsRead, "alexa::ask:models:read")]
    [InlineData(Scope.ModelsReadWrite, "alexa::ask:models:readwrite")]
    public void ScopeToString_ReturnsCorrectString(Scope scope, string expected)
    {
        Assert.Equal(expected, ScopeMethods.ScopeToString(scope));
    }
}
