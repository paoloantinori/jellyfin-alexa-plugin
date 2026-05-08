using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class ConfigTests
{
    [Fact]
    public void SkillName_IsJellyfin()
    {
        Assert.Equal("Jellyfin", Config.SkillName);
    }

    [Fact]
    public void InvocationName_IsJellyFin()
    {
        Assert.Equal("jellyfin player", Config.InvocationName);
    }

    [Fact]
    public void CsrfTokenLength_IsPositive()
    {
        Assert.True(Config.CsrfTokenLength > 0);
    }

    [Fact]
    public void CsrfTokenExpirationMinutes_IsPositive()
    {
        Assert.True(Config.CsrfTokenExpirationMinutes > 0);
    }

    [Fact]
    public void LwaAuthorizePageTokenLength_IsPositive()
    {
        Assert.True(Config.LwaAuthorizePageTokenLength > 0);
    }

    [Fact]
    public void LwaAuthorizePageTokenExpirationMinutes_IsPositive()
    {
        Assert.True(Config.LwaAuthorizePageTokenExpirationMinutes > 0);
    }

    [Fact]
    public void DbFilePath_IsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(Config.DbFilePath));
    }

    [Fact]
    public void ValidRedirectUrls_ContainsExpectedDomains()
    {
        Assert.Equal(3, Config.ValidRedirectUrls.Length);
        Assert.Contains(Config.ValidRedirectUrls, u => u.Contains("amazon.co.jp"));
        Assert.Contains(Config.ValidRedirectUrls, u => u.Contains("layla.amazon.com"));
        Assert.Contains(Config.ValidRedirectUrls, u => u.Contains("pitangui.amazon.com"));
    }

    [Fact]
    public void ValidRedirectUrls_AllStartWithHttps()
    {
        Assert.All(Config.ValidRedirectUrls, url => Assert.StartsWith("https://", url));
    }
}
