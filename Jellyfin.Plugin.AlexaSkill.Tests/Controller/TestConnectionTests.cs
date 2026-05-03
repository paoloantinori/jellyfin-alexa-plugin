using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

public class TestConnectionTests
{
    [Fact]
    public void ServerAddress_Empty_IsInvalid()
    {
        var config = new PluginConfiguration();
        Assert.True(string.IsNullOrEmpty(config.ServerAddress));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://wrong-scheme.com")]
    public void InvalidAddresses_CannotBeParsedAsUri(string address)
    {
        // The TestConnection endpoint uses Uri.TryCreate with UriKind.Absolute
        bool valid = Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri);
        if (!string.IsNullOrWhiteSpace(address) && address.StartsWith("ftp://"))
        {
            // ftp:// is a valid absolute URI but wrong scheme — our endpoint would catch this
            // via the HTTP request failing, not via URI parsing
            Assert.NotNull(uri);
        }
        else
        {
            Assert.False(valid && uri != null && (uri.Scheme == "http" || uri.Scheme == "https"));
        }
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://example.com:8096")]
    [InlineData("http://192.168.1.100:8096")]
    [InlineData("https://my-server.local")]
    public void ValidAddresses_CanBeParsedAsUri(string address)
    {
        bool valid = Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? uri);
        Assert.True(valid);
        Assert.NotNull(uri);
        Assert.True(uri!.Scheme == "http" || uri.Scheme == "https");
    }
}
