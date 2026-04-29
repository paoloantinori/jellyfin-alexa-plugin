using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class CsrfTokenTests
{
    [Fact]
    public void Token_HasExpectedLength()
    {
        var token = new CsrfToken();
        var bytes = Convert.FromBase64String(token.Token);
        Assert.Equal(Config.CsrfTokenLength, bytes.Length);
    }

    [Fact]
    public void Expiration_IsInFuture()
    {
        var before = DateTime.UtcNow.AddMinutes(Config.CsrfTokenExpirationMinutes).AddSeconds(-1);
        var token = new CsrfToken();
        var after = DateTime.UtcNow.AddMinutes(Config.CsrfTokenExpirationMinutes).AddSeconds(1);

        Assert.True(token.Expiration > before);
        Assert.True(token.Expiration < after);
    }

    [Fact]
    public void Token_IsUnique()
    {
        var token1 = new CsrfToken();
        var token2 = new CsrfToken();
        Assert.NotEqual(token1.Token, token2.Token);
    }
}
